namespace Sro.Sim.Tests;

/// <summary>
/// Слухи торговца (M12): подсказка должна быть верной — она берётся из настоящих цен соседей,
/// а не выдумывается. Подсказка, которой нельзя верить, хуже, чем никакой.
/// </summary>
public class RumoursTests
{
    private const string Food = "food";
    private const string Ore = "ore";

    /// <param name="sells">Станция делает этот товар сама, то есть его тут можно купить.</param>
    private static MarketPrice Price(string good, int buy, int sell, double stock = 100, double norm = 100, bool sells = false) =>
        new(good, buy, sell, stock, norm, sells);

    /// <summary>Здесь делают продовольствие и берут его по 20, продают по 23.</summary>
    private static StationPrices Here(params MarketPrice[] prices) =>
        new("sol", "Sol", 0, prices.Length > 0 ? prices : [Price(Food, 23, 20, sells: true)]);

    [Fact]
    public void Route_PointsWhereTheGoodSellsHigher()
    {
        var far = new StationPrices("aldebaran", "Альдебаран", 3, [Price(Food, 70, 64)]);

        var rumour = Assert.Single(Rumours.Pick(Here(), [far]));

        Assert.Equal(Rumours.RouteKind, rumour.Kind);
        Assert.Equal(Food, rumour.Good);
        Assert.Equal("aldebaran", rumour.System);
        Assert.Equal(64, rumour.Price);
        Assert.Equal(64 - 23, rumour.Profit); // возят отсюда: платим 23, получаем 64
        Assert.Equal(3, rumour.Hops);
    }

    [Fact]
    public void Route_IsSilentWhenThereIsNoProfit()
    {
        // Там дают меньше, чем просят здесь: везти незачем, и говорить не о чем.
        var far = new StationPrices("vega", "Vega", 1, [Price(Food, 20, 17)]);

        Assert.Empty(Rumours.Pick(Here(), [far]));
    }

    [Fact]
    public void Route_IsSilentAboutWhatTheStationDoesNotSell()
    {
        // Руду здесь только скупают — взять её тут нельзя, значит и советовать маршрут не из чего.
        var here = Here(Price(Ore, 16, 13));
        var far = new StationPrices("castor", "Кастор", 2, [Price(Ore, 90, 80)]);

        Assert.Empty(Rumours.Pick(here, [far]));
    }

    [Fact]
    public void Scarcity_IsMarkedWhenTheStockIsLow()
    {
        var hungry = new StationPrices("epsilon", "Эпсилон", 2, [Price(Food, 90, 80, stock: 20, norm: 100)]);
        var calm = new StationPrices("nova", "Nova", 2, [Price(Food, 90, 80, stock: 100, norm: 100)]);

        Assert.True(Rumours.Pick(Here(), [hungry]).Single().Scarce);
        Assert.False(Rumours.Pick(Here(), [calm]).Single().Scarce);
    }

    [Fact]
    public void Glut_PointsWhereTheGoodIsCheap()
    {
        // Здесь руду берут по 40, а в Касторе её навалом и отдают по 5: есть смысл слетать.
        var here = Here(Price(Ore, 44, 40));
        var mine = new StationPrices("castor", "Кастор", 2, [Price(Ore, 5, 4, stock: 400, norm: 100, sells: true)]);

        var rumour = Assert.Single(Rumours.Pick(here, [mine]));

        Assert.Equal(Rumours.GlutKind, rumour.Kind);
        Assert.Equal("castor", rumour.System);
        Assert.Equal(5, rumour.Price);
    }

    [Fact]
    public void NearbyBeatsFarAwayAtTheSameProfit()
    {
        var near = new StationPrices("vega", "Vega", 1, [Price(Food, 70, 64)]);
        var far = new StationPrices("edge", "Край", 5, [Price(Food, 70, 64)]);

        var first = Rumours.Pick(Here(), [far, near], count: 1).Single();

        Assert.Equal("vega", first.System);
    }

    [Fact]
    public void TooFarAway_IsNotWorthTelling()
    {
        var beyond = new StationPrices("edge", "Край", 9, [Price(Food, 200, 190)]);

        Assert.Empty(Rumours.Pick(Here(), [beyond]));
    }

