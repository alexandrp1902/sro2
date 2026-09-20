using System.Diagnostics;
using Sro.Server.Accounts;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>Что комната системы знает о галактике вокруг неё.</summary>
public interface IRoomHost
{
    /// <summary>Имя занято игроком или NPC в любой системе.</summary>
    bool IsNameTaken(string name, Player? self);

    /// <summary>Пилотов на связи во всей галактике.</summary>
    int OnlineTotal { get; }

    /// <summary>
    /// Корабль уходит в систему to — гиперпрыжком (jump) или домой после гибели. Переводит галактика, сразу после шага
    /// комнаты: посреди шага состав её кораблей меняться не должен.
    /// </summary>
    void Depart(Room from, Player player, string to, bool jump);

    /// <summary>a и b в одной группе (GDD §37): друг по другу не стреляют.</summary>
    bool SameParty(int a, int b);

    /// <summary>Участники группы пилота id, включая его самого; null — он не в группе.</summary>
    IReadOnlyList<int>? PartyMembers(int id);

    /// <summary>Пилот ушёл из игры насовсем: из группы — тоже.</summary>
    void Gone(Player player);

    /// <summary>Пилот нанёс урон пиратам вторжения id (GDD §38).</summary>
    void Contributed(Player player, int invasionId, double damage);

    /// <summary>
    /// Цены станций остальных систем (M12): по ним торговец в доке рассказывает, где что берут.
    /// Читается с потока тика, как и всё остальное, — комнаты живут в одном потоке.
    /// </summary>
    IReadOnlyList<StationPrices> MarketsExcept(string system);
}

/// <summary>
/// Галактика (GDD §4, §63–64): комнаты всех систем в одном процессе и один поток тика на всех, поэтому
/// гиперпрыжок — синхронный перевод игрока из комнаты в комнату, без гонок. Сессии, ник и ключ возврата
/// ищутся по всей галактике: корабль, ждущий после обрыва связи, находится, в какой бы системе он ни был.
/// Вызывается только из потока тика (<see cref="GalaxyHost"/>) — поэтому тестируется напрямую, как <see cref="Room"/>.
/// </summary>
public sealed class Galaxy : IRoomHost
{
    private readonly ILogger _log;
    private readonly AccountStore? _accounts;
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Room> _byConnection = [];
    private readonly List<(Room From, Player Player, string To, bool Jump)> _departures = [];
    private readonly double[] _stepMs;
    private readonly PartyBook _parties = new();
    private readonly InvasionDirector _invasion;
    private int _nextId;
    private int _lastTotal;

    /// <param name="roll">Броски попадания для всех комнат; тесты подставляют своё.</param>
    /// <param name="orbitEpoch">Орбитальное время в тик 0, секунды; null — сейчас по unix-часам.</param>
    public Galaxy(
        Balance balance,
        ILogger log,
        AccountStore? accounts = null,
        Func<double>? roll = null,
        Func<int, Random>? random = null,
        double? orbitEpoch = null)
    {
        Balance = balance;
        _log = log;
        _accounts = accounts;
        var seed = 0;
        var epoch = orbitEpoch ?? Room.Now();
        foreach (var id in balance.Galaxy.SystemMap.Keys)
        {
            // У каждой комнаты свои генераторы: общий Random.Shared из одного потока годится, но тестам нужен сид.
            Random? Next() => random?.Invoke(seed++);
            _rooms[id] = new Room(balance.ForSystem(id), log, roll, Next(), Next(), Next(), Next(), accounts, this, () => ++_nextId, epoch);
        }
        _stepMs = new double[_rooms.Count];
        _invasion = new InvasionDirector(log, random?.Invoke(seed++));
    }

    /// <summary>Группы галактики.</summary>
    public PartyBook Parties => _parties;

    /// <summary>Вторжения пиратов.</summary>
    public InvasionDirector Invasion => _invasion;

    public Balance Balance { get; private set; }

    public IReadOnlyCollection<Room> Rooms => _rooms.Values;

    public Room this[string system] => _rooms[system];

    /// <summary>Тик галактики: комнаты шагают вместе, у всех он один.</summary>
    public long Tick => _rooms.Values.First().Tick;

    public int OnlineTotal => _rooms.Values.Sum(r => r.OnlineCount);

    /// <summary>Время шага каждой комнаты в последнем тике, мс, в порядке <see cref="Rooms"/>.</summary>
    public IReadOnlyList<double> StepMs => _stepMs;

    /// <summary>Комната, где сейчас корабль этого соединения.</summary>
    public Room? RoomOf(IClientConnection connection) => _byConnection.GetValueOrDefault(connection.Id);

    /// <summary>Гость: в системе, где ждёт его корабль, иначе — в стартовой.</summary>
    public void Join(IClientConnection connection, string? token, string? name, string? hull, string? weapon = null)
    {
        if (_byConnection.ContainsKey(connection.Id)) return;
        var room = _rooms.Values.FirstOrDefault(r => r.HasGuest(token)) ?? Start;
        room.Join(connection, token, name, hull, weapon);
        _byConnection[connection.Id] = room;
        Joined(room, connection);
    }

