namespace Sro.Sim.Race;

public sealed class RaceSession(RaceConfig config)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public RaceConfig Config { get; } = config;
    public RaceBody Body { get; } = new() { Position = config.Track.Spawn, Heading = config.Track.SpawnHeading, Hp = config.Rules.Hp };
    public string State { get; private set; } = "GRID";
    public string? Reason { get; private set; }
    public int Tick { get; private set; }
    public int RunningTick { get; private set; }
    public int NextCp { get; private set; }
    public List<int> LapTimesMs { get; } = [];
    public int ElapsedMs { get; private set; }
    public int CountdownMs => State == "COUNTDOWN" ? Math.Max(0, (int)Math.Ceiling(Config.Rules.CountdownSeconds * 1000 - _countdownTicks * 1000.0 / 60)) : 0;
    public bool Active => State is "GRID" or "COUNTDOWN" or "RUNNING";
    public RaceInput Input { get; private set; } = new();
    public double LastNormalSpeed { get; private set; }
    public bool ContactLimit { get; private set; }
    private int _countdownTicks;
    private int _lastInputTick;
    private int _lastControlTick;
    private int _previousLapMs;

    public bool Ready(string hash)
    {
        if (hash != Config.Hash || State != "GRID") return false;
        State = "COUNTDOWN"; return true;
    }
    public bool Apply(RaceInput input)
    {
        if (!Active || !input.Valid || input.Seq <= Input.Seq) return false;
        Input = input with { Heading = RacePhysics.Angle(input.Heading) };
        _lastInputTick = Tick;
        if (input.ControlActive) _lastControlTick = Tick;
        return true;
    }
    public void Abort(string reason)
    {
        if (!Active) return;
        State = "ABORTED"; Reason = reason;
    }
    public void Step()
    {
        if (!Active) return;
        Tick++;
        if (State == "GRID") { if (Tick * RacePhysics.Dt >= Config.Rules.LoadTimeoutSeconds) Abort("LOAD_TIMEOUT"); return; }
        if (State == "COUNTDOWN")
        {
            if (++_countdownTicks >= (int)Math.Round(Config.Rules.CountdownSeconds * 60))
            { State = "RUNNING"; _lastInputTick = _lastControlTick = Tick; }
            return;
        }
        var r = Config.Rules;
        var safe = !Input.ControlActive || (Tick - _lastInputTick) * RacePhysics.Dt >= r.InputTimeoutSeconds;
        var input = safe ? Input with { Throttle = 0, Turn = 0, SteerMode = "turn", Brake = false, ControlActive = false } : Input;
        var motion = RacePhysics.Step(Body, input, Config.Track, r);
        ContactLimit = motion.ContactLimit;
        LastNormalSpeed = motion.NormalSpeed;
        foreach (var segment in motion.Segments)
        {
            var gates = Config.Track.Checkpoints.Select((gate, index) => (gate, index))
                .Append((gate: Config.Track.FinishLine, index: Config.Track.Checkpoints.Length))
                .Select(g => (g.index, t: RacePhysics.Crossing(segment.From, segment.To, g.gate)))
                .Where(g => g.t.HasValue).OrderBy(g => g.t).ToArray();
            foreach (var g in gates)
            {
                var fraction = segment.StartFraction + (segment.EndFraction - segment.StartFraction) * g.t!.Value;
                if (motion.FatalReason is not null && fraction >= motion.FatalFraction - r.EventEpsilon) break;
                if (g.index != NextCp) continue;
                if (NextCp < Config.Track.Checkpoints.Length) { NextCp++; continue; }
                var ms = (int)Math.Round((RunningTick + fraction) * 1000 / 60, MidpointRounding.AwayFromZero);
                LapTimesMs.Add(ms - _previousLapMs); _previousLapMs = ms; NextCp = 0;
                if (LapTimesMs.Count == Config.Track.LapCount)
                {
                    State = "FINISHED"; ElapsedMs = ms;
                    if (motion.HpBeforeFatal.HasValue) Body.Hp = motion.HpBeforeFatal.Value;
                    Body.Position = segment.From + (segment.To - segment.From) * g.t.Value;
                    Body.Velocity = default; return;
                }
            }
        }
        if (motion.FatalReason is not null)
        {
            State = "CRASHED"; Reason = motion.FatalReason;
            ElapsedMs = (int)Math.Round((RunningTick + motion.FatalFraction) * 1000 / 60, MidpointRounding.AwayFromZero);
            Body.Velocity = default; return;
        }
        RunningTick++; ElapsedMs = (int)Math.Round(RunningTick * 1000.0 / 60, MidpointRounding.AwayFromZero);
        if ((Tick - Math.Min(_lastInputTick, _lastControlTick)) * RacePhysics.Dt >= r.AbortTimeoutSeconds) Abort("CONTROL_LOST");
        else if (RunningTick * RacePhysics.Dt >= r.TimeLimitSeconds) Abort("TIME_LIMIT");
    }
}
