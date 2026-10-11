using Sro.Sim.Race;
using System.Text.Json;

namespace Sro.Sim.Tests;

public class RaceTests
{
    private static readonly RaceRules Rules = new() { DragK = 0, LateralK = 0, CoastBrakeAccel = 0, CountdownSeconds = 0 };
    private static RaceGate Gate(double x) => new(new(x, -10), new(x, 10), new(1, 0));
    public static RaceTrack Straight(int laps = 1, double finish = .3, RaceWall[]? walls = null) =>
        new("test", "1", "Test", laps, new(0, 0), 0, "desert", true, [], walls ?? [], [], [Gate(.1), Gate(.2)], Gate(finish), 1);
    private static RaceInput Input(long seq = 1, double throttle = 0, bool brake = false) =>
        new(seq, Throttle: throttle, Brake: brake);
    private static RaceSession Session(RaceTrack? track = null)
    {
        var session = new RaceSession(new(track ?? Straight(), Rules, "hash"));
        Assert.True(session.Ready("hash")); session.Step(); return session;
    }
    [Fact] public void CountdownLocksPositionAndHeading()
    {
        var s = new RaceSession(new(Straight(), new(), "hash"));
        s.Apply(new(1, "heading", Math.PI, Throttle: 1)); s.Ready("hash");
        for (var i = 0; i < 180; i++) s.Step();
        Assert.Equal("RUNNING", s.State); Assert.Equal(new RaceVec(0, 0), s.Body.Position);
        Assert.Equal(0, s.Body.Heading); Assert.Equal(0, s.ElapsedMs);
        s.Apply(new(2, Throttle: 1)); s.Step();
        Assert.True(s.Body.Position.X > 0);
    }
    [Fact] public void BrakeStopsWithoutReverseAndRestoresThrust()
    {
        var b = new RaceBody { Velocity = new(42, 0) };
        for (var i = 0; i < 84; i++) RacePhysics.Step(b, Input(throttle: 1, brake: true), Straight(), Rules);
        Assert.Equal(0, b.Velocity.Length, 8); Assert.InRange(b.Position.X, 28, 30);
        RacePhysics.Step(b, Input(throttle: 1), Straight(), Rules); Assert.True(b.Velocity.X > 0);
    }
    [Fact] public void BodyHeadingDoesNotInstantlyRotateVelocity()
    {
        var b = new RaceBody { Velocity = new(42, 0) };
        RacePhysics.Step(b, new(1, "heading", Math.PI / 2, Throttle: 1), Straight(), new());
        Assert.InRange(b.Heading, 0, .01); Assert.True(b.Velocity.X > 40); Assert.InRange(b.Velocity.Y, 0, .01);
    }
    [Fact] public void SelectedSpeedDeceleratesGraduallyAndIsRestoredAfterBrake()
    {
        var rules = new RaceRules { DragK = 0, LateralK = 0 };
        var track = Straight(finish: 10000);
        var b = new RaceBody { Velocity = new(30, 0) };
        var selected = Input(throttle: 10.0 / rules.EngineLimit);
        RacePhysics.Step(b, selected, track, rules);
        Assert.InRange(b.Velocity.Length, 29.8, 30);
        for (var i = 0; i < 360; i++) RacePhysics.Step(b, selected, track, rules);
        Assert.InRange(b.Velocity.Length, 9.99, 10.01);
        var cruising = Input(throttle: 20.0 / rules.EngineLimit);
        for (var i = 0; i < 240; i++) RacePhysics.Step(b, cruising, track, rules);
        Assert.InRange(b.Velocity.Length, 19.99, 20.01);
        for (var i = 0; i < 60; i++) RacePhysics.Step(b, cruising with { Brake = true }, track, rules);
        Assert.Equal(0, b.Velocity.Length);
        for (var i = 0; i < 240; i++) RacePhysics.Step(b, cruising, track, rules);
        Assert.InRange(b.Velocity.Length, 19.99, 20.01);
    }
    [Theory] [InlineData(17.999, false)] [InlineData(18, true)] [InlineData(1200, true)]
    public void SweptThinWallHonorsFatalThreshold(double speed, bool fatal)
    {
        var b = new RaceBody { Position = new(.19, 0), Velocity = new(speed, 0) };
        var motion = RacePhysics.Step(b, Input(), Straight(walls: [new("wall", new(1.2, -100), new(1.2, 100), "WALL")]), Rules);
        Assert.Equal(fatal, motion.FatalReason is not null); Assert.True(b.Position.X <= .3002);
        if (fatal) Assert.Equal(0, b.Hp); else Assert.True(b.Hp > 0);
    }
    [Fact] public void MultipleGatesPerStepInterpolateFinishAndLapSum()
    {
        var s = Session(Straight(laps: 3));
        for (var i = 0; i < 3; i++)
        {
            s.Body.Position = new(0, 0); s.Body.Velocity = new(30, 0); s.Apply(Input(i + 1)); s.Step();
        }
        Assert.Equal("FINISHED", s.State); Assert.Equal(3, s.LapTimesMs.Count);
        Assert.Equal(s.ElapsedMs, s.LapTimesMs.Sum()); Assert.Equal(43, s.ElapsedMs);
    }
    [Fact] public void SustainedContactDoesNotRepeatDamageAndNewImpactAfterSeparationDoes()
    {
        var track = Straight(walls: [new("wall", new(1.2, -100), new(1.2, 100), "WALL")]);
        var rules = Rules with { Restitution = 0 };
        var b = new RaceBody { Position = new(.3, 0), Velocity = new(3, 10) };
        RacePhysics.Step(b, Input(), track, rules); var hp = b.Hp;
        for (var i = 0; i < 10; i++) {
            b.Position = new(.3, 0); b.Velocity = new(3, 10); RacePhysics.Step(b, Input(), track, rules); Assert.Equal(hp, b.Hp);
        }
        b.Position = new(0, 0); b.Velocity = new(-1, 0); RacePhysics.Step(b, Input(), track, rules);
        Assert.Empty(b.Contacts); b.Position = new(.3, 0); b.Velocity = new(3, 10); RacePhysics.Step(b, Input(), track, rules);
        Assert.True(b.Hp < hp);
    }
    [Fact] public void FinishWithoutCheckpointsAndReverseCrossingDoNotCount()
    {
        var s = Session(); s.Body.Position = new(.25, 0); s.Body.Velocity = new(30, 0); s.Step();
        Assert.Empty(s.LapTimesMs); Assert.Equal(0, s.NextCp);
        Assert.Null(RacePhysics.Crossing(new(1, 0), new(-1, 0), Gate(0)));
        Assert.Null(RacePhysics.Crossing(new(0, 0), new(0, 0), Gate(0)));
    }
    [Theory] [InlineData(.25, "FINISHED")] [InlineData(.3, "CRASHED")] [InlineData(.4, "CRASHED")]
    public void FinishAndCrashUseEventOrderWithCrashWinningTie(double finish, string expected)
    {
        var s = Session(Straight(finish: finish, walls: [new("wall", new(1.2, -100), new(1.2, 100), "WALL")]));
        s.Body.Velocity = new(30, 0); s.Apply(Input()); s.Step(); Assert.Equal(expected, s.State);
        if (expected == "FINISHED") Assert.Equal(100, s.Body.Hp);
    }
    [Fact] public void OldSequencesAndNonFiniteInputAreRejected()
    {
        var s = Session(); Assert.True(s.Apply(Input(2, 1)));
        Assert.False(s.Apply(Input(2))); Assert.False(s.Apply(Input(1)));
        Assert.False(s.Apply(new(3, Heading: double.NaN))); Assert.Equal(1, s.Input.Throttle);
    }
    [Fact] public void InputTimeoutCoastsAndThenAbortsWithoutPausedClock()
    {
        var s = Session(Straight(finish: 1000)); s.Apply(Input(1, 1));
        for (var i = 0; i < 180; i++) s.Step();
        Assert.Equal("ABORTED", s.State); Assert.Equal("CONTROL_LOST", s.Reason); Assert.Equal(3000, s.ElapsedMs);
        Assert.InRange(s.Body.Velocity.Length, 3, 5); Assert.True(s.Body.Position.X > 0);
    }
    [Fact] public void ActiveFalseHeartbeatsStillAbortAndMenuHeartbeatsDoNot()
    {
        var lost = Session(Straight(finish: 1000)); var menu = Session(Straight(finish: 1000));
        for (var i = 1; i <= 190; i++) { lost.Apply(new(i, ControlActive: false)); menu.Apply(new(i)); lost.Step(); menu.Step(); }
        Assert.Equal("ABORTED", lost.State); Assert.Equal("RUNNING", menu.State);
    }