    /// <summary>
    /// Пилот с аккаунтом: к кораблю, который ещё в игре (ждёт после обрыва или летает с другого устройства), —
    /// где бы тот ни был; иначе — к станции системы, где пилот стыковался последний раз.
    /// </summary>
    public void JoinAccount(IClientConnection connection, string accountId, string name)
    {
        if (_byConnection.ContainsKey(connection.Id)) return;
        var room = _rooms.Values.FirstOrDefault(r => r.HasToken(accountId)) ?? Home(_accounts?.Profile(accountId)?.System);
        room.JoinAccount(connection, accountId, name);
        _byConnection[connection.Id] = room;
        Joined(room, connection);
    }

    /// <summary>Вошедшему (или вернувшемуся) — идущее вторжение и своя группа сразу, не дожидаясь рассылки.</summary>
    private void Joined(Room room, IClientConnection connection)
    {
        if (room.PlayerOf(connection) is not { } player) return;
        _invasion.SendTo(player, this);
        if (_parties.PartyOf(player.Id) is { } party) SendParty(party);
    }

    public void Disconnect(IClientConnection connection)
    {
        if (_byConnection.Remove(connection.Id, out var room)) room.Disconnect(connection);
    }

    /// <summary>Команда игрока — в комнату, где сейчас его корабль.</summary>
    public void With(IClientConnection connection, Action<Room> command)
    {
        if (_byConnection.TryGetValue(connection.Id, out var room)) command(room);
    }

    /// <summary>Тик всех систем; переходы между ними — сразу после шага комнаты, откуда корабль уходит.</summary>
    public void Step()
    {
        var i = 0;
        foreach (var room in _rooms.Values)
        {
            var started = Stopwatch.GetTimestamp();
            room.Step();
            _stepMs[i++] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (_departures.Count > 0) Transfer();
        }
        _invasion.Step(this, Tick);
        StepParties();

        // «Онлайн» в статусе — по всей галактике: вошли в одной системе — узнают и в остальных.
        var total = OnlineTotal;
        if (total == _lastTotal) return;
        _lastTotal = total;
        foreach (var room in _rooms.Values) room.BroadcastPlayers();
    }

    /// <summary>
    /// Баланс изменился на диске. Каждая комната получает свой вид; набор систем меняется только перезапуском —
    /// корабли в исчезнувшей системе было бы некуда деть.
    /// </summary>
    public void ApplyBalance(Balance balance)
    {
        var systems = balance.Galaxy.SystemMap.Keys;
        if (systems.Count() != _rooms.Count || !systems.All(_rooms.ContainsKey))
        {
            _log.LogWarning("Balance rejected: the set of systems in galaxy.json changed, restart the server to apply it");
            return;
        }
        Balance = balance;
        foreach (var (id, room) in _rooms) room.ApplyBalance(balance.ForSystem(id));
    }

    /// <summary>Пилот по id, где бы он ни был.</summary>
    public (Room Room, Player Player)? FindPilot(int id)
    {
        foreach (var room in _rooms.Values)
        {
            if (room.Pilot(id) is { } player) return (room, player);
        }
        return null;
    }

    public bool SameParty(int a, int b) => _parties.SameParty(a, b);

    public IReadOnlyList<int>? PartyMembers(int id) => _parties.PartyOf(id)?.Members;

    public void Gone(Player player) => LeaveParty(player.Id, player.Name);

    public void Contributed(Player player, int invasionId, double damage) => _invasion.Contributed(player, invasionId, damage);

    /// <summary>Цены станций остальных систем — торговцу в доке на слухи (M12).</summary>
    public IReadOnlyList<StationPrices> MarketsExcept(string system)
    {
        var galaxy = Balance.Galaxy;
        var list = new List<StationPrices>();
        foreach (var (id, room) in _rooms)
        {
            if (id == system || room.Prices() is not { Count: > 0 } prices) continue;
            var hops = MissionRules.Hops(galaxy, system, id);
            if (hops is not { } jumps) continue; // отрезанная система: туда и не долететь
            list.Add(new StationPrices(id, galaxy.System(id)?.Name ?? id, jumps, prices));
        }
        return list;
    }

