using Sro.Sim;

namespace Sro.Sim.Tests;

/// <summary>
/// События спроса (M15.5): множитель тает вместе с квотой, цена события считается от обычной цены товара,
/// а не от здешнего склада (плейтест 2026-09-26), и в системе события товар не купить.
/// </summary>
public class DemandRulesTests
{
    private static readonly IReadOnlyDictionary<string, LootItem> Items = new Dictionary<string, LootItem>
    {
        ["medicine"] = new("Медикаменты", Volume: 1, Price: 60),
        ["food"] = new("Продовольствие", Volume: 1, Price: 30),
    };

    private static DemandRules Rules(params DemandCase[] cases) =>
        new(Quota: 100, Mul: 3, MulEnd: 2, Cases: cases);

    private static readonly DemandCase Plague = new("plague", "Эпидемия", ["medicine", "food"]);

    [Fact]
    public void TheMultiplierFallsFromMulToMulEndAsTheQuotaFills()
    {
        var rules = Rules(Plague);
        Assert.Equal(3, rules.Multiplier(100, 100), 6); // ещё ничего не привезли
        Assert.Equal(2.5, rules.Multiplier(50, 100), 6); // половина квоты
        Assert.Equal(2, rules.Multiplier(0, 100), 6); // всё довезли
    }

    [Fact]
    public void TheLastLoadIsStillWorthCarrying()
    {
        // Если множитель уходил бы в единицу, последний трюм везти было бы незачем,
        // квота не выбиралась бы никогда, и «спрос закрыт» перестал бы случаться.
        Assert.True(Rules(Plague).Multiplier(1, 100) > 1.5);
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

    private static readonly MarketRules Market = new(
        Goods: new Dictionary<string, MarketGood> { ["medicine"] = new(100), ["food"] = new(100) },
        Station: new MarketStation(Produces: ["medicine"], Consumes: ["food"]));

    private static MarketDemand Demand(int left = 100, bool here = true) => new(["medicine"], 3, 2, left, 100, here);

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(750)]
    public void TheEventPaysTheBasePriceTimesTheMultiplier_WhateverTheStock(double stock)
    {
        // Плейтест 2026-09-26: «×2.4» поверх цены затоваренного склада платило меньше, чем товар стоил
        // в соседней системе. Теперь табло не врёт: ×3 от обычной цены при любом запасе.
        var boosted = Market.With(Demand());
        Assert.Equal((int)Math.Floor(60 * 3 * (1 - Market.Spread / 2)), boosted.SellPrice("medicine", 60, stock));
        Assert.Equal(3, boosted.Demand!.Mul, 6);
    }

    [Fact]
    public void EveryUnitTapersTheMultiplier_AndTheEventUnitsDoNotPileUpInStock()
    {
        var boosted = Market.With(Demand(left: 100));
        var (credits, stock) = boosted.Trade("medicine", 60, 250, 100, buying: false);

        var expected = 0;
        for (var i = 0; i < 100; i++) expected += (int)Math.Floor(60 * DemandRules.Taper(3, 2, 100 - i, 100) * (1 - Market.Spread / 2) + 1e-9);
        Assert.Equal(expected, credits);
        // Сотню лекарств забрала нужда: склад места их не видел, и завала после события не будет.
        Assert.Equal(250, stock, 6);
        // Даже последняя штука вдвое дороже обычной: её стоит везти.
        Assert.True(boosted.SellPrice("medicine", 60, 250, 99) >= (int)Math.Floor(60 * 2 * (1 - Market.Spread / 2)));
    }

    [Fact]
    public void OverTheQuotaTheStationPaysThePlainPrice()
    {
        var boosted = Market.With(Demand(left: 10));
        var (credits, stock) = boosted.Trade("medicine", 60, 250, 20, buying: false);

        var (plain, plainStock) = Market.Trade("medicine", 60, 250, 10, buying: false);
        var eventPart = 0;
        for (var i = 0; i < 10; i++) eventPart += boosted.SellPrice("medicine", 60, 250, i);
        Assert.Equal(eventPart + plain, credits);
        Assert.Equal(plainStock, stock, 6);
    }

    [Fact]
    public void TheWholeSystemIsShort_AndOnlyTheEventPlaceBuysDear()
    {
        var there = Market.With(Demand());
        var nextDoor = Market.With(Demand(here: false));

        // Купить нельзя нигде в системе: иначе медикаменты брались бы у соседки и сдавались за углом.
        Assert.False(there.Sells("medicine"));
        Assert.False(nextDoor.Sells("medicine"));
        Assert.True(nextDoor.Sells("food"));
        // Втридорога берут только на месте события; соседка платит как обычно.
        Assert.Equal(Market.SellPrice("medicine", 60, 250), nextDoor.SellPrice("medicine", 60, 250));
        Assert.True(there.SellPrice("medicine", 60, 250) > 2 * nextDoor.SellPrice("medicine", 60, 250));
    }

    [Fact]
    public void DemandTouchesOnlyTheGoodsItAsksFor()
    {
        var boosted = Market.With(Demand());
        Assert.Equal(Market.SellPrice("food", 30, 50), boosted.SellPrice("food", 30, 50));
        Assert.Equal(Market.BuyPrice("food", 30, 50), boosted.BuyPrice("food", 30, 50));
    }
}