    private sealed record State(RaceVec Position, RaceVec Velocity, double Heading, double Omega, double Hp);
    private sealed record Phase(int Ticks, RaceInput Input);
    private sealed record Vector(string Name, State Initial, Phase[] Phases, State Expected, string? FatalReason);
    private sealed record Vectors(RaceRules Rules, RaceTrack Track, Vector[] Cases);
    private static State StateOf(RaceBody b) => new(b.Position, b.Velocity, b.Heading, b.Omega, b.Hp);
    [Fact] public void SharedVectorsMatchServerAndCanBeRegenerated()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "shared"))) repo = repo.Parent;
        var path = Path.Combine(repo!.FullName, "shared", "test-vectors", "race.json");
        var rules = new RaceRules(); var track = Straight(finish: 1000, walls: [new("thin", new(50, -1000), new(50, 1000), "WALL")]);
        var seed = new State(new(0, 0), new(0, 0), 0, 0, 100);
        var definitions = new[] {
            new Vector("engine-and-coast", seed, [new(120, Input(1, 1)), new(60, Input(2))], seed, null),
            new Vector("steering-inertia", seed with { Velocity = new(42, 0) }, [new(30, new(1, "heading", Math.PI / 2, Throttle: 1))], seed, null),
            new Vector("brake-and-resume", seed with { Velocity = new(42, 0) }, [new(100, Input(1, 1, true)), new(30, Input(2, 1))], seed, null),
            new Vector("selected-speed-reduction", seed with { Velocity = new(35, 0) }, [new(90, Input(1, .25)), new(90, Input(2, 0))], seed, null),
            new Vector("ccd-fatal", seed with { Position = new(49, 0), Velocity = new(1200, 0) }, [new(1, Input())], seed, null),
            new Vector("glancing-wall", seed with { Position = new(49, 0), Velocity = new(5, 30) }, [new(3, Input())], seed, null),
        };
        Vector Run(Vector v) {
            var b = new RaceBody { Position = v.Initial.Position, Velocity = v.Initial.Velocity, Heading = v.Initial.Heading, Omega = v.Initial.Omega, Hp = v.Initial.Hp };
            string? fatal = null;
            foreach (var phase in v.Phases) { for (var i = 0; i < phase.Ticks; i++) { fatal = RacePhysics.Step(b, phase.Input, track, rules).FatalReason; if (fatal is not null) break; } if (fatal is not null) break; }
            return v with { Expected = StateOf(b), FatalReason = fatal };
        }
        var generated = new Vectors(rules, track, definitions.Select(Run).ToArray());
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        if (Environment.GetEnvironmentVariable("SRO_UPDATE_VECTORS") == "1") File.WriteAllText(path, JsonSerializer.Serialize(generated, json) + "\n");
        var stored = JsonSerializer.Deserialize<Vectors>(File.ReadAllText(path), json)!;
        foreach (var v in stored.Cases) {
            var expected = Run(v); Assert.Equal(v.FatalReason, expected.FatalReason);
            Assert.Equal(v.Expected.Position.X, expected.Expected.Position.X, 9); Assert.Equal(v.Expected.Position.Y, expected.Expected.Position.Y, 9);
            Assert.Equal(v.Expected.Velocity.X, expected.Expected.Velocity.X, 9); Assert.Equal(v.Expected.Velocity.Y, expected.Expected.Velocity.Y, 9);
            Assert.Equal(v.Expected.Heading, expected.Expected.Heading, 9); Assert.Equal(v.Expected.Hp, expected.Expected.Hp, 9);
        }
    }

    [Fact] public void RealCanyonCanBeCompletedWithSelectedSpeed()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "shared"))) repo = repo.Parent;
        var config = RaceConfig.Load(Path.Combine(repo!.FullName, "shared", "race"));
        var s = new RaceSession(config); s.Ready(config.Hash);
        var line = config.Track.Centerline;
        for (var tick = 1; tick < 36000 && s.Active; tick++)
        {
            var nearest = double.MaxValue; var index = 0; var projection = new RaceVec();
            for (var i = 0; i < line.Length; i++)
            {
                var a = line[i]; var delta = line[(i + 1) % line.Length] - a;
                var p = a + delta * Math.Clamp((s.Body.Position - a).Dot(delta) / delta.Dot(delta), 0, 1);
                var d = (p - s.Body.Position).Length;
                if (d < nearest) { nearest = d; index = i; projection = p; }
            }
            var remaining = 4.0; var target = projection;
            for (var i = 0; i < line.Length && remaining > 0; i++)
            {
                var next = line[(index + 1) % line.Length]; var delta = next - target;
                if (delta.Length > remaining) { target += delta * (remaining / delta.Length); break; }
                remaining -= delta.Length; target = next; index = (index + 1) % line.Length;
            }
            var desired = target - s.Body.Position; var heading = Math.Atan2(desired.Y, desired.X);
            var speed = s.Body.Velocity.Length;
            s.Apply(new(tick, "heading", heading, Throttle: 6 / config.Rules.EngineLimit, Brake: speed > 7)); s.Step();
        }
        Assert.True(s.State == "FINISHED", $"{s.State}/{s.Reason} at {s.Body.Position}, CP {s.NextCp}, laps {s.LapTimesMs.Count}, HP {s.Body.Hp}");
        Assert.Equal(3, s.LapTimesMs.Count); Assert.Equal(s.ElapsedMs, s.LapTimesMs.Sum());
    }
}
