using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sro.Sim.Race;

public readonly record struct RaceVec(double X, double Y)
{
    public static RaceVec operator +(RaceVec a, RaceVec b) => new(a.X + b.X, a.Y + b.Y);
    public static RaceVec operator -(RaceVec a, RaceVec b) => new(a.X - b.X, a.Y - b.Y);
    public static RaceVec operator *(RaceVec a, double s) => new(a.X * s, a.Y * s);
    public double Dot(RaceVec b) => X * b.X + Y * b.Y;
    public double Length => Math.Sqrt(Dot(this));
}
public sealed record RaceWall(string Id, RaceVec A, RaceVec B, string Reason);
public sealed record RaceObstacle(string Id, RaceVec Center, double Radius, string Reason);
public sealed record RaceGate(RaceVec A, RaceVec B, RaceVec Normal);
public sealed record RaceTrack(string Id, string Version, string Name, int LapCount, RaceVec Spawn,
    double SpawnHeading, string Environment, bool IsActive, RaceVec[] Centerline,
    RaceWall[] Walls, RaceObstacle[] Obstacles, RaceGate[] Checkpoints, RaceGate FinishLine, int Length);
public sealed record RaceRules
{
    public string Id { get; init; } = "gravity-v1";
    public string PhysicsVersion { get; init; } = "1";
    public string VehicleVersion { get; init; } = "scrap-1";
    public double Radius { get; init; } = .9;
    public double Hp { get; init; } = 100;
    public double EngineAccel { get; init; } = 18;
    public double EngineLimit { get; init; } = 46;
    public double DragK { get; init; } = 3.0 / 42;
    public double LateralK { get; init; } = 1.2;
    public double BrakeAccel { get; init; } = 30;
    public double TurnFast { get; init; } = 110 * Math.PI / 180;
    public double TurnSlow { get; init; } = 150 * Math.PI / 180;
    public double AngularAccel { get; init; } = 240 * Math.PI / 180;
    public double HeadingGain { get; init; } = 4;
    public double CoastBrakeAccel { get; init; } = 10;
    public double FatalSpeed { get; init; } = 18;
    public double Restitution { get; init; } = .05;
    public double TangentLoss { get; init; } = .1;
    public double EventEpsilon { get; init; } = .000001;
    public double ContactSlop { get; init; } = .0001;
    public int MaxContacts { get; init; } = 8;
    public double CountdownSeconds { get; init; } = 3;
    public double InputTimeoutSeconds { get; init; } = .25;
    public double AbortTimeoutSeconds { get; init; } = 3;
    public double LoadTimeoutSeconds { get; init; } = 30;
    public double TimeLimitSeconds { get; init; } = 600;
    public double OverloadSeconds { get; init; } = .25;
}
public sealed record RaceConfig(RaceTrack Track, RaceRules Rules, string Hash)
{
    public string RankingKey => $"{Track.Id}:{Track.Version}:{Rules.Id}:{Rules.VehicleVersion}:{Track.LapCount}:browser-v2";
    public static RaceConfig Load(string directory)
    {
        var rulesText = File.ReadAllText(Path.Combine(directory, "rules.json"));
        var trackText = File.ReadAllText(Path.Combine(directory, "tracks", "red-canyon.json"));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var rules = JsonSerializer.Deserialize<RaceRules>(rulesText, options)!;
        var track = JsonSerializer.Deserialize<RaceTrack>(trackText, options)!;
        if (track.LapCount < 1 || track.Checkpoints.Length < 1 || track.Walls.Length < 3 ||
            rules.Radius <= 0 || rules.EngineLimit <= 0 || rules.MaxContacts < 1 || rules.FatalSpeed <= 0 ||
            track.Walls.Any(w => (w.B - w.A).Length < 1e-8))
            throw new InvalidDataException("Invalid race geometry or rules");
        return new(track, rules, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rulesText + trackText))));
    }
}
