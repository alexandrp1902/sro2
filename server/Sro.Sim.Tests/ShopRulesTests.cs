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

        var shop = balance!.Shop;
        Assert.Equal(1000, shop.StartCredits); // GDD §54
        Assert.Equal(0, shop.HullPrice(SimConfig.DefaultHull)); // стартовый корабль бесплатно (§30)
        Assert.Equal(0, shop.WeaponPrice(SimConfig.DefaultWeapon));
    }

    [Fact]
    public void Defaults_WhenFieldsAreMissing()
    {
        Assert.True(ShopRules.TryParse("{}", Hulls, Weapons, out var shop, out var error), error);

        Assert.Equal(1000, shop.StartCredits);
        Assert.Null(shop.HullPrice("light")); // в прайсе нет — не продаётся
        Assert.Equal(0, shop.RepairCost(100)); // ремонт по умолчанию бесплатный
    }

    [Theory]
    [InlineData("""{"startCredits": -1}""")]
    [InlineData("""{"repairPrice": -0.5}""")]
    [InlineData("""{"hulls": {"ghost": 100}}""")]
    [InlineData("""{"weapons": {"ghost": 100}}""")]
    [InlineData("""{"hulls": {"light": -5}}""")]
    [InlineData("""not json""")]
    public void BadFile_IsRejected(string json)
    {
        Assert.False(ShopRules.TryParse(json, Hulls, Weapons, out _, out var error));
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
