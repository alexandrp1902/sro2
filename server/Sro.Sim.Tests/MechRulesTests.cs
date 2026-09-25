using Sro.Sim.Mech;

namespace Sro.Sim.Tests;

/// <summary>Мехи в тестах: числа документа баланса (§8–9, §16, §38), не shared/mechs.json — тюнинг не ломает тесты.</summary>
internal static class TestMechs
{
    public static readonly MechWeapon Autocannon = new("Автопушка", 240, 82, 2, 3, 5, 7, 600, 15);
    public static readonly MechWeapon Shotgun = new("Дробовик", 330, 85, 1, 1, 2, 4, 700, 20, "flak");
    public static readonly MechShield Light = new("Лёгкий щит", 1100, 25, 30);

    public static MechRules Rules(string[] map, MechSpawn[] player, MechSpawn[] enemy) => new(
        Bodies: new Dictionary<string, MechFrame> { ["medium"] = new("Средний", 2200, 25) },
        Chassis: new Dictionary<string, MechFrame> { ["medium"] = new("Среднее", 1100, 20, 4) },
        Weapons: new Dictionary<string, MechWeapon> { ["autocannon"] = Autocannon, ["shotgun"] = Shotgun },
        Shields: new Dictionary<string, MechShield> { ["light"] = Light },
        Units: new Dictionary<string, MechUnitDef>
        {
            ["proto"] = new("Прототип", "medium", "medium", "light", "autocannon"),
            ["raider"] = new("Налётчик", "medium", "medium", "light", "autocannon"),
            ["shotgun"] = new("Дробовик", "medium", "medium", "light", "shotgun"),
            ["bare"] = new("Голый", "medium", "medium", null, "autocannon"),
        },
        Missions: new Dictionary<string, MechMission> { ["test"] = new("Тест", "Цель", map, player, enemy) });

    public static readonly string[] Open =
    [
        "............",
        "............",
        "............",
        "............",
        "............",
        "............",
        "............",
        "............",
        "............",
        "............",
        "............",
        "............",
    ];
}

public class MechRulesTests
{
    private static readonly MechCombatDef C = new();

