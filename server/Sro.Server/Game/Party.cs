using Sro.Server.Net;

namespace Sro.Server.Game;

/// <summary>Группа игроков (GDD §37): лидер и участники по порядку вступления.</summary>
public sealed class Party
{
    public int LeaderId { get; internal set; }
    public List<int> Members { get; } = [];
}

/// <summary>
/// Все группы галактики и приглашения в них. Группа живёт поверх систем: участники могут быть где угодно.
/// Только логика — сообщения игрокам рассылает <see cref="Galaxy"/>. Не сохраняется: перезапуск сервера группы распускает.
/// </summary>
public sealed class PartyBook
{
    private readonly Dictionary<int, Party> _byPlayer = [];
    private readonly List<Invitation> _invites = [];

    /// <summary>Приглашение From → To ждёт ответа до тика Until.</summary>
    public readonly record struct Invitation(int From, int To, long Until);

    public Party? PartyOf(int id) => _byPlayer.GetValueOrDefault(id);

    public bool SameParty(int a, int b) => a != b && _byPlayer.TryGetValue(a, out var party) && _byPlayer.GetValueOrDefault(b) == party;

    /// <summary>Все группы — каждая один раз.</summary>
    public IEnumerable<Party> Parties => _byPlayer.Values.Distinct();

    public IReadOnlyList<Invitation> Invites => _invites;

    /// <summary>Пригласить to в свою группу (или создать её, когда to согласится).</summary>
    /// <returns>Код отказа (<see cref="PartyCodes"/>) или null — приглашение ушло.</returns>
    public string? Invite(int from, int to, long tick, int maxSize, int inviteTicks)
    {
        if (from == to) return PartyCodes.Gone;
        if (_byPlayer.ContainsKey(to)) return PartyCodes.Busy;
        if (_byPlayer.GetValueOrDefault(from) is { } party && party.Members.Count >= maxSize) return PartyCodes.Full;
        // Одно приглашение на пару: повтор продлевает срок.
        _invites.RemoveAll(i => i.From == from && i.To == to);
        _invites.Add(new Invitation(from, to, tick + inviteTicks));
        return null;
    }

    /// <summary>to принимает приглашение from: вступает в группу from (её создаёт, если не было).</summary>
    /// <returns>Код отказа или null — to в группе.</returns>
    public string? Accept(int to, int from, long tick, int maxSize)
    {
        var index = _invites.FindIndex(i => i.From == from && i.To == to && i.Until > tick);
        if (index < 0) return PartyCodes.Expired;
        _invites.RemoveAt(index);
        if (_byPlayer.ContainsKey(to)) return PartyCodes.Busy;
        if (!_byPlayer.TryGetValue(from, out var party))
        {
            party = new Party { LeaderId = from };
            party.Members.Add(from);
            _byPlayer[from] = party;
        }
        if (party.Members.Count >= maxSize) return PartyCodes.Full;
        party.Members.Add(to);
        _byPlayer[to] = party;
        // Вступил — остальные приглашения ему больше не нужны.
        _invites.RemoveAll(i => i.To == to);
        return null;
    }

    /// <returns>true — такое приглашение было.</returns>
    public bool Decline(int to, int from) => _invites.RemoveAll(i => i.From == from && i.To == to) > 0;

    /// <summary>
    /// Игрок уходит из группы (или из игры): лидерство переходит к следующему; один оставшийся — группы больше нет.
    /// Его приглашения, отправленные и полученные, пропадают.
    /// </summary>
    /// <returns>Группа, из которой он ушёл (её оставшиеся участники ещё в Members, даже если она распалась), или null.</returns>
    public Party? Leave(int id)
    {
        _invites.RemoveAll(i => i.From == id || i.To == id);
        if (!_byPlayer.Remove(id, out var party)) return null;
        party.Members.Remove(id);
        if (party.LeaderId == id && party.Members.Count > 0) party.LeaderId = party.Members[0];
        if (party.Members.Count == 1) _byPlayer.Remove(party.Members[0]);
        return party;
    }

    /// <summary>Истёкшие приглашения — убираются и возвращаются, чтобы сказать пригласившему.</summary>
    public List<Invitation> Expire(long tick)
    {
        var expired = _invites.Where(i => i.Until <= tick).ToList();
        if (expired.Count > 0) _invites.RemoveAll(i => i.Until <= tick);
        return expired;
    }

    /// <summary>Группа ещё существует: в ней хотя бы двое.</summary>
    public bool Alive(Party party) => party.Members.Count > 1 && _byPlayer.GetValueOrDefault(party.Members[0]) == party;
}
