namespace Sro.Sim.Tests;

/// <summary>Лут и трюм (GDD §21–23): разбор shared/loot.json, валидация и таблицы дропа.</summary>
public class LootRulesTests
{
    /// <summary>rng с заданной последовательностью: роллы проверяются точно, а не статистически.</summary>
    private static Func<double> Seq(params double[] values)
    {
        var i = 0;
        return () => values[i++ % values.Length];
    }

    private static List<(string Item, int Count)> Roll(LootTable table, int level, Func<double> rng)
    {
        var into = new List<(string, int)>();
        table.Roll(level, rng, into);
        return into;
    }

    private static readonly IReadOnlyDictionary<string, LootItem> Items = new Dictionary<string, LootItem>
    {
        ["metal"] = new("Металл", Volume: 1, Price: 10),
        ["tech"] = new("Компонент", "epic", Volume: 2, Price: 200),
    };

    [Fact]
    public void EmptyFile_MeansNoLootButKeepsDefaults()
    {
        Assert.True(LootRules.TryParse("{}", out var rules, out var error), error);
        Assert.Empty(rules.ItemMap);
        Assert.Empty(rules.TableMap);
        Assert.Equal(130, rules.PickupRange);
        Assert.True(rules.StationUnload);
    }

    [Fact]
    public void UnknownItem_HasNoVolumeAndNoPrice()
    {
        Assert.True(LootRules.TryParse("""{"items":{"metal":{"name":"Металл","volume":3,"price":7}}}""", out var rules, out _));
        Assert.Equal(3, rules.Volume("metal"));
        Assert.Equal(7, rules.Price("metal"));
        Assert.Equal(0, rules.Volume("nope"));
        Assert.Equal(0, rules.Price("nope"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"pickupRange": -1}""")]
    [InlineData("""{"lifetimeSeconds": 0}""")]
    [InlineData("""{"lifetimeSeconds": 10, "fadeSeconds": 20}""")]
    [InlineData("""{"maxItems": 0}""")]
    [InlineData("""{"driftFactor": 1.5}""")]
    [InlineData("""{"driftDampTime": 0}""")]
    [InlineData("""{"stationRange": 0}""")]
    [InlineData("""{"items":{"metal":{"name":"","volume":1}}}""")]
    [InlineData("""{"items":{"metal":{"name":"Металл","volume":0}}}""")]
    [InlineData("""{"items":{"metal":{"name":"Металл","price":-1}}}""")]
    [InlineData("""{"items":{"metal":{"name":"Металл","rarity":"shiny"}}}""")]
    [InlineData("""{"tables":{"pirate":{"rolls":[{"item":"nope"}]}}}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"tables":{"p":{"rolls":[{"item":"metal","chance":1.5}]}}}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"tables":{"p":{"rolls":[{"item":"metal","min":3,"max":2}]}}}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"tables":{"p":{"rolls":[{"item":"metal","min":0}]}}}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"tables":{"p":{"levelCountBonus":-1}}}""")]
    // Фит: только известное снаряжение, только снаряжение, вменяемый шанс.
    [InlineData("""{"tables":{"p":{"fit":["nope"]}}}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"tables":{"p":{"fit":["metal"]}}}""")]
    [InlineData("""{"tables":{"p":{"gearChance":1.5}}}""")]
    [InlineData("""{"gearChance":2}""")]
    // Контейнеры: ровно одно из item и table, живые ссылки, разумное количество, внутри мира.
    [InlineData("""{"containers":[{"name":"Ящик","x":0,"y":0}]}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"tables":{"t":{}},"containers":[{"name":"Я","x":0,"y":0,"item":"metal","table":"t"}]}""")]
    [InlineData("""{"containers":[{"name":"Ящик","x":0,"y":0,"item":"nope"}]}""")]
    [InlineData("""{"containers":[{"name":"Ящик","x":0,"y":0,"table":"nope"}]}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"containers":[{"name":"","x":0,"y":0,"item":"metal"}]}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"containers":[{"name":"Я","x":0,"y":0,"item":"metal","count":0}]}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"containers":[{"name":"Я","x":99999,"y":0,"item":"metal"}]}""")]
    [InlineData("""{"items":{"metal":{"name":"М"}},"containers":[{"name":"Я","x":0,"y":0,"item":"metal","respawnSeconds":-1}]}""")]
    public void BrokenFiles_AreRejected(string json)
    {
        Assert.False(LootRules.TryParse(json, out var rules, out var error));
        Assert.NotNull(error);
        Assert.Same(LootRules.None, rules);
    }

    [Fact]
    public void RollWithChanceOne_AlwaysDrops()
    {
        var table = new LootTable([new LootRoll("metal", 1, 2, 2)]);
        var drop = Assert.Single(Roll(table, 1, Seq(0.999, 0)));
        Assert.Equal("metal", drop.Item);
        Assert.Equal(2, drop.Count);
    }

    [Fact]
    public void RollBelowTheChance_DropsNothing()
    {
        var table = new LootTable([new LootRoll("metal", 0.3)]);
        Assert.Empty(Roll(table, 1, Seq(0.3, 0)));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0.5, 4)]
    [InlineData(0.999, 5)]
    public void RollCount_StaysWithinMinAndMax(double dice, int expected)
    {
        var table = new LootTable([new LootRoll("metal", 1, 2, 5)]);
        Assert.Equal(expected, Assert.Single(Roll(table, 1, Seq(0, dice))).Count);
    }

    [Fact]
    public void Level_RaisesTheChance()
    {
        var table = new LootTable([new LootRoll("metal", 0.5)], LevelChanceBonus: 0.05);

        // 0.55 выше шанса первого уровня (0.5), но ниже шанса третьего (0.5 + 2 × 0.05).
        Assert.Empty(Roll(table, 1, Seq(0.55, 0)));
        Assert.Single(Roll(table, 3, Seq(0.55, 0)));
    }

    [Fact]
    public void Level_RaisesTheCount()
    {
        var table = new LootTable([new LootRoll("metal", 1, 4, 4)], LevelCountBonus: 0.2);
        Assert.Equal(4, Assert.Single(Roll(table, 1, Seq(0, 0))).Count);
        Assert.Equal(6, Assert.Single(Roll(table, 3, Seq(0, 0))).Count); // 4 × 1.4 = 5.6
    }

    [Fact]
    public void Roll_IsDeterministicForASeed()
    {
        var table = new LootTable(
            [new LootRoll("metal", 1, 2, 5), new LootRoll("tech", 0.5), new LootRoll("metal", 0.3, 1, 3)],
            LevelChanceBonus: 0.05,
            LevelCountBonus: 0.2);

        var first = Roll(table, 2, new Random(1).NextDouble);
        var second = Roll(table, 2, new Random(1).NextDouble);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Roll_FollowsTheChanceOverManyTries()
    {
        var table = new LootTable([new LootRoll("metal", 0.45)]);
        var rng = new Random(7).NextDouble;
        var hits = 0;
        const int tries = 10_000;
        for (var i = 0; i < tries; i++) hits += Roll(table, 1, rng).Count;
        Assert.InRange(hits / (double)tries, 0.43, 0.47);
    }

    private static List<(string Item, int Count)> RollFit(LootTable table, double chance, Func<double> rng)
    {
        var into = new List<(string, int)>();
        table.RollFit(chance, rng, into);
        return into;
    }

    /// <summary>Снаряжение противника: id — объём в трюме, как его строит Balance.</summary>
    private static readonly IReadOnlyDictionary<string, double> Gear = new Dictionary<string, double>
    {
        ["pulse"] = 2,
        ["shieldM"] = 4,
        ["railgun"] = 6,
    };

    [Fact]
    public void Fit_DropsEachPieceOnItsOwn()
    {
        var table = new LootTable(Fit: ["pulse", "shieldM", "railgun"]);

        // Монета у каждой своя: выпали первая и третья, вторая не прошла.
        var drop = RollFit(table, 0.5, Seq(0.4, 0.6, 0.1));
        Assert.Equal([("pulse", 1), ("railgun", 1)], drop);
    }

    [Fact]
    public void Fit_IgnoresTheLevelBonuses()
    {
        var table = new LootTable(Fit: ["pulse"], LevelChanceBonus: 0.05, LevelCountBonus: 0.2);

        // Прибавки за уровень разгоняли бы 3 % до 48 %, а одну пушку — до трёх штук. Фита они не касаются:
        // RollFit уровня вообще не знает, и матёрый пират роняет ровно то же, что новобранец.
        Assert.Empty(RollFit(table, 0.03, Seq(0.2)));
        Assert.Equal([("pulse", 1)], RollFit(table, 0.03, Seq(0.02)));
    }

    [Fact]
    public void Fit_PrefersTheTableChanceOverTheCommonOne()
    {
        var table = new LootTable(Fit: ["pulse"], GearChance: 1);
        Assert.Equal([("pulse", 1)], RollFit(table, 0, Seq(0.99)));
    }

    [Fact]
    public void GearInRolls_IsRejected()
    {
        // Снаряжению в rolls не место: там его разгоняют прибавки за уровень.
        const string json = """{"tables":{"p":{"rolls":[{"item":"pulse"}]}}}""";
        Assert.False(LootRules.TryParse(json, out _, out var error, gear: Gear));
        Assert.Contains("list it in fit", error);
    }

    [Fact]
    public void Gear_HasVolumeByItsClass()
    {
        Assert.Equal(2, LootRules.GearVolume(EquipClass.S));
        Assert.Equal(4, LootRules.GearVolume(EquipClass.M));
        Assert.Equal(6, LootRules.GearVolume(EquipClass.L));

        // Трофей занимает трюм, а грузом не торгуют: цена у него нулевая, её знает магазин.
        Assert.True(LootRules.TryParse("{}", out var rules, out var error, gear: Gear), error);
        Assert.Equal(4, rules.Volume("shieldM"));
        Assert.Equal(0, rules.Price("shieldM"));
        Assert.True(rules.IsGear("shieldM"));
    }

    [Fact]
    public void Table_ValidatesAgainstTheItems()
    {
        Assert.Null(new LootTable([new LootRoll("metal")]).Validate(Items));
        Assert.NotNull(new LootTable([new LootRoll("gold")]).Validate(Items));
    }

    [Fact]
    public void ContainerInsideTheShelter_IsRejected()
    {
        // Ящик в укрытии — бесплатный лут без риска ровно там, где все появляются.
        const string json = """{"items":{"metal":{"name":"Металл"}},"containers":[{"name":"Ящик","x":0,"y":500,"item":"metal"}]}""";

        Assert.False(LootRules.TryParse(json, out _, out var error, stationSafeRadius: 900));
        Assert.Contains("too close to the station", error);

        // Тот же файл без укрытия (тесты, старые сборки) разбирается.
        Assert.True(LootRules.TryParse(json, out var rules, out _));
        Assert.Single(rules.ContainerList);
    }

    [Fact]
    public void SharedLootJson_KeepsContainersOutOfTheShelter()
    {
        // Разбор всего набора проверяет контейнеры каждой системы против настоящего радиуса укрытия из npcs.json.
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        Assert.All(balance!.Galaxy.SystemMap.Keys, id => Assert.NotEmpty(balance.ForSystem(id).Loot.ContainerList));
    }

    [Fact]
    public void Hull_RejectsNegativeCargo()
    {
        Assert.Null((TestHulls.Light with { Cargo = 0 }).Validate());
        Assert.Equal("cargo must not be negative", (TestHulls.Light with { Cargo = -1 }).Validate());
    }

    [Fact]
    public void SharedLootJson_IsValid()
    {
        var json = System.IO.File.ReadAllText(Path.Combine(TestHulls.RepoRoot(), "shared", Balance.LootFile));
        // Таблицы Пограничья и Рубежа роняют снаряжение (M11) — его каталог нужен для проверки.
        Assert.True(LootRules.TryParse(json, out var rules, out var error, gear: TestHulls.SharedGear()), error);
        Assert.NotEmpty(rules.ItemMap);
        Assert.NotEmpty(rules.TableMap);

        // Каждый тип пирата из npcs.json должен уметь что-то ронять, иначе бой ничего не даёт.
        Assert.Contains("pirate", rules.TableMap.Keys);
    }

    [Fact]
    public void SharedLootJson_GivesEveryFighterAFitOfFourToSix()
    {
        var json = System.IO.File.ReadAllText(Path.Combine(TestHulls.RepoRoot(), "shared", Balance.LootFile));
        Assert.True(LootRules.TryParse(json, out var rules, out var error, gear: TestHulls.SharedGear()), error);

        string[] fighters = ["pirate", "heavyPirate", "frontierPirate", "frontierBrute", "rimPirate", "rimBrute", "ranger", "trader"];
        foreach (var id in fighters)
        {
            var fit = rules.TableMap[id].FitList;
            Assert.InRange(fit.Count, 4, 6);
            Assert.Equal(fit.Count, fit.Distinct().Count()); // два одинаковых модуля — это опечатка, а не фит
        }

        // Камни и ящики снаряжения не носят: его снимают с корабля.
        Assert.Empty(rules.TableMap["rockLarge"].FitList);
        Assert.Empty(rules.TableMap["container"].FitList);
    }

    [Fact]
    public void SharedLootJson_KeepsGearOutOfTheRolls()
    {
        var json = System.IO.File.ReadAllText(Path.Combine(TestHulls.RepoRoot(), "shared", Balance.LootFile));
        Assert.True(LootRules.TryParse(json, out var rules, out var error, gear: TestHulls.SharedGear()), error);

        // Строку дропа разгоняет уровень — снаряжения там быть не должно ни в одной таблице.
        foreach (var (id, table) in rules.TableMap)
            Assert.All(table.RollList, roll => Assert.True(rules.ItemMap.ContainsKey(roll.Item), $"{id}: {roll.Item}"));
    }

    [Fact]
    public void Balance_RejectsTheWholeSetWhenLootIsBroken()
    {
        Assert.False(Balance.TryParse(TestHulls.SharedSources() with { Loot = "not json" }, out var balance, out var error));
        Assert.Null(balance);
        Assert.StartsWith(Balance.LootFile, error);
    }

    [Fact]
    public void Balance_WithoutLoot_FallsBackToNone()
    {
        var balance = new Balance(new Dictionary<string, HullParams>(), new Dictionary<string, WeaponParams>(), new CombatRules());
        Assert.Same(LootRules.None, balance.Loot);
    }
}
