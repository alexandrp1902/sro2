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