    [Fact]
    public void SharedMechsParse_AndHaveTheFirstSortie()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        var mission = balance!.Mechs.Mission(MechRules.FirstSortie);
        Assert.NotNull(mission);
        Assert.True(mission!.Reward > 0);
        // Бой ровно такой, каким его задумал план: один прототип против двух налётчиков на поле 12×12.
        Assert.Single(mission.Player);
        Assert.Equal(2, mission.Enemy.Length);
        Assert.Equal(12, mission.Map.Length);
    }

    /// <summary>Пример §21: 82 + 10 (стоял) + 10 (бок) − 10 (на две клетки дальше оптимума) = 92.</summary>
    [Fact]
    public void HitChance_MatchesTheWorkedExample()
    {
        Assert.Equal(92, MechCombat.HitChance(C, TestMechs.Autocannon, 7, 0, 4, MechSide.Left, aimed: false));
        // Прицельный — минус 25 (§27), и шанс не бывает меньше 20 и больше 95 (§20).
        Assert.Equal(67, MechCombat.HitChance(C, TestMechs.Autocannon, 7, 0, 4, MechSide.Left, aimed: true));
        Assert.Equal(95, MechCombat.HitChance(C, TestMechs.Shotgun, 1, 0, 4, MechSide.Rear, aimed: false));
        Assert.Equal(20, MechCombat.HitChance(C with { AimedPenalty = 90 }, TestMechs.Autocannon, 7, 4, 4, MechSide.Front, aimed: true));
    }

    [Fact]
    public void HitChance_RangePenaltyStopsAtTwentyFive_AndMovementCountsHalfTheMove()
    {
        // Дробовик на 4 клетках: на две дальше оптимума 1–2 — −10; автопушка в упор на 2 — −5.
        Assert.Equal(85 + 10 - 10, MechCombat.HitChance(C, TestMechs.Shotgun, 4, 0, 4, MechSide.Front, false));
        Assert.Equal(82 + 10 - 5, MechCombat.HitChance(C, TestMechs.Autocannon, 2, 0, 4, MechSide.Front, false));
        var far = new MechWeapon("Далёкая", 100, 80, 1, 1, 1, 20, 100, 0);
        Assert.Equal(80 + 10 - 25, MechCombat.HitChance(C, far, 20, 0, 4, MechSide.Front, false));
        // Прошёл половину хода — без штрафа, больше половины — −10 (§18).
        Assert.Equal(82, MechCombat.HitChance(C, TestMechs.Autocannon, 4, 2, 4, MechSide.Front, false));
        Assert.Equal(72, MechCombat.HitChance(C, TestMechs.Autocannon, 4, 3, 4, MechSide.Front, false));
    }

    [Fact]
    public void Armor_TwentyFiveTakesAFifth()
    {
        Assert.Equal(80, MechCombat.Damage(100, 25));
        Assert.Equal(67, MechCombat.Damage(100, 50));
        Assert.Equal(50, MechCombat.Damage(100, 100));
        var (min, max) = MechCombat.DamageRange(C, TestMechs.Autocannon, [15, 25]);
        Assert.Equal(MechCombat.Damage(240 * 0.9, 25), min);
        Assert.Equal(MechCombat.Damage(240 * 1.1, 15), max);
    }

    [Theory]
    [InlineData(1100, 4)]
    [InlineData(551, 4)]
    [InlineData(550, 3)]
    [InlineData(276, 3)]
    [InlineData(275, 2)]
    [InlineData(1, 2)]
    [InlineData(0, 0)]
    public void DamagedChassis_SlowsTheMech(int hp, int move)
    {
        Assert.Equal(move, MechCombat.MoveRange(4, hp, 1100));
    }

    [Fact]
    public void DamagedChassis_NeverTakesTheLastStepAwayWhileItStands()
    {
        Assert.Equal(1, MechCombat.MoveRange(2, 100, 1100));
    }

    [Theory]
    [InlineData(5, 2, "Front")]
    [InlineData(5, 8, "Rear")]
    [InlineData(8, 5, "Right")]
    [InlineData(2, 5, "Left")]
    [InlineData(8, 2, "Right")] // ровно на диагонали — бок, а не фронт
    [InlineData(2, 8, "Left")]
    [InlineData(6, 1, "Front")]
    public void Side_IsOneOfFourSectors(int ax, int ay, string side)
    {
        Assert.Equal(Enum.Parse<MechSide>(side), MechCombat.Side(ax, ay, 5, 5, targetDir: 0));
    }

    [Fact]
    public void Side_TurnsWithTheTarget()
    {
        // Цель смотрит на восток: стрелок с севера — у неё слева, с запада — сзади.
        Assert.Equal(MechSide.Left, MechCombat.Side(5, 2, 5, 5, 2));
        Assert.Equal(MechSide.Rear, MechCombat.Side(2, 5, 5, 5, 2));
        // Цель смотрит на северо-восток: стрелок прямо к северо-востоку — фронт.
        Assert.Equal(MechSide.Front, MechCombat.Side(8, 2, 5, 5, 1));
    }

    [Fact]
    public void Shield_HoldsItsOwnSideBest_AndNeverTheRear()
    {
        Assert.Equal(30, MechCombat.BlockChance(C, TestMechs.Light, MechPart.Left, true, MechSide.Front));
        Assert.Equal(45, MechCombat.BlockChance(C, TestMechs.Light, MechPart.Left, true, MechSide.Left));
        Assert.Equal(5, MechCombat.BlockChance(C, TestMechs.Light, MechPart.Left, true, MechSide.Right));
        Assert.Equal(0, MechCombat.BlockChance(C, TestMechs.Light, MechPart.Left, true, MechSide.Rear));
        Assert.Equal(0, MechCombat.BlockChance(C, TestMechs.Light, MechPart.Left, false, MechSide.Left));
        // Щит в правой руке — зеркально.
        Assert.Equal(45, MechCombat.BlockChance(C, TestMechs.Light, MechPart.Right, true, MechSide.Right));
    }

    [Fact]
    public void PartWeights_MirrorTheRightSide_AndMoveBrokenPartsIntoTheBody()
    {
        int[] whole = [1, 1, 1, 1];
        Assert.Equal([35, 10, 35, 20], MechCombat.PartWeights(C, MechSide.Right, whole));
        Assert.Equal([35 + 35, 0, 10, 20], MechCombat.PartWeights(C, MechSide.Left, [1, 0, 1, 1]));
        Assert.Equal([100, 0, 0, 0], MechCombat.PartWeights(C, MechSide.Rear, [1, 0, 0, 0]));
    }

    [Fact]
    public void Reach_DoesNotCutCorners_OrWalkThroughMechs()
    {
        var corner = new MechField([".w", ".."]);
        // Из угла на один шаг: вниз можно, по диагонали мимо стены — нельзя.
        Assert.Equal([corner.Index(0, 1)], corner.Reach(0, 0, 1, new HashSet<int>()).Keys);
        Assert.Equal(2, corner.Path(0, 0, 1, 1, 4, new HashSet<int>())!.Count);

        var open = new MechField(["...", "...", "..."]);
        // Занятая клетка — не стена: сквозь неё не пройти, но угол мимо неё срезать можно.
        var around = open.Reach(0, 0, 4, new HashSet<int> { open.Index(1, 0) });
        Assert.False(around.ContainsKey(open.Index(1, 0)));
        Assert.Equal(2, around[open.Index(2, 0)]);
    }

    [Fact]
    public void LineOfFire_WallStops_CrateDoesNot()
    {
        var field = new MechField(
        [
            ".....",
            "..w..",
            ".....",
            "..c..",
            ".....",
        ]);
        Assert.False(field.LineOfFire(0, 1, 4, 1));
        Assert.True(field.LineOfFire(0, 3, 4, 3));
        // Из (0,0) в (2,1) середина пути лежит ровно между (1,0) и (1,1): закрыто, только если закрыты обе.
        Assert.True(new MechField(["..w", "...", "..."]).LineOfFire(0, 0, 2, 1));
        Assert.True(new MechField([".w.", "...", "..."]).LineOfFire(0, 0, 2, 1));
        Assert.False(new MechField([".w.", ".w.", "..."]).LineOfFire(0, 0, 2, 1));
    }

    [Fact]
    public void LineOfFire_IsSymmetric_OnTheRealMap()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        var field = new MechField(balance!.Mechs.Mission(MechRules.FirstSortie)!.Map);
        for (var a = 0; a < field.Width * field.Height; a++)
        for (var b = a + 1; b < field.Width * field.Height; b++)
        {
            var (ax, ay, bx, by) = (a % field.Width, a / field.Width, b % field.Width, b / field.Width);
            Assert.Equal(field.LineOfFire(ax, ay, bx, by), field.LineOfFire(bx, by, ax, ay));
        }
    }

    [Theory]
    [InlineData(0, -3, 0)]
    [InlineData(3, -3, 1)]
    [InlineData(3, 0, 2)]
    [InlineData(1, 2, 3)]
    [InlineData(0, 5, 4)]
    [InlineData(-2, 1, 5)]
    [InlineData(-1, 0, 6)]
    [InlineData(-1, -1, 7)]
    [InlineData(1, -5, 0)]
    public void Direction_PicksTheNearestOfEight(int dx, int dy, int dir)
    {
        Assert.Equal(dir, MechField.Direction(dx, dy));
    }

    [Fact]
    public void Validate_CatchesBrokenBuildings_Weights_AndSpawns()
    {
        MechSpawn[] p = [new("proto", 0, 0, 0)];
        MechSpawn[] e = [new("raider", 3, 3, 0)];
        Assert.Null(TestMechs.Rules(TestMechs.Open, p, e).Validate());
        Assert.Contains("2x2", TestMechs.Rules(["b...", "....", "....", "...."], p, e).Validate());
        Assert.Contains("blocked", TestMechs.Rules(["w...", "....", "....", "...."], p, e).Validate());
        Assert.Contains("same length", TestMechs.Rules(["....", "...", "....", "...."], p, e).Validate());
        Assert.Contains("unknown map cell", TestMechs.Rules(["x...", "....", "....", "...."], p, e).Validate());
        var badWeights = TestMechs.Rules(TestMechs.Open, p, e) with { Combat = new MechCombatDef(PartsFront: [50, 20, 20, 20]) };
        Assert.Contains("partsFront", badWeights.Validate());
        Assert.Contains("two mechs", TestMechs.Rules(TestMechs.Open, p, [new("raider", 0, 0, 0)]).Validate());
    }
}
