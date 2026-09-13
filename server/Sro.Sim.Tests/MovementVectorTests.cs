using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sro.Sim.Tests;

/// <summary>
/// Эталонные траектории shared/test-vectors/movement.json: по ним же проверяется TS-модель клиента (Vitest),
/// так что предсказание и сервер считают одинаково. Перегенерация: SRO_UPDATE_VECTORS=1 dotnet test.
/// </summary>
public class MovementVectorTests
{
    /// <summary>sin/cos/exp/atan2 в .NET и V8 могут расходиться в последнем бите.</summary>
    private const double Tolerance = 1e-6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Scenario(string Name, HullParams Hull, double[] Start, double[][] Inputs, double[][] States);

    private sealed record VectorFile(double Dt, Scenario[] Scenarios);

    private static string VectorPath() => Path.Combine(TestHulls.RepoRoot(), "shared", "test-vectors", "movement.json");

    [Fact]
    public void MovementMatchesSharedVectors()
    {
        var generated = Generate();
        var path = VectorPath();
        if (Environment.GetEnvironmentVariable("SRO_UPDATE_VECTORS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Format(generated));
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing: run 'SRO_UPDATE_VECTORS=1 dotnet test'");
        var stored = JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(path), Json)!;
        Assert.Equal(generated.Dt, stored.Dt);
        Assert.Equal(generated.Scenarios.Length, stored.Scenarios.Length);
        foreach (var (expected, actual) in stored.Scenarios.Zip(generated.Scenarios))
        {
            Assert.Equal(expected.Name, actual.Name);
            for (var i = 0; i < expected.States.Length; i++)
                for (var k = 0; k < 5; k++)
                    Assert.True(
                        Math.Abs(expected.States[i][k] - actual.States[i][k]) <= Tolerance,
                        $"{expected.Name}, step {i}, component {k}: {expected.States[i][k]} vs {actual.States[i][k]}");
        }
    }

    private static VectorFile Generate()
    {
        var light = TestHulls.Light;
        var medium = TestHulls.Medium;
        var heavy = TestHulls.Heavy;
        return new VectorFile(SimConfig.Dt,
        [
            Run("лёгкий: разгон вверх", light, new ShipState(), Repeat(50, (0, -1, 1))),
            Run("лёгкий: поворот на 90° на полной скорости", light, new ShipState { Vy = -330 }, Repeat(40, (1, 0, 1))),
            Run("средний: разворот ровно на 180°", medium, new ShipState { Vy = -250 }, Repeat(100, (0, 1, 1))),
            Run("тяжёлый: почти разворот при 60% тяги", heavy, new ShipState { Vy = -170 }, Repeat(120, (-0.1, 1, 0.6))),
            Run("лёгкий: стоп с заносом", light, new ShipState { Vx = 150, Vy = -250 }, Repeat(60, (0, -1, 0))),
            Run("средний: зигзаг", medium, new ShipState(),
                [.. Repeat(10, (1, -1, 0.4)), .. Repeat(10, (-1, -1, 0.8)), .. Repeat(10, (1, 0.3, 0.25)), .. Repeat(10, (-0.2, 1, 1)),
                 .. Repeat(10, (0, 0, 0.5)), .. Repeat(10, (0.7, 0.7, 0))]),
            Run("лёгкий: граница мира", light, new ShipState { X = 3900, Rot = Math.PI / 2, Vx = 330 }, Repeat(40, (1, 0.2, 1))),
            Run("лёгкий: lateralToForward 0.5", light with { LateralToForward = 0.5 }, new ShipState { Vy = -330 }, Repeat(40, (1, 0, 1))),
        ]);
    }

    private static List<(double, double, double)> Repeat(int count, (double Dx, double Dy, double Th) input) =>
        Enumerable.Repeat(input, count).ToList();

    private static Scenario Run(string name, HullParams hull, ShipState start, List<(double Dx, double Dy, double Th)> raw)
    {
        var s = start;
        var inputs = new double[raw.Count][];
        var states = new double[raw.Count][];
        for (var i = 0; i < raw.Count; i++)
        {
            // Во входы попадает то же, что сервер получил бы от клиента после проверки.
            Assert.True(MoveInput.TryCreate(raw[i].Dx, raw[i].Dy, raw[i].Th, out var input));
            inputs[i] = [input.Dx, input.Dy, input.Throttle];
            Movement.Step(ref s, input, hull, SimConfig.Dt);
            states[i] = [s.X, s.Y, s.Rot, s.Vx, s.Vy];
        }
        return new Scenario(name, hull, [start.X, start.Y, start.Rot, start.Vx, start.Vy], inputs, states);
    }

    /// <summary>По строке на шаг — чтобы diff файла читался.</summary>
    private static string Format(VectorFile file)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"dt\": ").Append(Num(file.Dt)).Append(",\n  \"scenarios\": [\n");
        for (var i = 0; i < file.Scenarios.Length; i++)
        {
            var sc = file.Scenarios[i];
            sb.Append("    {\n");
            sb.Append("      \"name\": ").Append(JsonSerializer.Serialize(sc.Name, Json)).Append(",\n");
            sb.Append("      \"hull\": ").Append(JsonSerializer.Serialize(sc.Hull, Json)).Append(",\n");
            sb.Append("      \"start\": ").Append(Row(sc.Start)).Append(",\n");
            AppendRows(sb, "inputs", sc.Inputs, last: false);
            AppendRows(sb, "states", sc.States, last: true);
            sb.Append(i == file.Scenarios.Length - 1 ? "    }\n" : "    },\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static void AppendRows(StringBuilder sb, string name, double[][] rows, bool last)
    {
        sb.Append("      \"").Append(name).Append("\": [\n");
        for (var i = 0; i < rows.Length; i++)
            sb.Append("        ").Append(Row(rows[i])).Append(i == rows.Length - 1 ? "\n" : ",\n");
        sb.Append(last ? "      ]\n" : "      ],\n");
    }

    private static string Row(double[] values) => "[" + string.Join(", ", values.Select(Num)) + "]";

    /// <summary>Кратчайшая запись, которая читается обратно в тот же double (и в .NET, и в JS).</summary>
    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
