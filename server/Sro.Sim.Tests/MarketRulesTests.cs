namespace Sro.Sim.Tests;

/// <summary>
/// shared/market.json (M12): склад станции, цена от запаса, спред, возврат к норме (GDD §22, §27).
/// Числа здесь свои, а не из shared/, чтобы тюнинг экономики не ронял тесты.
/// </summary>
public class MarketRulesTests
{
    private const string Food = "food";
    private const double Base = 30;

    private static readonly IReadOnlyDictionary<string, LootItem> Items = new Dictionary<string, LootItem>
    {
        [Food] = new("Продовольствие", Price: (int)Base),
        ["crystals"] = new("Кристаллы", Price: 75),
    };

    /// <param name="role">Что станция делает с продовольствием.</param>
    private static MarketRules Rules(MarketRole role = MarketRole.Neutral, string? region = null)
    {
        var station = role switch
        {
            MarketRole.Produces => new MarketStation(Produces: [Food]),
            MarketRole.Consumes => new MarketStation(Consumes: [Food]),
            _ => new MarketStation(),
        };
        return new MarketRules(
            Goods: new Dictionary<string, MarketGood> { [Food] = new(Baseline: 100), ["crystals"] = new(Baseline: 50, Illegal: ["core"]) },
            Station: station,
            Region: region);
    }

