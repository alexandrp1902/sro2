namespace Sro.Sim;

/// <summary>Цена одного товара на станции — то, что видно со стороны: почём берут и сколько лежит.</summary>
/// <param name="Buy">Сколько там платит пилот.</param>
/// <param name="Sell">Сколько там получает пилот.</param>
/// <param name="Stock">Запас склада, штук.</param>
/// <param name="Norm">Равновесный запас: по нему видно, дефицит там или завал.</param>
/// <param name="Sells">Станция продаёт этот товар (то есть делает его сама), а не только скупает.</param>
public readonly record struct MarketPrice(string Good, int Buy, int Sell, double Stock, double Norm, bool Sells);

/// <summary>Место глазами соседей: где оно, как далеко и почём там товары.</summary>
/// <param name="System">Система места — по ней считается, сколько туда прыжков.</param>
/// <param name="Name">Как место зовут: станция системы или поселение на планете (M15).</param>
/// <param name="Hops">Сколько прыжков отсюда; 0 — это здешняя система.</param>
/// <param name="Place">Ключ места (M15); null — баланс без мест, как до M15.</param>
public sealed record StationPrices(string System, string Name, int Hops, IReadOnlyList<MarketPrice> Prices, string? Place = null);

/// <summary>
/// Слух торговца (M12): подсказка, куда везти товар или где его дёшево взять. Текст собирает клиент —
/// здесь только факты, как у <see cref="MissionOffer"/>.
/// </summary>
/// <param name="Kind"><see cref="Rumours.RouteKind"/> — брать здесь и везти туда; <see cref="Rumours.GlutKind"/> — там этого навалом.</param>
/// <param name="Good">Товар из loot.json.</param>
/// <param name="System">Про какую систему слух.</param>
/// <param name="Name">Её название — клиенту неоткуда его взять на экране дока.</param>
/// <param name="Hops">Сколько туда прыжков.</param>
/// <param name="Price">Цена штуки там: при route — сколько дадут, при glut — сколько просят.</param>
/// <param name="Profit">Сколько выходит с штуки при route; при glut — 0.</param>
/// <param name="Scarce">Там этого сейчас мало: отсюда разговоры про эпидемию и голод.</param>
public sealed record Rumour(
    string Kind,
    string Good,
    string System,
    string Name,
    int Hops,
    int Price,
    int Profit = 0,
    bool Scarce = false);

/// <summary>Корпус на чужой верфи и его цена там (M20).</summary>
public readonly record struct YardHull(string Hull, int Price);

/// <summary>Верфь глазами соседей: где она, как далеко и какие корпуса там на стапеле (M20).</summary>
/// <param name="Hops">Сколько прыжков отсюда; 0 — это здешняя система.</param>
public sealed record StationYard(string System, string Name, int Hops, IReadOnlyList<YardHull> Hulls, string? Place = null);

/// <summary>
/// О чём торговец судачит в доке (M12). Слухи берутся из настоящих цен соседних станций, а не выдумываются:
/// иначе они были бы шумом, а подсказка, которой нельзя верить, хуже, чем никакой.
///
/// Считается на стыковке и дальше не меняется: слух — это то, что услышал, а не бегущая строка.
/// </summary>
public static class Rumours
{
    /// <summary>Взять товар здесь и продать там.</summary>
    public const string RouteKind = "route";

    /// <summary>Там этого навалом и дёшево — есть смысл слетать за ним.</summary>
    public const string GlutKind = "glut";

    /// <summary>Там, на чужой верфи, стоит корпус, которого у пилота нет (M20). Good — id корпуса.</summary>
    public const string YardKind = "yard";

    /// <summary>Во сколько раз корпус может быть дороже кошелька, чтобы про него ещё стоило рассказывать как о близкой цели.</summary>
    private const int DreamShare = 3;

    /// <summary>Запас ниже этой доли нормы — там дефицит, и об этом говорят.</summary>
    private const double ScarceShare = 0.6;

    /// <summary>Запас выше этой доли нормы — там завал.</summary>
    private const double GlutShare = 1.6;

    /// <summary>Дальше этого торговцы новостей не собирают: слух про край галактики бесполезен.</summary>
    private const int MaxHops = 5;