    [Fact]
    public void OneRumourPerGood()
    {
        var here = Here(Price(Food, 23, 20, sells: true), Price(Ore, 16, 13, sells: true));
        var a = new StationPrices("a", "A", 1, [Price(Food, 70, 64), Price(Ore, 40, 36)]);
        var b = new StationPrices("b", "B", 1, [Price(Food, 80, 74), Price(Ore, 50, 46)]);

        var picked = Rumours.Pick(here, [a, b], count: 3);

        // Про продовольствие и руду — по одному разу, иначе все строки были бы об одном и том же.
        Assert.Equal(picked.Select(r => r.Good).Distinct().Count(), picked.Count);
    }

    [Fact]
    public void TheStationItselfIsNotGossipedAbout()
    {
        var self = new StationPrices("sol", "Sol", 0, [Price(Food, 200, 190)]);

        Assert.Empty(Rumours.Pick(Here(), [self]));
    }

    [Fact]
    public void SharedBalance_GivesTheCoreSomethingToSay()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);

        // Собираем цены всех станций на норме — так выглядит галактика сразу после запуска сервера.
        var stations = new List<StationPrices>();
        foreach (var (id, def) in balance!.Galaxy.SystemMap)
        {
            if (!def.Station) continue;
            var market = balance.ForSystem(id).MainMarket;
            var prices = market.Sold
                .Select(g => new MarketPrice(
                    g,
                    market.BuyPrice(g, balance.Loot.Price(g), market.Norm(g)),
                    market.SellPrice(g, balance.Loot.Price(g), market.Norm(g)),
                    market.Norm(g),
                    market.Norm(g),
                    market.Sells(g)))
                .ToList();
            stations.Add(new StationPrices(id, def.Name, MissionRules.Hops(balance.Galaxy, "sol", id) ?? 99, prices));
        }

        var here = stations.Single(s => s.System == "sol") with { Hops = 0 };
        var rumours = Rumours.Pick(here, stations.Where(s => s.System != "sol"));

        Assert.NotEmpty(rumours);
        foreach (var r in rumours)
        {
            Assert.True(balance.Loot.ItemMap.ContainsKey(r.Good), r.Good);
            Assert.True(r.Hops > 0, $"{r.System}: слух про саму станцию");
            if (r.Kind == Rumours.RouteKind) Assert.True(r.Profit > 0, $"{r.Good} → {r.System}: маршрут без выгоды");
        }
    }

    private static StationYard Yard(string system, string name, int hops, params (string Hull, int Price)[] hulls) =>
        new(system, name, hops, [.. hulls.Select(h => new YardHull(h.Hull, h.Price))], PlaceKey.Station(system));

    [Fact]
    public void YardRumour_NamesTheBestShipThePilotCanAlreadyAfford()
    {
        var yards = new[]
        {
            Yard("vega", "Вега", 1, ("needle", 4000), ("clipper", 16000)),
            Yard("aldebaran", "Крепость Альдебарана", 3, ("lancer", 24000)),
        };

        var rumour = Rumours.Yard(["light"], credits: 20000, yards);

        Assert.NotNull(rumour);
        Assert.Equal(Rumours.YardKind, rumour.Kind);
        // Из того, на что хватает, называют дорогое: это и есть следующий корабль, а не «Игла» за 4 000.
        Assert.Equal("clipper", rumour.Good);
        Assert.Equal(("vega", "Вега", 1, 16000), (rumour.System, rumour.Name, rumour.Hops, rumour.Price));
    }

    [Fact]
    public void YardRumour_FallsBackToTheNearestDream_WhenNothingIsAffordable()
    {
        var yards = new[]
        {
            Yard("epsilon", "Вольная гавань", 4, ("galleon", 45000)),
            Yard("vega", "Вега", 1, ("clipper", 16000)),
        };

        // Кошелёк вдесятеро меньше: обе цели — мечта, и ближняя дешёвая важнее дальней дорогой.
        var rumour = Rumours.Yard(["light"], credits: 6000, yards);
        Assert.Equal("clipper", rumour?.Good);
    }

    [Fact]
    public void YardRumour_IsSilentAboutOwnShipsAndFarSystems()
    {
        // В список «про это молчим» попадают и свои корабли, и те, что продают на здешней верфи.
        Assert.Null(Rumours.Yard(["light", "clipper"], 20000, [Yard("vega", "Вега", 1, ("clipper", 16000))]));
        // Слух про край галактики бесполезен — туда не слетать между делом.
        Assert.Null(Rumours.Yard(["light"], 20000, [Yard("edge", "Край", 9, ("galleon", 45000))]));
        // Своя же система: про здешнюю верфь мастер не рассказывает, её видно на вкладке.
        Assert.Null(Rumours.Yard(["light"], 20000, [Yard("sol", "Сол", 0, ("needle", 4000))]));
    }
}
