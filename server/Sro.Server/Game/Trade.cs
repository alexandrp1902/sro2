using Sro.Server.Net;

namespace Sro.Server.Game;

/// <summary>Половина стола обмена (M16b): что этот пилот кладёт и подтвердил ли он то, что видит.</summary>
public sealed class TradeOffer
{
    public int Credits;
    public readonly Dictionary<string, int> Items = new(StringComparer.Ordinal);
    public bool Ready;

    public void Clear()
    {
        Credits = 0;
        Items.Clear();
        Ready = false;
    }
}

/// <summary>
/// Сделка двоих. Живёт, пока оба рядом, живы и не в доке; проверяет это <see cref="Galaxy"/> каждый тик.
/// </summary>
public sealed class TradeSession
{
    public required int A { get; init; }
    public required int B { get; init; }
    public readonly TradeOffer OfferA = new();
    public readonly TradeOffer OfferB = new();

    /// <summary>До этого тика ждёт ответа приглашение; после <see cref="Open"/> не смотрится.</summary>
    public long Until;

    /// <summary>Приглашение принято — стол открыт; до этого сделки ещё нет.</summary>
    public bool Open;

    /// <summary>Сделка снята со стола и исполняется: второе подтверждение в том же тике её уже не найдёт.</summary>
    public bool Done;

    /// <summary>Номер редакции стола: любая правка его двигает и гасит оба подтверждения.</summary>
    public int Rev;

    public bool Has(int id) => id == A || id == B;

    public int Other(int id) => id == A ? B : A;

    public TradeOffer OfferOf(int id) => id == A ? OfferA : OfferB;

    /// <summary>Правка стола: обе стороны подтверждают заново, и старое подтверждение больше не действует.</summary>
    public void Touch()
    {
        Rev++;
        OfferA.Ready = false;
        OfferB.Ready = false;
    }
}

/// <summary>
/// Все сделки галактики (M16b). Только логика: сообщения игрокам рассылает <see cref="Galaxy"/>, а условия
/// «рядом, живы, не в доке» проверяет она же — здесь про мир ничего не известно.
/// Не сохраняется: перезапуск сервера сделки отменяет, как и группы.
/// </summary>
public sealed class TradeBook
{
    /// <summary>Сколько позиций разрешено класть на стол: словарь приходит из сети, и он не бесконечный.</summary>
    public const int MaxItems = 32;

    private readonly Dictionary<int, TradeSession> _byPlayer = [];

    public TradeSession? Of(int id) => _byPlayer.GetValueOrDefault(id);

    public IEnumerable<TradeSession> Sessions => _byPlayer.Values.Distinct();

    /// <summary>Позвать to меняться.</summary>
    /// <returns>Код отказа (<see cref="TradeCodes"/>) или null — предложение ушло.</returns>
    public string? Invite(int from, int to, long tick, int inviteTicks)
    {
        if (from == to) return TradeCodes.Gone;
        if (_byPlayer.ContainsKey(to)) return TradeCodes.Busy;
        // Своё прежнее предложение повтор отменяет: один стол на пилота, второго не бывает.
        if (_byPlayer.TryGetValue(from, out var mine))
        {
            if (mine.Open) return TradeCodes.Busy;
            Remove(mine);
        }
        var session = new TradeSession { A = from, B = to, Until = tick + inviteTicks };
        _byPlayer[from] = session;
        _byPlayer[to] = session;
        return null;
    }

    /// <summary>to принимает предложение from: стол открыт.</summary>
    /// <returns>Код отказа или null — можно торговать.</returns>
    public string? Accept(int to, int from, long tick)
    {
        var session = _byPlayer.GetValueOrDefault(to);
        if (session is null || session.Open || session.A != from || session.B != to || session.Until <= tick)
            return TradeCodes.Expired;
        session.Open = true;
        return null;
    }

    /// <returns>Сделка, от которой отказались, или null — такого предложения не было.</returns>
    public TradeSession? Decline(int to, int from)
    {
        var session = _byPlayer.GetValueOrDefault(to);
        if (session is null || session.Open || session.A != from || session.B != to) return null;
        Remove(session);
        return session;
    }

    /// <summary>
    /// Положить на стол свою половину целиком. Предметы проверяются на вид, а не на наличие: трюм за время
    /// сделки меняется, и единственная проверка, которая что-то значит, — в момент исполнения.
    /// </summary>
    /// <param name="known">Знает ли игра такой предмет: мусор из сети на стол не кладётся.</param>
    /// <returns>Сделка, которую надо разослать, или null — стола нет.</returns>
    public TradeSession? Set(int id, int credits, IReadOnlyDictionary<string, int>? items, Func<string, bool> known)
    {
        var session = _byPlayer.GetValueOrDefault(id);
        if (session is null || !session.Open || session.Done) return null;
        var offer = session.OfferOf(id);
        offer.Clear();
        offer.Credits = Math.Max(0, credits);
        foreach (var (item, count) in items ?? new Dictionary<string, int>())
        {
            if (offer.Items.Count >= MaxItems) break;
            if (count <= 0 || !known(item)) continue;
            offer.Items[item] = count;
        }
        session.Touch();
        return session;
    }

    /// <summary>Подтвердить то, что на столе в редакции rev.</summary>
    /// <returns>Код отказа или null — подтверждение принято.</returns>
    public string? Ready(int id, int rev, out TradeSession? session)
    {
        session = _byPlayer.GetValueOrDefault(id);
        if (session is null || !session.Open || session.Done) return TradeCodes.Gone;
        if (rev != session.Rev) return TradeCodes.Stale;
        session.OfferOf(id).Ready = true;
        return null;
    }

    /// <summary>Оба подтвердили: пора исполнять.</summary>
    public static bool Agreed(TradeSession session) => session.OfferA.Ready && session.OfferB.Ready;

    /// <summary>Снять сделку со стола: отмена, стыковка, гибель, прыжок, обрыв связи или исполнение.</summary>
    /// <returns>Снятая сделка или null — её не было.</returns>
    public TradeSession? End(int id)
    {
        var session = _byPlayer.GetValueOrDefault(id);
        if (session is not null) Remove(session);
        return session;
    }

    /// <summary>Предложения, которых не дождались: убираются и возвращаются, чтобы сказать пригласившему.</summary>
    public List<TradeSession> Expire(long tick)
    {
        var expired = Sessions.Where(s => !s.Open && s.Until <= tick).ToList();
        foreach (var session in expired) Remove(session);
        return expired;
    }

    private void Remove(TradeSession session)
    {
        _byPlayer.Remove(session.A);
        _byPlayer.Remove(session.B);
    }
}