    /// <summary>
    /// Про какую чужую верфь рассказать мастеру (M20). С M20 корпуса стоят не в каждом доке своего
    /// региона, а на одной-двух верфях, и без наводки «Улан» можно не найти за всю игру. Но и наводка
    /// не должна быть каталогом: называем один корабль — тот, до которого пилоту ближе всего дотянуться.
    ///
    /// Сначала то, на что уже хватает кредитов (из них — самое дорогое: это и есть следующий корабль),
    /// потом то, что ещё по карману в обозримом будущем, и только потом остальное. При равном — что ближе.
    /// </summary>
    /// <param name="skip">
    /// Корпуса, про которые рассказывать нечего: они уже в ангаре или стоят на здешней верфи —
    /// советовать лететь за тем, что продают в этом же доке, мастер не станет.
    /// </param>
    /// <param name="credits">Кошелёк пилота.</param>
    /// <returns>null — рассказывать не о чем: все соседние верфи торгуют тем, что уже в ангаре.</returns>
    public static Rumour? Yard(IReadOnlyCollection<string> skip, int credits, IEnumerable<StationYard> yards)
    {
        Rumour? best = null;
        var bestKey = (Reach: 0, Hops: 0, Tie: 0L, Hull: "");
        foreach (var yard in yards)
        {
            if (yard.Hops <= 0 || yard.Hops > MaxHops) continue;
            foreach (var (hull, price) in yard.Hulls)
            {
                if (skip.Contains(hull)) continue;
                var reach = price <= credits ? 0 : price <= (long)credits * DreamShare ? 1 : 2;
                // По карману — сперва дорогое (мечта ближе к делу), не по карману — сперва дешёвое.
                var key = (reach, yard.Hops, reach == 0 ? -(long)price : price, hull);
                if (best is not null && key.CompareTo(bestKey) >= 0) continue;
                best = new Rumour(YardKind, hull, yard.System, yard.Name, yard.Hops, price);
                bestKey = key;
            }
        }
        return best;
    }

    /// <summary>
    /// Что рассказать пилоту, который стоит на станции here. Сначала самый выгодный маршрут отсюда,
    /// потом — где дёшево взять; про один товар говорим только раз, иначе все три строки будут об одном.
    /// </summary>
    /// <param name="count">Сколько слухов нужно.</param>
    public static IReadOnlyList<Rumour> Pick(StationPrices here, IEnumerable<StationPrices> others, int count = 3)
    {
        if (count <= 0) return [];
        var local = here.Prices.ToDictionary(p => p.Good, StringComparer.Ordinal);
        var routes = new List<(double Weight, Rumour Rumour)>();
        var gluts = new List<(double Weight, Rumour Rumour)>();

        foreach (var station in others)
        {
            if (station.System == here.System || station.Hops <= 0 || station.Hops > MaxHops) continue;
            foreach (var there in station.Prices)
            {
                var scarce = there.Norm > 0 && there.Stock < there.Norm * ScarceShare;
                // Маршрут: здесь этот товар продают, там за него дают больше.
                if (local.TryGetValue(there.Good, out var mine) && mine.Sells)
                {
                    var profit = there.Sell - mine.Buy;
                    if (profit > 0)
                    {
                        // Дальний рейс выгоднее ближнего только если платит заметно больше.
                        var weight = profit / (double)station.Hops + (scarce ? profit * 0.25 : 0);
                        routes.Add((weight, new Rumour(
                            RouteKind, there.Good, station.System, station.Name, station.Hops, there.Sell, profit, scarce)));
                    }
                }
                // Завал: там этого много, продают сами и просят меньше, чем здесь дают.
                if (!there.Sells || !(there.Norm > 0) || there.Stock <= there.Norm * GlutShare) continue;
                var gain = local.TryGetValue(there.Good, out var back) ? back.Sell - there.Buy : 0;
                if (gain > 0) gluts.Add((gain / (double)station.Hops, new Rumour(
                    GlutKind, there.Good, station.System, station.Name, station.Hops, there.Buy, gain)));
            }
        }

        var picked = new List<Rumour>(count);
        var goods = new HashSet<string>(StringComparer.Ordinal);
        // Сначала маршруты: пилот в доке чаще решает, что взять с собой, чем куда слетать порожняком.
        foreach (var list in new[] { routes, gluts })
        {
            foreach (var (_, rumour) in list.OrderByDescending(r => r.Weight).ThenBy(r => r.Rumour.System, StringComparer.Ordinal))
            {
                if (picked.Count >= count) break;
                if (!goods.Add(rumour.Good)) continue;
                picked.Add(rumour);
            }
        }
        return picked;
    }
}
