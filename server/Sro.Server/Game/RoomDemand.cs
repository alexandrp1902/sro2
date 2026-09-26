using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Идущее событие спроса на одном месте (M15.5): что просят, сколько ещё примут и до какого тика.
///
/// Живёт в комнате, а не в директоре: цену считает комната на каждый запрос рынка, и ходить за ней
/// через галактику было бы и дорого, и незачем.
/// </summary>
internal sealed class DemandEvent(int id, string placeKey, DemandCase cause, DemandRules rules, long endsAtTick)
{
    public int Id { get; } = id;

    /// <summary>Место события: «pl:edgeAsh». Берут товар только здесь, а в дефиците он во всей системе.</summary>
    public string PlaceKey { get; } = placeKey;

    public DemandCase Cause { get; } = cause;

    public int Quota { get; } = rules.Quota;

    /// <summary>Сколько единиц ещё примут.</summary>
    public int Left { get; private set; } = rules.Quota;

    public long EndsAtTick { get; } = endsAtTick;

    /// <summary>Множитель цены прямо сейчас: чем больше уже привезли, тем он меньше.</summary>
    public double Mul => rules.Multiplier(Left, Quota);

    /// <summary>Квота выбрана — событие кончилось досрочно, и это успех, а не срок.</summary>
    public bool Filled => Left <= 0;

    /// <summary>
    /// Правила места системы со спросом события. Профиль места не меняется: цену события считает
    /// <see cref="MarketDemand"/> от обычной цены товара, а не от склада. Событие живёт в комнате,
    /// то есть в одной системе, поэтому любое место здесь — место этой системы: на месте события товар
    /// берут, на остальных его просто нет в продаже. Везти приходится из других систем.
    /// </summary>
    public MarketRules Apply(string key, MarketRules market)
    {
        if (!market.Any) return market;
        var wanted = Cause.GoodList.Where(market.Trades).ToList();
        if (wanted.Count == 0) return market;
        return market with { Demand = new MarketDemand(wanted, rules.Mul, rules.MulEnd, Left, Quota, Here: key == PlaceKey) };
    }

    /// <summary>Пилот сдал сюда count штук; сколько из них пошло в счёт квоты.</summary>
    public int Fill(string key, string good, int count)
    {
        if (key != PlaceKey || count <= 0 || !Cause.GoodList.Contains(good)) return 0;
        var counted = Math.Min(count, Left);
        Left -= counted;
        return counted;
    }
}

public sealed partial class Room
{
    private DemandEvent? _demand;

    /// <summary>Идёт ли здесь событие с таким номером — для директора.</summary>
    public bool HasDemand(int id) => _demand?.Id == id;

    /// <summary>Сколько единиц событие ещё примет; 0 — квота выбрана или события нет.</summary>
    public int DemandLeft(int id) => _demand?.Id == id ? _demand.Left : 0;

    /// <summary>Множитель цены события; 1 — события нет.</summary>
    public double DemandMul(int id) => _demand?.Id == id ? _demand.Mul : 1;

    /// <summary>Место события; null — здесь его нет.</summary>
    public string? DemandPlace => _demand?.PlaceKey;

    /// <summary>
    /// Места, где может вспыхнуть спрос: все, где торгуют. Систему выбирает директор — ему важна только
    /// красная зона, а тут он спрашивает, есть ли в ней куда сдавать товар.
    /// </summary>
    public IReadOnlyList<PlaceDef> TradingPlaces => [.. Balance.Places.Where(p => MarketAt(p.Key) is { Rules.Any: true })];

    /// <summary>
    /// Открыть приёмку: просимое тут же пропадает с прилавков всей системы, а на месте события его
    /// начинают брать втридорога. Цены в доке сразу уезжают всем, кто стоит в этой системе.
    /// </summary>
    internal void StartDemand(int id, PlaceDef place, DemandCase cause, DemandRules rules)
    {
        _demand = new DemandEvent(id, place.Key, cause, rules, Tick + rules.DurationTicks);
        BroadcastMarket();
        _log.LogInformation("Demand {Id} opened at {Place}: {Case}, quota {Quota}", id, place.Key, cause.Id, rules.Quota);
    }

    /// <summary>
    /// Событие кончилось — квоту выбрали или вышел срок. Запас сам вернётся к норме за свой полупериод:
    /// цена оседает на глазах, а не щёлкает обратно.
    /// </summary>
    internal void EndDemand(int id)
    {
        if (_demand?.Id != id) return;
        _demand = null;
        BroadcastMarket();
    }

    /// <summary>Сдали товар события — квота убывает. Вернёт true, если после этого она выбрана.</summary>
    private bool CountDemand(Player player, string good, int count)
    {
        if (_demand is null || PlaceOf(player)?.Key is not { } key) return false;
        if (_demand.Fill(key, good, count) <= 0) return false;
        return _demand.Filled;
    }
}
