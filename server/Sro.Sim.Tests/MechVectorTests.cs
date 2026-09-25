using System.Text;
using System.Text.Json;
using Sro.Sim.Mech;

namespace Sro.Sim.Tests;

/// <summary>
/// Эталоны shared/test-vectors/mech.json (M21): прогноз боя на клиенте (client/src/mech/rules.ts) проверяется
/// по ним же, так что «Попадание 67 % · урон 190–230» на экране — это ровно то, что посчитает сервер.
/// Броски сюда не входят: их делает только сервер.
/// Перегенерация: SRO_UPDATE_VECTORS=1 dotnet test.
/// </summary>
public class MechVectorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record HitCase(string Weapon, int Distance, int Steps, int MoveRange, string Side, bool Aimed, int Chance);

    private sealed record SideCase(int Ax, int Ay, int Tx, int Ty, int Dir, string Side);

    private sealed record BlockCase(string ShieldArm, bool Alive, string Side, int Chance);

    private sealed record DamageCase(string Weapon, int[] Armors, int Min, int Max);

    private sealed record MoveCase(int Base, int Hp, int Max, int Range);

    private sealed record DirCase(int Dx, int Dy, int Dir);

    private sealed record WeightCase(string Side, int[] Hp, int[] Weights);

    /// <summary>Достижимые клетки: пары [индекс, шагов], по индексу.</summary>
    private sealed record ReachCase(int X, int Y, int Range, int[] Occupied, int[][] Cells);

    /// <summary>Линия огня из (x, y) во все клетки поля: строка из «1» и «0» по индексу клетки.</summary>
    private sealed record LineRow(int X, int Y, string Clear);

    private sealed record VectorFile(
        MechCombatDef Combat,
        Dictionary<string, MechWeapon> Weapons,
        MechShield Shield,
        string[] Map,
        HitCase[] Hit,
        SideCase[] Side,
        BlockCase[] Block,
        DamageCase[] Damage,
        MoveCase[] Move,
        DirCase[] Dir,
        WeightCase[] Weights,
        ReachCase[] Reach,
        LineRow[] Line);

    /// <summary>Свои числа, не из shared/mechs.json: эталон не должен ездить от тюнинга.</summary>
    private static readonly MechCombatDef Combat = new(
        PartsFront: [45, 20, 20, 15], PartsLeft: [35, 35, 10, 20], PartsRear: [55, 15, 15, 15]);

    private static readonly Dictionary<string, MechWeapon> Weapons = new()
    {
        ["autocannon"] = TestMechs.Autocannon,
        ["shotgun"] = TestMechs.Shotgun,
    };

    /// <summary>Поле с кусками всего: стены вплотную (срез угла), здание, ящики и камни — простреливаемые.</summary>
    private static readonly string[] Map =
    [
        "......,.....",
        "..c.....r...",
        "....ww......",
        ".bb.....,.c.",
        ".bb..r......",
        ".......ww...",
        "..,.c.......",
        "......bb..r.",
        "..r...bb....",
        ".ww.....c...",
        "....w.,.....",
        "..,.w.......",
    ];

    private static string VectorPath() => Path.Combine(TestHulls.RepoRoot(), "shared", "test-vectors", "mech.json");

    [Fact]
    public void MechMatchesSharedVectors()
    {
        var generated = Generate();
        var path = VectorPath();
        if (Environment.GetEnvironmentVariable("SRO_UPDATE_VECTORS") == "1")
        {
            File.WriteAllText(path, Format(generated));
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing: run 'SRO_UPDATE_VECTORS=1 dotnet test'");
        var stored = JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(path), Json)!;
        // Сверяем не файл с файлом, а сохранённые случаи с формулами сейчас: так видно, какая формула уехала.
        Assert.Equal(Format(generated), Format(Recompute(stored)));
    }

    private static VectorFile Recompute(VectorFile s)
    {
        var field = new MechField(s.Map);
        return s with
        {
            Hit = [.. s.Hit.Select(c => c with { Chance = MechCombat.HitChance(s.Combat, s.Weapons[c.Weapon], c.Distance, c.Steps, c.MoveRange, Enum.Parse<MechSide>(c.Side, true), c.Aimed) })],
            Side = [.. s.Side.Select(c => c with { Side = SideName(MechCombat.Side(c.Ax, c.Ay, c.Tx, c.Ty, c.Dir)) })],
            Block = [.. s.Block.Select(c => c with { Chance = MechCombat.BlockChance(s.Combat, s.Shield, MechCombat.ParsePart(c.ShieldArm)!.Value, c.Alive, Enum.Parse<MechSide>(c.Side, true)) })],
            Damage = [.. s.Damage.Select(c => DamageOf(s.Combat, c.Weapon, s.Weapons[c.Weapon], c.Armors))],
            Move = [.. s.Move.Select(c => c with { Range = MechCombat.MoveRange(c.Base, c.Hp, c.Max) })],
            Dir = [.. s.Dir.Select(c => c with { Dir = MechField.Direction(c.Dx, c.Dy) })],
            Weights = [.. s.Weights.Select(c => c with { Weights = MechCombat.PartWeights(s.Combat, Enum.Parse<MechSide>(c.Side, true), c.Hp) })],
            Reach = [.. s.Reach.Select(c => ReachOf(field, c.X, c.Y, c.Range, c.Occupied))],
            Line = [.. s.Line.Select(c => LineOf(field, c.X, c.Y))],
        };
    }

    private static string SideName(MechSide side) => MechCombat.SideNames[(int)side];

    private static DamageCase DamageOf(MechCombatDef c, string id, MechWeapon w, int[] armors)
    {
        var (min, max) = MechCombat.DamageRange(c, w, armors);
        return new DamageCase(id, armors, min, max);
    }

    private static ReachCase ReachOf(MechField field, int x, int y, int range, int[] occupied)
    {
        var reach = field.Reach(x, y, range, occupied.ToHashSet());
        return new ReachCase(x, y, range, occupied, [.. reach.OrderBy(p => p.Key).Select(p => new[] { p.Key, p.Value })]);
    }

    private static LineRow LineOf(MechField field, int x, int y)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < field.Width * field.Height; i++)
            sb.Append(field.LineOfFire(x, y, i % field.Width, i / field.Width) ? '1' : '0');
        return new LineRow(x, y, sb.ToString());
    }

    private static VectorFile Generate()
    {
        var sides = MechCombat.SideNames;
        var hit = new List<HitCase>();
        foreach (var (id, w) in Weapons)
        foreach (var distance in new[] { 1, 2, 3, 5, 6, 7, 12 })
        foreach (var steps in new[] { 0, 2, 3 })
        foreach (var side in sides)
        foreach (var aimed in new[] { false, true })
        {
            hit.Add(new HitCase(id, distance, steps, 4, side,
                aimed, MechCombat.HitChance(Combat, w, distance, steps, 4, Enum.Parse<MechSide>(side, true), aimed)));
        }

        var side8 = new List<SideCase>();
        foreach (var dir in Enumerable.Range(0, 8))
        foreach (var (ax, ay) in new[] { (5, 1), (8, 2), (9, 5), (8, 8), (5, 9), (2, 8), (1, 5), (2, 2), (6, 2), (7, 9), (4, 5) })
            side8.Add(new SideCase(ax, ay, 5, 5, dir, SideName(MechCombat.Side(ax, ay, 5, 5, dir))));

        var block = new List<BlockCase>();
        foreach (var arm in new[] { "left", "right" })
        foreach (var alive in new[] { true, false })
        foreach (var side in sides)
            block.Add(new BlockCase(arm, alive, side, MechCombat.BlockChance(Combat, TestMechs.Light, MechCombat.ParsePart(arm)!.Value, alive, Enum.Parse<MechSide>(side, true))));

        var damage = new List<DamageCase>();
        foreach (var (id, w) in Weapons)
        foreach (var armors in new[] { new[] { 25 }, [15, 20, 25], [0], [50, 100] })
            damage.Add(DamageOf(Combat, id, w, armors));

        var move = new List<MoveCase>();
        foreach (var hp in new[] { 1100, 551, 550, 276, 275, 1, 0 })
            move.Add(new MoveCase(4, hp, 1100, MechCombat.MoveRange(4, hp, 1100)));
        move.Add(new MoveCase(2, 100, 1100, MechCombat.MoveRange(2, 100, 1100)));

        var dirs = new List<DirCase>();
        for (var dy = -3; dy <= 3; dy++)
        for (var dx = -3; dx <= 3; dx++)
            dirs.Add(new DirCase(dx, dy, MechField.Direction(dx, dy)));
        dirs.Add(new DirCase(1, -11, MechField.Direction(1, -11)));
        dirs.Add(new DirCase(-11, 5, MechField.Direction(-11, 5)));

        var weights = new List<WeightCase>();
        foreach (var side in sides)
        foreach (var hp in new[] { new[] { 1, 1, 1, 1 }, [1, 0, 1, 1], [1, 1, 0, 0] })
            weights.Add(new WeightCase(side, hp, MechCombat.PartWeights(Combat, Enum.Parse<MechSide>(side, true), hp)));

        var field = new MechField(Map);
        ReachCase[] reach =
        [
            ReachOf(field, 1, 10, 4, []),
            ReachOf(field, 3, 10, 4, [field.Index(3, 9)]),
            ReachOf(field, 5, 3, 3, []),
            ReachOf(field, 10, 1, 2, [field.Index(9, 2)]),
            ReachOf(field, 0, 0, 1, []),
        ];
        LineRow[] line =
        [
            LineOf(field, 1, 10),
            LineOf(field, 10, 1),
            LineOf(field, 5, 6),
            LineOf(field, 0, 5),
            LineOf(field, 3, 4),
            LineOf(field, 8, 8),
        ];

        return new VectorFile(Combat, Weapons, TestMechs.Light, Map,
            [.. hit], [.. side8], [.. block], [.. damage], [.. move], [.. dirs], [.. weights], reach, line);
    }

    /// <summary>По строке на случай — чтобы diff файла читался.</summary>
    private static string Format(VectorFile file)
    {
        var sb = new StringBuilder("{\n");
        sb.Append("  \"combat\": ").Append(JsonSerializer.Serialize(file.Combat, Json)).Append(",\n");
        sb.Append("  \"weapons\": ").Append(JsonSerializer.Serialize(file.Weapons, Json)).Append(",\n");
        sb.Append("  \"shield\": ").Append(JsonSerializer.Serialize(file.Shield, Json)).Append(",\n");
        AppendList(sb, "map", file.Map, last: false);
        AppendList(sb, "hit", file.Hit, last: false);
        AppendList(sb, "side", file.Side, last: false);
        AppendList(sb, "block", file.Block, last: false);
        AppendList(sb, "damage", file.Damage, last: false);
        AppendList(sb, "move", file.Move, last: false);
        AppendList(sb, "dir", file.Dir, last: false);
        AppendList(sb, "weights", file.Weights, last: false);
        AppendList(sb, "reach", file.Reach, last: false);
        AppendList(sb, "line", file.Line, last: true);
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendList<T>(StringBuilder sb, string name, T[] items, bool last)
    {
        sb.Append("  \"").Append(name).Append("\": [\n");
        for (var i = 0; i < items.Length; i++)
            sb.Append("    ").Append(JsonSerializer.Serialize(items[i], Json)).Append(i == items.Length - 1 ? "\n" : ",\n");
        sb.Append(last ? "  ]\n" : "  ],\n");
    }
}