    [Fact]
    public void SharedFile_IsValid()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);

        var market = balance!.MarketSet;
        Assert.NotNull(market);
        // Всё, чем торгуют, — настоящий груз: иначе в доке будет строка без названия и объёма.
        foreach (var id in market!.GoodMap.Keys) Assert.True(balance.Loot.ItemMap.ContainsKey(id), id);
        // В системе со станцией рынок есть, в системе без станции — нет.
        Assert.True(balance.ForSystem("sol").Market.Any);
        Assert.False(balance.ForSystem("tau").Market.Any);
    }

    [Fact]
    public void SharedFile_MakesTheCoreToRimRunProfitable()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out _));

        var sol = balance!.ForSystem("sol").Market;
        var rim = balance.ForSystem("aldebaran").Market;
        var price = balance.Loot.Price(Food);

        // Продовольствие делают в Ядре и ждут на Рубеже: везти туда должно быть выгодно даже по одной штуке.
        var bought = sol.BuyPrice(Food, price, sol.Norm(Food));
        var sold = rim.SellPrice(Food, price, rim.Norm(Food));
        Assert.True(sold > bought, $"food: bought {bought} in sol, sold {sold} in aldebaran");
    }

    [Fact]
    public void AtNorm_ProducerIsCheaperThanConsumer()
    {
        var producer = Rules(MarketRole.Produces);
        var consumer = Rules(MarketRole.Consumes);

        // Уровень цены задаёт профиль, а не только размер склада: на полном складе разница обязана остаться.
        Assert.True(
            producer.BuyPrice(Food, Base, producer.Norm(Food)) < consumer.SellPrice(Food, Base, consumer.Norm(Food)),
            "producer must undercut the consumer even at full stock");
    }

    [Fact]
    public void Price_FallsAsStockGrows()
    {
        var rules = Rules();
        var norm = rules.Norm(Food);

        var scarce = rules.BuyPrice(Food, Base, norm / 4);
        var normal = rules.BuyPrice(Food, Base, norm);
        var glut = rules.BuyPrice(Food, Base, norm * 3);

        Assert.True(scarce > normal, $"{scarce} > {normal}");
        Assert.True(normal > glut, $"{normal} > {glut}");
    }

    [Fact]
    public void Price_StaysWithinTheClamps()
    {
        var rules = Rules();

        // Пустой склад не просит бесконечность, затоваренный не отдаёт даром.
        Assert.True(rules.Mid(Food, Base, 0) <= Base * rules.MaxFactor + 1e-9);
        Assert.True(rules.Mid(Food, Base, 1e9) >= Base * rules.MinFactor - 1e-9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(100000)]
    public void BuyingAlwaysCostsMoreThanSelling(double stock)
    {
        foreach (var role in new[] { MarketRole.Neutral, MarketRole.Produces, MarketRole.Consumes })
        {
            var rules = Rules(role);
            var buy = rules.BuyPrice(Food, Base, stock);
            var sell = rules.SellPrice(Food, Base, stock);
            Assert.True(buy > sell, $"{role} at {stock}: buy {buy} must exceed sell {sell}");
        }
    }

    [Fact]
    public void Trade_StepsThePricePerUnit()
    {
        var rules = Rules(MarketRole.Consumes);
        var norm = rules.Norm(Food);

        var (bulk, after) = rules.Trade(Food, Base, norm, 10, buying: false);
        var flat = rules.SellPrice(Food, Base, norm) * 10;

        // Сдать десять разом дешевле, чем десять раз по одной цене первой штуки: цена едет по ходу сделки.
        Assert.True(bulk < flat, $"bulk {bulk} must be below flat {flat}");
        Assert.Equal(norm + 10, after, 6);
    }

    [Fact]
    public void Trade_DoesNotDigBelowEmpty()
    {
        var rules = Rules();

        var (_, after) = rules.Trade(Food, Base, 3, 10, buying: true);

        Assert.Equal(0, after);
    }

    [Fact]
    public void Regress_MovesHalfWayInAHalfLife()
    {
        var rules = Rules();
        var norm = rules.Norm(Food);

        var half = rules.Regress(Food, norm * 3, rules.HalfLifeSeconds);

        Assert.Equal(norm + (norm * 3 - norm) / 2, half, 6);
    }

    [Fact]
    public void Regress_ConvergesToNorm()
    {
        var rules = Rules();
        var stock = 0.0;
        for (var i = 0; i < 5000; i++) stock = rules.Regress(Food, stock, rules.TickSeconds);

        Assert.Equal(rules.Norm(Food), stock, 3);
    }

    [Fact]
    public void Illegal_IsNotTradedInThatRegion()
    {
        var core = Rules(region: "core");
        var rim = Rules(region: "rim");

        Assert.False(core.Trades("crystals"));
        Assert.DoesNotContain("crystals", core.Sold);
        Assert.True(rim.Trades("crystals"));
        Assert.Contains("crystals", rim.Sold);
    }

    [Fact]
    public void Local_LeavesStationlessSystemsWithoutAMarket()
    {
        var rules = new MarketRules(
            Goods: new Dictionary<string, MarketGood> { [Food] = new(Baseline: 100) },
            Stations: new Dictionary<string, MarketStation> { ["sol"] = new(Produces: [Food]) });

        Assert.True(rules.Local("sol", "core").Any);
        Assert.False(rules.Local("tau", "frontier").Any);
    }

    [Fact]
    public void Defaults_WhenFieldsAreMissing()
    {
        Assert.True(MarketRules.TryParse("{}", Items, out var rules, out var error), error);

        Assert.False(rules.Any); // без станций торговать негде
        Assert.Equal(0.18, rules.Spread);
        Assert.Equal(8, rules.TraderUnits);
    }

    [Theory]
    [InlineData("""{"goods": {"ghost": {"baseline": 10}}}""")]
    [InlineData("""{"goods": {"food": {"baseline": -1}}}""")]
    [InlineData("""{"goods": {"food": {}}, "stations": {"sol": {"produces": ["ghost"]}}}""")]
    [InlineData("""{"goods": {"food": {}}, "stations": {"sol": {"produces": ["food"], "consumes": ["food"]}}}""")]
    [InlineData("""{"spread": -1}""")]
    [InlineData("""{"minFactor": 2, "maxFactor": 1}""")]
    [InlineData("""{"elasticity": -1}""")]
    [InlineData("""{"halfLifeSeconds": 0}""")]
    [InlineData("""{"stockCap": 0.5}""")]
    [InlineData("""{"baseline": 0}""")]
    [InlineData("""not json""")]
    public void BadFile_IsRejected(string json)
    {
        Assert.False(MarketRules.TryParse(json, Items, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void BadFile_RejectsTheWholeBalance()
    {
        var sources = TestHulls.SharedSources() with { Market = """{"goods": {"ghost": {}}}""" };

        Assert.False(Balance.TryParse(sources, out _, out var error));
        Assert.StartsWith(Balance.MarketFile, error);
    }

    [Fact]
    public void StationWithoutAStation_IsRejected()
    {
        var sources = TestHulls.SharedSources() with
        {
            Market = """{"goods": {"food": {}}, "stations": {"tau": {"produces": ["food"]}}}""",
        };

        Assert.False(Balance.TryParse(sources, out _, out var error));
        Assert.Contains("no station", error);
    }

    [Fact]
    public void UnknownIllegalRegion_IsRejected()
    {
        var sources = TestHulls.SharedSources() with
        {
            Market = """{"goods": {"food": {"illegal": ["nowhere"]}}}""",
        };

        Assert.False(Balance.TryParse(sources, out _, out var error));
        Assert.Contains("unknown region", error);
    }
}
