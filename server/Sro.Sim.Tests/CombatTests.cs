namespace Sro.Sim.Tests;

public class CombatTests
{
    private static readonly WeaponParams Pulse = TestWeapons.Pulse;
    private static readonly HullParams Light = TestHulls.Light;

    private static (double Dx, double Dy) Towards(double bearingDeg, double distance = 300)
    {
        var b = bearingDeg * Math.PI / 180;
        return (Math.Sin(b) * distance, -Math.Cos(b) * distance);
    }

    [Fact]
    public void Evasion_GrowsWithSpeedUpToMoveEvasion()
    {
        Assert.Equal(25, Combat.Evasion(Light, 0), 12);
        Assert.Equal(29, Combat.Evasion(Light, Light.MaxSpeed / 2), 12);
        Assert.Equal(33, Combat.Evasion(Light, Light.MaxSpeed), 12);
        Assert.Equal(33, Combat.Evasion(Light, Light.MaxSpeed * 2), 12); // быстрее максимума (сменили корпус на ходу)
    }

    [Fact]
    public void RangePenalty_IsZeroUpToOptimalThenGrowsLinearly()
    {
        Assert.Equal(0, Combat.RangePenalty(Pulse, 0));
        Assert.Equal(0, Combat.RangePenalty(Pulse, 500));
        Assert.Equal(5, Combat.RangePenalty(Pulse, 600), 12);
        Assert.Equal(10, Combat.RangePenalty(Pulse, 700), 12);
        Assert.Equal(10, Combat.RangePenalty(Pulse, 900), 12);
    }

    [Fact]
    public void InRange_EndsAtMaxRange()
    {
        Assert.True(Combat.InRange(Pulse, 700));
        Assert.False(Combat.InRange(Pulse, 700.01));
    }

    [Fact]
    public void HitChance_FollowsTheGddExample()
    {
        // GDD §46: точность 85 − уклонение 20 − штраф 10 = 55%.
        var weapon = new WeaponParams("x", 100, 85, 1, 500, 700, 10);
        var target = Light with { Evasion = 20, MoveEvasion = 0 };
        Assert.Equal(55, Combat.HitChance(weapon, 700, target, 0), 12);
    }

    [Fact]
    public void HitChance_LightAtFullSpeedIsHarderToHit()
    {
        Assert.Equal(50, Combat.HitChance(Pulse, 300, Light, 0), 12);
        Assert.Equal(42, Combat.HitChance(Pulse, 300, Light, Light.MaxSpeed), 12);
        Assert.Equal(70, Combat.HitChance(Pulse, 300, TestHulls.Heavy, 0), 12);
    }

    [Theory]
    [InlineData(100, 0, 95)]
    [InlineData(10, 25, 5)]
    public void HitChance_IsClampedTo5And95(double accuracy, double evasion, double expected)
    {
        var weapon = Pulse with { Accuracy = accuracy };
        var target = Light with { Evasion = evasion, MoveEvasion = 0 };
        Assert.Equal(expected, Combat.HitChance(weapon, 0, target, 0));
    }

