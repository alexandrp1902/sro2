using System.Text;
using System.Text.Json;

namespace Sro.Sim.Tests;

/// <summary>
/// Эталоны shared/test-vectors/combat.json: по ним же проверяется TS-формула клиента (Vitest), так что карточка
/// цели показывает тот же шанс, по которому бросает сервер. Перегенерация: SRO_UPDATE_VECTORS=1 dotnet test.
/// </summary>
public class CombatVectorTests
{
    private const double Tolerance = 1e-9;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица без \u — diff читается
    };

    private sealed record ChanceCase(string Weapon, string Hull, double Distance, double Speed, double Chance, bool InRange);

    private sealed record ArcCase(double Rot, double Dx, double Dy, double Arc, bool InArc);

    private sealed record VectorFile(
        Dictionary<string, WeaponParams> Weapons,
        Dictionary<string, HullParams> Hulls,
        ChanceCase[] HitChance,
        ArcCase[] Arc);

    private static string VectorPath() => Path.Combine(TestHulls.RepoRoot(), "shared", "test-vectors", "combat.json");

    [Fact]
    public void CombatMatchesSharedVectors()
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
        Assert.Equal(generated.HitChance.Length, stored.HitChance.Length);
        foreach (var (expected, actual) in stored.HitChance.Zip(generated.HitChance))
        {
            Assert.Equal((expected.Weapon, expected.Hull, expected.Distance, expected.Speed), (actual.Weapon, actual.Hull, actual.Distance, actual.Speed));
            Assert.True(Math.Abs(expected.Chance - actual.Chance) <= Tolerance, $"{expected}: {actual.Chance}");
            Assert.Equal(expected.InRange, actual.InRange);
        }
        Assert.Equal(stored.Arc, generated.Arc);
    }

    private static VectorFile Generate()
    {
        var weapons = new Dictionary<string, WeaponParams>
        {
            ["pulse"] = TestWeapons.Pulse,
            ["laser"] = TestWeapons.Laser,
            ["plasma"] = TestWeapons.Plasma,
        };
        var hulls = new Dictionary<string, HullParams>
        {
            ["light"] = TestHulls.Light,
            ["medium"] = TestHulls.Medium,
            ["heavy"] = TestHulls.Heavy,
        };

        var chances = new List<ChanceCase>();
        foreach (var (weaponId, weapon) in weapons)
            foreach (var (hullId, hull) in hulls)
                foreach (var distance in new[] { 0, 450, 550, 640, 700, 750 })
                    foreach (var speedRatio in new[] { 0, 0.37, 1, 1.3 })
                    {
                        var speed = hull.MaxSpeed * speedRatio;
                        chances.Add(new ChanceCase(weaponId, hullId, distance, speed,
                            Combat.HitChance(weapon, distance, hull, speed), Combat.InRange(weapon, distance)));
                    }

        var arcs = new List<ArcCase> { new(1, 0, 0, 60, Combat.InArc(1, 0, 0, 60)) };
        foreach (var rot in new[] { 0, 1, -3, 3.1, Math.PI / 2 })
            foreach (var offsetDeg in new[] { 0, 30, 59.9, 60, 60.1, 90, 180, -59.9, -60, -60.1, -120 })
            {
                var bearing = rot + offsetDeg * Math.PI / 180;
                var dx = Math.Sin(bearing) * 300;
                var dy = -Math.Cos(bearing) * 300;
                arcs.Add(new ArcCase(rot, dx, dy, 60, Combat.InArc(rot, dx, dy, 60)));
            }
        arcs.Add(new ArcCase(0, 1, -1, 45, Combat.InArc(0, 1, -1, 45)));
        arcs.Add(new ArcCase(0, 0, 500, 180, Combat.InArc(0, 0, 500, 180)));

        return new VectorFile(weapons, hulls, [.. chances], [.. arcs]);
    }

    /// <summary>По строке на случай — чтобы diff файла читался.</summary>
    private static string Format(VectorFile file)
    {
        var sb = new StringBuilder("{\n");
        AppendMap(sb, "weapons", file.Weapons);
        AppendMap(sb, "hulls", file.Hulls);
        AppendList(sb, "hitChance", file.HitChance, last: false);
        AppendList(sb, "arc", file.Arc, last: true);
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendMap<T>(StringBuilder sb, string name, Dictionary<string, T> map)
    {
        sb.Append("  \"").Append(name).Append("\": {\n");
        var i = 0;
        foreach (var (id, value) in map)
        {
            sb.Append("    ").Append(JsonSerializer.Serialize(id, Json)).Append(": ").Append(JsonSerializer.Serialize(value, Json));
            sb.Append(++i == map.Count ? "\n" : ",\n");
        }
        sb.Append("  },\n");
    }

    private static void AppendList<T>(StringBuilder sb, string name, T[] items, bool last)
    {
        sb.Append("  \"").Append(name).Append("\": [\n");
        for (var i = 0; i < items.Length; i++)
            sb.Append("    ").Append(JsonSerializer.Serialize(items[i], Json)).Append(i == items.Length - 1 ? "\n" : ",\n");
        sb.Append(last ? "  ]\n" : "  ],\n");
    }
}
