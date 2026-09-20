using Sro.Sim;

namespace Sro.Sim.Tests;

/// <summary>Тиры Mk1–Mk3 (M11): раскрытие каталогов, цены и ассортимент магазинов по регионам и станциям.</summary>
public class TiersTests
{
    private static readonly IReadOnlyList<TierDef> Two =
        [new TierDef(Stat: 1.25, Power: 1.15, Price: 3, Engine: 1.5, Radar: 1.1), new TierDef(Stat: 1.5, Power: 1.3, Price: 8, Engine: 2, Radar: 1.2)];

    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["pulse"] = new("Импульсная пушка Mk1", 100, 75, 1.0, 500, 700, 10, Power: 20),
    };

    private static readonly IReadOnlyDictionary<string, ModuleParams> Modules = new Dictionary<string, ModuleParams>
    {
        ["engineM"] = new("Двигатель", Fitting.EngineSlot, EquipClass.M, Power: 10, Speed: 1.1, Accel: 1.2),
        ["afterburner"] = new("Форсаж", Fitting.EngineSlot, EquipClass.M, Power: 20, Speed: 1.3, Accel: 0.9),
        ["shieldS"] = new("Щит", Fitting.ShieldSlot, EquipClass.S, Power: 10, Shield: 200, ShieldRegen: 20),
        ["cooling"] = new("Охлаждение", Fitting.UtilityKind, EquipClass.S, Power: 12, Cooling: 0.1),
    };

    [Theory]
    [InlineData("ion", "ion", 1)]
    [InlineData("ion_mk2", "ion", 2)]
    [InlineData("ion_mk3", "ion", 3)]
    [InlineData("ion_mk9", "ion_mk9", 1)]
    [InlineData("_mk2", "_mk2", 1)]
    public void Split_ReadsTheTierFromTheId(string id, string baseId, int tier)
    {
        Assert.Equal((baseId, tier), Tiers.Split(id));
        Assert.Equal(id, Tiers.Id(baseId, tier));
    }

    [Fact]
    public void Tiers_KeepCountableStatsWhole()
    {
        var weapons = Tiers.Expand(Weapons, Two);
        var modules = Tiers.Expand(Modules, Two);

        // Множители энергии (1.15, 1.3) в двоичной дроби не ложатся ровно: без округления щит Mk2 просил бы
        // «22.999999999999996 энергии», а радар Mk2 — «13.799999999999999». Игрок видит эти числа как есть.
        static void Whole(double value, string what) => Assert.True(value == Math.Round(value), $"{what} = {value:R}");

        foreach (var (id, w) in weapons) Whole(w.Power, $"{id}.power");
        foreach (var (id, m) in modules)
        {
            Whole(m.Power, $"{id}.power");
            Whole(m.Shield, $"{id}.shield");
            Whole(m.ShieldRegen, $"{id}.shieldRegen");
            Whole(m.Radar, $"{id}.radar");
            Whole(m.Fuel, $"{id}.fuel");
            Whole(m.Output, $"{id}.output");
            Whole(m.Repair, $"{id}.repair");
            Whole(m.Cargo, $"{id}.cargo");
        }

        Assert.Equal(12, modules["shieldS_mk2"].Power);   // 10 × 1.15 = 11.5 → 12
        Assert.Equal(14, modules["cooling_mk2"].Power);   // 12 × 1.15 = 13.8 → 14
        // Дробные по смыслу — без хвоста: 1 + 0.1 × 1.5 = 1.15, а не 1.1500000000000001.
        Assert.Equal(1.15, modules["engineM_mk2"].Speed);
        Assert.Equal(0.125, modules["cooling_mk2"].Cooling);
    }

    [Fact]
    public void Name_ReplacesMk1OrAppendsTheTier()
    {
        Assert.Equal("Лазер Mk2", Tiers.Name("Лазер Mk1", 2));
        Assert.Equal("Щит «Заслон» Mk3", Tiers.Name("Щит «Заслон»", 3));
        Assert.Equal("Лазер Mk1", Tiers.Name("Лазер Mk1", 1));
    }

    [Fact]
    public void Expand_ScalesDamageAndPower()
    {
        var weapons = Tiers.Expand(Weapons, Two);
        Assert.Equal(3, weapons.Count);
        Assert.Equal(125, weapons["pulse_mk2"].Damage);
        Assert.Equal(23, weapons["pulse_mk2"].Power);
        Assert.Equal(150, weapons["pulse_mk3"].Damage);
        Assert.Equal(2, weapons["pulse_mk2"].Tier);
        Assert.Equal(100, weapons["pulse"].Damage); // Mk1 остаётся как в файле
    }

    [Fact]
    public void Expand_GrowsTheEngineBonusButNotTheDrawback()
    {
        var modules = Tiers.Expand(Modules, Two);
        Assert.Equal(1.15, modules["engineM_mk2"].Speed, 6); // 1 + 0.1 × 1.5
        Assert.Equal(1.3, modules["engineM_mk2"].Accel, 6);
        // Форсаж и в Mk2 разгоняется хуже обычного двигателя: ухудшение не растёт.
        Assert.Equal(0.9, modules["afterburner_mk2"].Accel, 6);
        Assert.Equal(1.45, modules["afterburner_mk2"].Speed, 6);
        Assert.Equal(250, modules["shieldS_mk2"].Shield);
        Assert.Equal(0.125, modules["cooling_mk2"].Cooling, 6);
    }

    [Fact]
    public void Price_MultipliesAndRoundsToTens()
    {
        Assert.Equal(300, Tiers.Price(300, 1, Two));
        Assert.Equal(900, Tiers.Price(300, 2, Two));
        Assert.Equal(2400, Tiers.Price(300, 3, Two));
    }

    private static ShopRules Shop() => new(
        Hulls: new Dictionary<string, int> { ["light"] = 0, ["cruiser"] = 60000 },
        Items: new Dictionary<string, int> { ["pulse"] = 300, ["railgun"] = 6000 },
        Tiers: Two,
        Regions: new Dictionary<string, StockDef>
        {
            ["core"] = new(Hulls: ["light"], Items: ["pulse"], Tiers: [1]),
            ["rim"] = new(Hulls: ["cruiser"], Items: ["pulse", "railgun"], Tiers: [2, 3]),
        },
        Stations: new Dictionary<string, StockDef>
        {
            ["epsilon"] = new(Items: ["railgun"], Remove: ["pulse"], Price: 1.2),
        });

    private static readonly string[] AllItems = ["pulse", "pulse_mk2", "pulse_mk3", "railgun", "railgun_mk2", "railgun_mk3"];

    [Fact]
    public void Local_SellsWhatTheRegionHas()
    {
        var core = Shop().Local("sol", "core", AllItems);
        Assert.True(core.SellsHull("light"));
        Assert.False(core.SellsHull("cruiser")); // за крейсером — на Рубеж
        Assert.True(core.SellsItem("pulse"));
        Assert.False(core.SellsItem("pulse_mk2")); // в Ядре только Mk1
        Assert.False(core.SellsItem("railgun"));
        // Цена есть на всё: продать со склада можно что угодно.
        Assert.Equal(900, core.ItemPrice("pulse_mk2"));
        Assert.Equal(450, core.SellPrice("pulse_mk2"));
    }

    [Fact]
    public void Local_AppliesStationStockAndPrices()
    {
        var rim = Shop().Local("epsilon", "rim", AllItems);
        Assert.True(rim.SellsHull("cruiser"));
        Assert.True(rim.SellsItem("railgun_mk3"));
        Assert.False(rim.SellsItem("railgun")); // Mk1 на Рубеже не держат
        Assert.False(rim.SellsItem("pulse_mk2")); // станция убрала импульсные из ассортимента
        Assert.Equal(72000, rim.HullPrice("cruiser")); // ×1.2 станции
        Assert.Equal("Вольная гавань", rim.Title ?? "Вольная гавань");
    }

    [Fact]
    public void Local_WithoutRegionsKeepsOneShopEverywhere()
    {
        var shop = new ShopRules(Items: new Dictionary<string, int> { ["pulse"] = 300 });
        var local = shop.Local("sol", null, ["pulse"]);
        Assert.True(local.SellsItem("pulse"));
        Assert.Null(local.Stock);
    }

    [Fact]
    public void SharedShop_GivesEachRegionItsOwnStock()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        var core = balance!.ForSystem("sol").Shop;
        var frontier = balance.ForSystem("nova").Shop;
        var rim = balance.ForSystem("epsilon").Shop;

        Assert.True(core.SellsHull("light"));
        Assert.False(core.SellsHull("cruiser"));
        Assert.False(core.SellsItem("railgun"));
        Assert.True(frontier.SellsItem("ion"));
        Assert.True(frontier.SellsItem("ion_mk2"));
        Assert.True(frontier.SellsItem("railgun")); // военная станция Nova торгует рельсотронами
        Assert.False(frontier.SellsItem("railgun_mk3"));
        Assert.True(rim.SellsHull("cruiser"));
        Assert.True(rim.SellsItem("torpedoes_mk3"));
        Assert.False(rim.SellsItem("pulse")); // Mk1 на Рубеже не продают
    }
}
