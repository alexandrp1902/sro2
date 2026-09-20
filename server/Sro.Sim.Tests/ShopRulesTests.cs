namespace Sro.Sim.Tests;

/// <summary>shared/shop.json: стартовые кредиты, прайс, ремонт (GDD §26, §30, §50, §54).</summary>
public class ShopRulesTests
{
    private static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams> { ["light"] = TestHulls.Light };
    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams> { ["pulse"] = TestWeapons.Pulse };

    [Fact]
    public void SharedFile_IsValid()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);

        var shop = balance!.MainShop;
        Assert.Equal(1000, shop.StartCredits); // GDD §54
        Assert.Equal(0, shop.HullPrice(SimConfig.DefaultHull)); // стартовый корабль бесплатно (§30)
        // Всё оснащение продаётся: иначе второй слот нечем занять.
        foreach (var id in balance.Weapons.Keys.Concat(balance.Modules!.Keys)) Assert.NotNull(shop.ItemPrice(id));
    }

    [Fact]
    public void Defaults_WhenFieldsAreMissing()
    {
        Assert.True(ShopRules.TryParse("{}", Hulls, Weapons, null, out var shop, out var error), error);

        Assert.Equal(1000, shop.StartCredits);
        Assert.Null(shop.HullPrice("light")); // в прайсе нет — не продаётся
        Assert.Equal(0, shop.RepairCost(100)); // ремонт по умолчанию бесплатный
        Assert.Equal(0.5, shop.SellShare);
    }

    [Fact]
    public void RepairCost_GrowsWithThePriceOfTheHull()
    {
        var shop = new ShopRules(
            RepairPrice: 1,
            Hulls: new Dictionary<string, int> { ["light"] = 0, ["cruiser"] = 60_000 },
            RepairHullShare: 0.06);

        // Полный ремонт стоит шестую часть от шести процентов цены корпуса сверх платы за прочность.
        Assert.Equal(100, shop.RepairCost(100, 100, 0)); // стартовый корпус даром — только прочность
        Assert.Equal(100 + 3600, shop.RepairCost(100, 100, 60_000));
        Assert.Equal(50 + 1800, shop.RepairCost(50, 100, 60_000)); // полкорпуса — половина надбавки
        Assert.Equal(0, shop.RepairCost(0, 100, 60_000));
    }

    /// <summary>
    /// Выкуп снятых с баланса предметов (M15.6): цены живут отдельно от прайса, потому что этих id
    /// в каталогах модулей уже нет, и обычная проверка «unknown weapon or module» падала бы на них.
    /// </summary>
    [Fact]
    public void LegacyPrice_BuysBackWhatLeftTheBalance_TiersIncluded()
    {
        var shop = new ShopRules(
            Tiers: [new TierDef(Price: 3), new TierDef(Price: 8)],
            Legacy: new Dictionary<string, int> { ["tankS"] = 100, ["tankL"] = 1500 });

        Assert.Equal(100, shop.LegacyPrice("tankS"));
        Assert.Equal(4500, shop.LegacyPrice("tankL_mk2"));
        Assert.Equal(12_000, shop.LegacyPrice("tankL_mk3"));
        Assert.Null(shop.LegacyPrice("tankM"));   // цены нет — и возвращать нечего
        Assert.Null(shop.LegacyPrice("pulse"));
        // Местный магазин тоже умеет выкупать: Local переносит блок как есть.
        Assert.Equal(100, shop.Local("st:sol", "core", []).LegacyPrice("tankS"));
    }

    [Fact]
    public void SharedFile_BuysBackEveryTankItEverSold()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);

        var shop = balance!.Economy;
        foreach (var id in new[] { "tankS", "tankM", "tankL" })
        {
            Assert.True(shop.LegacyPrice(id) > 0, $"{id} has no legacy price");
            Assert.True(shop.LegacyPrice($"{id}_mk3") > 0, $"{id}_mk3 has no legacy price");
            Assert.Null(shop.ItemPrice(id));   // в прайсе бака больше нет: купить его нельзя
        }
    }

    [Fact]
    public void SellPrice_IsAShareOfThePrice_RoundedDown()
    {
        var shop = new ShopRules(Items: new Dictionary<string, int> { ["pulse"] = 301 }, SellShare: 0.5);

        Assert.Equal(150, shop.SellPrice("pulse"));
        Assert.Equal(0, shop.SellPrice("ghost")); // не продаётся — и не выкупается
    }

    [Theory]
    [InlineData("""{"startCredits": -1}""")]
    [InlineData("""{"repairPrice": -0.5}""")]
    [InlineData("""{"hulls": {"ghost": 100}}""")]
    [InlineData("""{"items": {"ghost": 100}}""")]
    [InlineData("""{"sellShare": 1.5}""")]
    [InlineData("""{"hulls": {"light": -5}}""")]
    [InlineData("""not json""")]
    public void BadFile_IsRejected(string json)
    {
        Assert.False(ShopRules.TryParse(json, Hulls, Weapons, null, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void RepairCost_RoundsUp()
    {
        var shop = new ShopRules(RepairPrice: 0.25);

        Assert.Equal(26, shop.RepairCost(101));
        Assert.Equal(0, shop.RepairCost(0));
        Assert.Equal(0, shop.RepairCost(-3));
    }

    [Fact]
    public void BadShop_RejectsTheWholeBalance()
    {
        Assert.False(Balance.TryParse(TestHulls.SharedSources() with { Shop = "not json" }, out var balance, out var error));
        Assert.Null(balance);
        Assert.StartsWith(Balance.ShopFile, error);
    }
}