    /// <summary>
    /// Команда группы (GDD §37): позвать, принять или отклонить приглашение, выйти. Позвать можно пилота где угодно,
    /// но клиент зовёт того, кто у него в цели.
    /// </summary>
    public void Party(IClientConnection connection, string? action, int id)
    {
        if (RoomOf(connection)?.PlayerOf(connection) is not { } player) return;
        var rules = Balance.Party;
        switch (action)
        {
            case PartyCodes.InviteAction:
            {
                if (FindPilot(id) is not { Player: { Connection: { } to } target })
                {
                    Event(player, PartyCodes.Gone);
                    return;
                }
                if (_parties.Invite(player.Id, target.Id, Tick, rules.MaxSize, rules.InviteTicks) is { } problem)
                {
                    Event(player, problem, target.Name);
                    return;
                }
                to.Send(new PartyInviteMsg(player.Id, player.Name, rules.InviteSeconds));
                Event(player, PartyCodes.Invited, target.Name);
                _log.LogInformation("Player {From} invited {To} to a party", player.Id, target.Id);
                return;
            }
            case PartyCodes.AcceptAction:
            {
                if (FindPilot(id) is not { Player: var inviter })
                {
                    _parties.Decline(player.Id, id);
                    Event(player, PartyCodes.Gone);
                    return;
                }
                if (_parties.Accept(player.Id, id, Tick, rules.MaxSize) is { } problem)
                {
                    Event(player, problem, inviter.Name);
                    return;
                }
                var party = _parties.PartyOf(player.Id)!;
                foreach (var member in party.Members)
                {
                    if (FindPilot(member)?.Player is { } other) Event(other, PartyCodes.Joined, other == player ? null : player.Name);
                }
                SendParty(party);
                _log.LogInformation("Player {Id} joined the party of {Leader}, {Count} members", player.Id, party.LeaderId, party.Members.Count);
                return;
            }
            case PartyCodes.DeclineAction:
                if (_parties.Decline(player.Id, id) && FindPilot(id)?.Player is { } host) Event(host, PartyCodes.Declined, player.Name);
                return;
            case PartyCodes.LeaveAction:
                LeaveParty(player.Id, player.Name);
                return;
        }
    }

    /// <summary>Выход из группы: ему — пустая группа, остальным — кто ушёл (или что группы больше нет).</summary>
    private void LeaveParty(int id, string name)
    {
        if (_parties.Leave(id) is not { } party) return;
        if (FindPilot(id)?.Player.Connection is { } connection) connection.Send(new PartyStateMsg(0, []));
        var alive = _parties.Alive(party);
        foreach (var member in party.Members)
        {
            if (FindPilot(member)?.Player is not { } other) continue;
            if (alive) Event(other, PartyCodes.Left, name);
            else
            {
                Event(other, PartyCodes.Disbanded, name);
                other.Connection?.Send(new PartyStateMsg(0, []));
            }
        }
        if (alive) SendParty(party);
        _log.LogInformation("Player {Id} left a party", id);
    }

    /// <summary>Истёкшие приглашения и раз в statusSeconds — состояние каждой группы её участникам.</summary>
    private void StepParties()
    {
        foreach (var invite in _parties.Expire(Tick))
        {
            if (FindPilot(invite.From)?.Player is { } host) Event(host, PartyCodes.Expired, FindPilot(invite.To)?.Player.Name);
        }
        if (Tick % Balance.Party.StatusTicks != 0) return;
        foreach (var party in _parties.Parties.ToList()) SendParty(party);
    }

    /// <summary>Состав группы, где кто, корпус и щит — всем её участникам на связи.</summary>
    private void SendParty(Party party)
    {
        var members = new List<PartyMemberDto>(party.Members.Count);
        foreach (var id in party.Members)
        {
            if (FindPilot(id) is not { } found) continue;
            var (room, player) = found;
            var hull = player.Effective(room.Balance);
            members.Add(new PartyMemberDto(
                player.Id, player.Name, room.SystemId, room.Balance.SystemDef.Name, player.Ship.X, player.Ship.Y,
                (int)Math.Ceiling(player.Hp), (int)Math.Ceiling(player.MaxHp(hull)),
                (int)Math.Ceiling(player.Shield), (int)Math.Ceiling(player.MaxShield(hull)),
                player.Connection is not null, player.IsDead, player.Docked));
        }
        var bytes = Protocol.Encode(new PartyStateMsg(party.LeaderId, members));
        foreach (var id in party.Members) FindPilot(id)?.Player.Connection?.SendRaw(bytes);
    }

    private static void Event(Player player, string code, string? name = null) => player.Connection?.Send(new PartyEventMsg(code, name));

    public bool IsNameTaken(string name, Player? self) => _rooms.Values.Any(r => r.NameTaken(name, self));

    public void Depart(Room from, Player player, string to, bool jump) => _departures.Add((from, player, to, jump));

    private void Transfer()
    {
        foreach (var (from, player, to, jump) in _departures)
        {
            if (from.Pilot(player.Id) != player) continue; // уже ушёл: две причины в одном тике
            var room = jump ? _rooms.GetValueOrDefault(to) : Home(to);
            if (room is null) continue;
            var arrival = jump ? Balance.Galaxy.Arrival(from.SystemId, room.SystemId) : null;
            from.Release(player);
            if (!jump) player.Home = room.SystemId; // дом без станции (правка баланса) — домом становится стартовая
            room.Admit(player, arrival);
            from.BroadcastPlayers();
            if (player.Connection is { } connection) _byConnection[connection.Id] = room;
            _log.LogInformation(
                "Player {Id} moved {From} → {To} ({Reason})", player.Id, from.SystemId, room.SystemId, jump ? "jump" : "respawn");
        }
        _departures.Clear();
    }

    private Room Start => _rooms.GetValueOrDefault(Balance.Galaxy.StartSystem) ?? _rooms.Values.First();

    /// <summary>Дом пилота: система со станцией; иначе — стартовая.</summary>
    private Room Home(string? system) =>
        system is not null && _rooms.TryGetValue(system, out var room) && room.Balance.HasStation ? room : Start;
}
