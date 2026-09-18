using System.Text;
using System.Text.Json;

namespace Sro.Sim.Tests;

/// <summary>
/// Эталоны shared/test-vectors/meteors.json: полёт камня в поле тяготения считают обе стороны — сервер двигает,
/// клиент рисует ту же дугу между снапшотами. Разойдутся формулы — камень на экране разойдётся с настоящим.
/// Перегенерация: SRO_UPDATE_VECTORS=1 dotnet test.
/// </summary>
public class MeteorVectorTests
{
    private const double Tolerance = 1e-9;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record FlightCase(double X0, double Y0, double Vx0, double Vy0, int Ticks, double X, double Y, double Vx, double Vy);

    private sealed record VectorFile(double Gravity, double GravityMinRadius, FlightCase[] Flight);

    /// <summary>Тяготение как в shared/meteors.json; числа здесь свои, чтобы тюнинг баланса не ломал вектор.</summary>
    private static readonly MeteorRules Rules = new(Gravity: 15_000_000, GravityMinRadius: 600);

    private static string VectorPath() => Path.Combine(TestHulls.RepoRoot(), "shared", "test-vectors", "meteors.json");

    [Fact]
    public void MeteorFlightMatchesSharedVectors()
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
        Assert.Equal(generated.Flight.Length, stored.Flight.Length);
        Assert.Equal(Rules.Gravity, stored.Gravity);
        Assert.Equal(Rules.GravityMinRadius, stored.GravityMinRadius);

        foreach (var c in stored.Flight)
        {
            var (x, y, vx, vy) = Fly(c.X0, c.Y0, c.Vx0, c.Vy0, c.Ticks);
            Assert.True(Math.Abs(c.X - x) <= Tolerance && Math.Abs(c.Y - y) <= Tolerance, $"{c}: ({x}, {y})");
            Assert.True(Math.Abs(c.Vx - vx) <= Tolerance && Math.Abs(c.Vy - vy) <= Tolerance, $"{c}: v ({vx}, {vy})");
        }
    }

    [Fact]
    public void Gravity_BendsTheTrackTowardsTheCentre()
    {
        // Камень идёт мимо центра: без тяготения он остался бы на x = 1500, а так его тянет внутрь.
        var (x, _, vx, _) = Fly(1500, -3000, 0, 250, 20 * 20);
        Assert.True(x < 1500 - 50, $"the track did not bend: x = {x}");
        Assert.True(vx < 0, $"no inward pull: vx = {vx}");
    }

    [Fact]
    public void GravityMinRadius_KeepsThePullFinite()
    {
        // В самом центре ускорение конечно и направлено наружу от точки, а не в бесконечность.
        var (ax, ay) = Rules.Pull(1, 0);
        Assert.True(Math.Abs(ax) <= Rules.Gravity / (Rules.GravityMinRadius * Rules.GravityMinRadius));
        Assert.Equal(0, ay);
        Assert.Equal((0, 0), Rules.Pull(0, 0));
        Assert.Equal((0, 0), (Rules with { Gravity = 0 }).Pull(1000, 0));
    }

    private static (double X, double Y, double Vx, double Vy) Fly(double x, double y, double vx, double vy, int ticks)
    {
        for (var i = 0; i < ticks; i++) Rules.Step(ref x, ref y, ref vx, ref vy, SimConfig.Dt);
        return (x, y, vx, vy);
    }

    private static VectorFile Generate()
    {
        var cases = new List<FlightCase>();
        foreach (var (x0, y0, vx0, vy0) in new[]
                 {
                     (0.0, -3000.0, 0.0, 250.0),      // прямо в центр: тяготение только разгоняет
                     (1500.0, -3000.0, 0.0, 250.0),   // мимо центра: дуга загибается внутрь
                     (-2600.0, 1200.0, 210.0, -90.0), // наискось через обитаемую часть
                     (900.0, 0.0, 0.0, 120.0),        // медленный у самого укрытия: круче всего гнёт
                     (300.0, 200.0, 40.0, 0.0),       // внутри сглаживания: сила растёт линейно
                     (4600.0, 4600.0, -180.0, -180.0),// от угла мира внутрь
                 })
        {
            foreach (var ticks in new[] { 1, 20, 200 })
            {
                var (x, y, vx, vy) = Fly(x0, y0, vx0, vy0, ticks);
                cases.Add(new FlightCase(x0, y0, vx0, vy0, ticks, x, y, vx, vy));
            }
        }
        return new VectorFile(Rules.Gravity, Rules.GravityMinRadius, [.. cases]);
    }

    /// <summary>По строке на случай — чтобы diff файла читался.</summary>
    private static string Format(VectorFile file)
    {
        var sb = new StringBuilder("{\n");
        sb.Append("  \"gravity\": ").Append(JsonSerializer.Serialize(file.Gravity, Json)).Append(",\n");
        sb.Append("  \"gravityMinRadius\": ").Append(JsonSerializer.Serialize(file.GravityMinRadius, Json)).Append(",\n");
        sb.Append("  \"flight\": [\n");
        for (var i = 0; i < file.Flight.Length; i++)
        {
            sb.Append("    ").Append(JsonSerializer.Serialize(file.Flight[i], Json));
            sb.Append(i == file.Flight.Length - 1 ? "\n" : ",\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }
}