    [Fact]
    public void IsHit_ComparesTheRollWithTheChance()
    {
        Assert.True(Combat.IsHit(42, 0.419));
        Assert.False(Combat.IsHit(42, 0.42));
        Assert.True(Combat.IsHit(5, 0));
        Assert.False(Combat.IsHit(95, 0.95));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 60, true)]
    [InlineData(0, -60, true)]
    [InlineData(0, 61, false)]
    [InlineData(0, -61, false)]
    [InlineData(0, 180, false)]
    [InlineData(170, -170, true)] // через ±180°
    [InlineData(-170, 175, true)]
    [InlineData(90, 149, true)]
    [InlineData(90, 151, false)]
    public void InArc_ChecksTheAngleFromTheNose(double rotDeg, double bearingDeg, bool expected)
    {
        var (dx, dy) = Towards(bearingDeg);
        Assert.Equal(expected, Combat.InArc(rotDeg * Math.PI / 180, dx, dy, 60));
    }

    [Fact]
    public void InArc_ShipsInTheSameSpotAreInArc()
    {
        Assert.True(Combat.InArc(2, 0, 0, 60));
    }

    [Fact]
    public void ApplyDamage_TakesShieldFirstThenHull()
    {
        double hp = 400, shield = 150;

        Assert.Equal(new DamageResult(100, 0), Combat.ApplyDamage(ref hp, ref shield, 100));
        Assert.Equal((400.0, 50.0), (hp, shield));

        Assert.Equal(new DamageResult(50, 50), Combat.ApplyDamage(ref hp, ref shield, 100));
        Assert.Equal((350.0, 0.0), (hp, shield));

        Assert.Equal(new DamageResult(0, 350), Combat.ApplyDamage(ref hp, ref shield, 1000));
        Assert.Equal((0.0, 0.0), (hp, shield));
    }

    [Theory]
    [InlineData(1.0, 20)]
    [InlineData(0.5, 10)]
    [InlineData(2.0, 40)]
    [InlineData(1.23, 25)]
    [InlineData(0.01, 1)]
    public void CooldownTicks_RoundsUpToWholeTicks(double cooldown, int expected)
    {
        Assert.Equal(expected, Combat.CooldownTicks(Pulse with { Cooldown = cooldown }));
    }

    [Fact]
    public void SharedBalanceFiles_Parse()
    {
        var dir = Path.Combine(TestHulls.RepoRoot(), "shared");
        string Read(string file) => File.ReadAllText(Path.Combine(dir, file));

        Assert.True(
            Balance.TryParse(Read(Balance.HullsFile), Read(Balance.WeaponsFile), Read(Balance.RulesFile), out var balance, out var error),
            error);
        Assert.Contains(SimConfig.DefaultWeapon, balance!.Weapons.Keys);
        Assert.All(balance.Hulls.Values, h => Assert.True(h.Hp > 0));
        Assert.NotEmpty(balance.Rules.DroneList);
    }

    [Fact]
    public void HullCombatFields_HaveDefaultsWhenMissing()
    {
        const string json = """{ "light": { "name": "x", "maxSpeed": 1, "acceleration": 1, "brakeAcceleration": 1, "turnRate": 1, "lateralDampTime": 1, "lateralToForward": 0, "size": 1 } }""";
        Assert.True(HullCatalog.TryParse(json, out var hulls, out var error), error);
        Assert.True(hulls["light"].Hp > 0);
        Assert.Null(hulls["light"].Validate());
    }

    [Theory]
    [InlineData("""{ "laser": { "name": "x", "damage": 1, "accuracy": 50, "cooldown": 1, "optimalRange": 1, "maxRange": 2, "rangePenalty": 0 } }""")]
    [InlineData("""{ "pulse": { "name": "x", "damage": 1, "accuracy": 150, "cooldown": 1, "optimalRange": 1, "maxRange": 2, "rangePenalty": 0 } }""")]
    [InlineData("""{ "pulse": { "name": "x", "damage": 1, "accuracy": 50, "cooldown": 1, "optimalRange": 3, "maxRange": 2, "rangePenalty": 0 } }""")]
    [InlineData("""{ "pulse": { "name": "x", "damage": 1, "accuracy": 50, "cooldown": 0, "optimalRange": 1, "maxRange": 2, "rangePenalty": 0 } }""")]
    [InlineData("not json")]
    public void WeaponCatalog_RejectsBrokenFiles(string json)
    {
        Assert.False(WeaponCatalog.TryParse(json, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void CombatRules_UseDefaultsForMissingFields()
    {
        var hulls = new Dictionary<string, HullParams> { ["light"] = Light };
        Assert.True(CombatRules.TryParse("{}", hulls, out var rules, out var error), error);
        Assert.Equal(10 * SimConfig.TickRate, rules.RespawnTicks);
        Assert.Empty(rules.DroneList);
    }

    [Fact]
    public void CombatRules_RejectDroneWithUnknownHull()
    {
        var hulls = new Dictionary<string, HullParams> { ["light"] = Light };
        Assert.False(CombatRules.TryParse("""{ "drones": [ { "name": "d", "hull": "nope", "x": 0, "y": 0 } ] }""", hulls, out _, out var error));
        Assert.Contains("nope", error);
    }
}
