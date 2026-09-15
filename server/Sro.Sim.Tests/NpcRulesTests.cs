namespace Sro.Sim.Tests;

public class NpcRulesTests
{
    private static readonly Dictionary<string, HullParams> Hulls = new() { ["light"] = TestHulls.Light };
    private static readonly Dictionary<string, WeaponParams> Weapons = new() { ["pulse"] = TestWeapons.Pulse };

    private const string Pirate = """{ "name": "Пират", "hull": "light", "weapon": "pulse", "hp": 300, "shield": 100, "damage": 0.45 }""";

    private static string File(string spawns, string type = Pirate, string extra = "") =>
        $$"""{ {{extra}} "types": { "pirate": {{type}} }, "spawns": [ {{spawns}} ] }""";

    private static NpcRules Parse(string json)
    {
        Assert.True(NpcRules.TryParse(json, Hulls, Weapons, out var rules, out var error), error);
        return rules;
    }

    [Fact]
    public void EmptyFile_MeansNoPirates()
    {
        var rules = Parse("{}");
        Assert.Equal(0, rules.Count);
        Assert.Equal(20 * SimConfig.TickRate, rules.RespawnTicks);
    }

    [Fact]
    public void Lairs_CountAllMembers()
    {
        var rules = Parse(File("""{ "type": "pirate", "level": 1, "x": 0, "y": -2000, "count": 2 }, { "type": "pirate", "level": 2, "x": 2000, "y": 0 }"""));
        Assert.Equal(3, rules.Count);
    }

    [Fact]
    public void Level_ScalesHullShieldDamageAndAccuracy()
    {
        var rules = Parse(File("""{ "type": "pirate", "level": 3, "x": 0, "y": -2000 }"""));
        var type = rules.TypeMap["pirate"];

        Assert.Equal("Пират Ур.3", NpcRules.Name(type, 3));
        Assert.Equal(300 * 1.4, rules.MaxHp(type, 3, TestHulls.Light), 9);
        Assert.Equal(100 * 1.4, rules.MaxShield(type, 3, TestHulls.Light), 9);
        var weapon = rules.ScaledWeapon(type, 3, TestWeapons.Pulse);
        Assert.Equal(100 * 0.45 * 1.2, weapon.Damage, 9);
        Assert.Equal(75 + 4, weapon.Accuracy, 9);
        Assert.Equal(TestWeapons.Pulse.Cooldown, weapon.Cooldown);

        Assert.Equal(300, rules.MaxHp(type, 1, TestHulls.Light), 9);
        Assert.Equal(100 * 0.45, rules.ScaledWeapon(type, 1, TestWeapons.Pulse).Damage, 9);
    }

    [Fact]
    public void TypeWithoutOwnHp_TakesTheHullValues()
    {
        var rules = Parse(File("""{ "type": "pirate", "level": 1, "x": 0, "y": -2000 }""", """{ "name": "П", "hull": "light", "weapon": "pulse" }"""));
        var type = rules.TypeMap["pirate"];
        Assert.Equal(TestHulls.Light.Hp, rules.MaxHp(type, 1, TestHulls.Light));
        Assert.Equal(TestHulls.Light.Shield, rules.MaxShield(type, 1, TestHulls.Light));
    }

    [Theory]
    [InlineData("""{ "type": "nope", "level": 1, "x": 0, "y": -2000 }""", Pirate, "unknown type")]
    [InlineData("""{ "type": "pirate", "level": 1, "x": 0, "y": -2000 }""", """{ "name": "П", "hull": "nope", "weapon": "pulse" }""", "unknown hull")]
    [InlineData("""{ "type": "pirate", "level": 1, "x": 0, "y": -2000 }""", """{ "name": "П", "hull": "light", "weapon": "nope" }""", "unknown weapon")]
    [InlineData("""{ "type": "pirate", "level": 1, "x": 0, "y": -2000 }""", """{ "name": "П", "hull": "light", "weapon": "pulse", "retreatHp": 1 }""", "retreatHp")]
    [InlineData("""{ "type": "pirate", "level": 0, "x": 0, "y": -2000 }""", Pirate, "level")]
    [InlineData("""{ "type": "pirate", "level": 1, "x": 0, "y": -2000, "count": 0 }""", Pirate, "count")]
    [InlineData("""{ "type": "pirate", "level": 1, "x": 5000, "y": 0 }""", Pirate, "within")]
    // 900 укрытие + 250 патруль + 700 пушка = 1850: с 1500 игрок достаёт пирата из укрытия.
    [InlineData("""{ "type": "pirate", "level": 1, "x": 0, "y": -1500 }""", Pirate, "too close to the station")]
    public void BrokenFiles_AreRejected(string spawn, string type, string expected)
    {
        Assert.False(NpcRules.TryParse(File(spawn, type), Hulls, Weapons, out _, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void DropRange_MustNotBeShorterThanAggroRange()
    {
        Assert.False(NpcRules.TryParse("""{ "aggroRange": 800, "dropRange": 700 }""", Hulls, Weapons, out _, out var error));
        Assert.Contains("aggroRange", error);
    }

    [Fact]
    public void Balance_RejectsTheWholeSetWhenNpcsAreBroken()
    {
        var dir = Path.Combine(TestHulls.RepoRoot(), "shared");
        string Read(string file) => System.IO.File.ReadAllText(Path.Combine(dir, file));

        Assert.False(Balance.TryParse(Read(Balance.HullsFile), Read(Balance.WeaponsFile), Read(Balance.RulesFile), "not json", out var balance, out var error));
        Assert.Null(balance);
        Assert.StartsWith(Balance.NpcsFile, error);
    }
}
