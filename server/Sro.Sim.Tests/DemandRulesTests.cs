using Sro.Sim;

namespace Sro.Sim.Tests;

/// <summary>
/// События спроса (M15.5): множитель тает вместе с квотой, а потолок цены поднимается вместе с ним —
/// иначе зажим MaxFactor съел бы весь смысл события.
/// </summary>
public class DemandRulesTests
{
    private static readonly IReadOnlyDictionary<string, LootItem> Items = new Dictionary<string, LootItem>
    {
        ["medicine"] = new("Медикаменты", Volume: 1, Price: 60),
        ["food"] = new("Продовольствие", Volume: 1, Price: 30),
    };

    private static DemandRules Rules(params DemandCase[] cases) =>
        new(Quota: 180, Mul: 4.5, MulEnd: 2, Cases: cases);

    private static readonly DemandCase Plague = new("plague", "Эпидемия", ["medicine", "food"]);

    [Fact]
    public void TheMultiplierFallsFromMulToMulEndAsTheQuotaFills()
    {
        var rules = Rules(Plague);
        Assert.Equal(4.5, rules.Multiplier(180, 180), 6); // ещё ничего не привезли
        Assert.Equal(3.25, rules.Multiplier(90, 180), 6); // половина квоты
        Assert.Equal(2, rules.Multiplier(0, 180), 6); // всё довезли
    }

    [Fact]
    public void TheLastLoadIsStillWorthCarrying()
    {
        // Если множитель уходил бы в единицу, последний трюм везти было бы незачем,
        // квота не выбиралась бы никогда, и «спрос закрыт» перестал бы случаться.
        Assert.True(Rules(Plague).Multiplier(1, 180) > 1.5);
    }

    [Theory]
    [InlineData(0, 4.5, 2, "quota must not be negative")]
    [InlineData(-1, 4.5, 2, "quota must not be negative")]
    public void BadQuotaIsRejected(int quota, double mul, double mulEnd, string expected)
    {
        // 0 — не ошибка сама по себе, но и событий тогда нет: Any это учитывает.
        var rules = new DemandRules(Quota: quota, Mul: mul, MulEnd: mulEnd, Cases: [Plague]);
        Assert.Equal(quota < 0 ? expected : null, rules.Validate(Items));
    }

    [Fact]
    public void BadMultipliersAreRejected()
    {
        Assert.Equal("mulEnd must be at least 1", new DemandRules(MulEnd: 0.5, Cases: [Plague]).Validate(Items));
        Assert.Equal("mul must not be below mulEnd", new DemandRules(Mul: 1.5, MulEnd: 2, Cases: [Plague]).Validate(Items));
    }

    [Fact]
    public void ACaseMustNameRealGoods()
    {
        Assert.Equal("cases[0]: unknown good gold", Rules(new DemandCase("gold", "Золото", ["gold"])).Validate(Items));
        Assert.Equal("cases[0]: no goods", Rules(new DemandCase("empty", "Пусто")).Validate(Items));
        Assert.Equal("cases[1]: duplicate id 'plague'", Rules(Plague, Plague).Validate(Items));
    }

    [Fact]
    public void WithoutCasesThereAreNoEvents()
    {
        Assert.Equal("no cases", new DemandRules().Validate(Items));
        Assert.False(DemandRules.None.Any);
        Assert.Null(DemandRules.None.Validate(Items)); // выключённый файл — не ошибка
    }

    [Fact]
    public void DemandLiftsThePriceAboveMaxFactor()
    {
        // Главная ловушка этапа: Mid зажимает цену в MaxFactor, и без подъёма потолка
        // множитель ×4.5 превратился бы в ×2.2, а событие — в обычный рейс.
        var market = new MarketRules(
            Goods: new Dictionary<string, MarketGood> { ["medicine"] = new(100) },
            Station: new MarketStation(Consumes: ["medicine"]));
        var norm = market.Norm("medicine");

        var plain = market.Mid("medicine", 60, norm);
        var boosted = market.With(new MarketDemand(["medicine"], 4.5)).Mid("medicine", 60, norm);

        Assert.True(plain <= 60 * market.MaxFactor + 1e-9, "без события цена упирается в обычный потолок");
        Assert.True(boosted > 60 * market.MaxFactor, $"с событием цена {boosted} должна пробивать обычный потолок");
        Assert.Equal(plain * 4.5, boosted, 6);
    }

    [Fact]
    public void DemandTouchesOnlyTheGoodsItAsksFor()
    {
        var market = new MarketRules(
            Goods: new Dictionary<string, MarketGood> { ["medicine"] = new(100), ["food"] = new(100) },
            Station: new MarketStation(Consumes: ["medicine", "food"]));
        var boosted = market.With(new MarketDemand(["medicine"], 4.5));

        Assert.Equal(market.Mid("food", 30, 50), boosted.Mid("food", 30, 50), 6);
        Assert.NotEqual(market.Mid("medicine", 60, 50), boosted.Mid("medicine", 60, 50), 6);
    }
}
