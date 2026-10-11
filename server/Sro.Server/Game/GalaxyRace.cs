using System.Diagnostics;
using Sro.Server.Net;
using Sro.Sim.Race;

namespace Sro.Server.Game;

public sealed record RaceSnapshot(string SessionId, int Tick, long AckSeq, string State, RaceVec Position,
    RaceVec Velocity, double Heading, double Omega, double Hp,
    int CountdownMs, int Lap, int NextCp, int ElapsedMs, int LapElapsedMs, int[] LapTimesMs);

public sealed partial class Galaxy
{
    private sealed class RaceEntry(Player player, string request, RaceSession session)
    {
        public Player Player = player;
        public string Request = request;
        public RaceSession Session = session;
        public RaceResult? Result;
        public Task? Save;
        public long RetryAt;
        public bool Published;
        public bool Confirmed;
        public long InputWindow;
        public int InputCount;
        public long CreatedAt = Stopwatch.GetTimestamp();
    }
    private readonly Dictionary<string, RaceEntry> _races = [];
    private RaceConfig? _raceConfig;
    private RaceResultStore? _raceStore;
    private long _raceClock;
    public void ConfigureRace(RaceConfig config, RaceResultStore store)
    { _raceConfig = config; _raceStore = store; _raceClock = Stopwatch.GetTimestamp(); }
    private static string RaceOwner(Player p) => p.AccountId ?? $"guest:{p.Id}";
    public bool InRace(Player p) => _races.Values.Any(e => RaceOwner(e.Player) == RaceOwner(p) && e.Session.Active);
    public void Race(IClientConnection connection, RaceActMsg m)
    {
        if (RoomOf(connection)?.PlayerOf(connection) is not { } p || _raceConfig is not { } config || _raceStore is null) return;
        void Error(string code) => connection.Send(new RaceEventMsg("error", m.RequestId, m.SessionId, Code: code));
        if (m.Act == "leaderboard")
        {
            if (m.Kind is not ("total" or "lap")) { Error("INVALID_INPUT"); return; }
            connection.Send(new RaceEventMsg("leaderboard", m.RequestId, Board: _raceStore.Board(config.RankingKey, m.Kind, p.AccountId))); return;
        }
        if (m.Act == "join")
        {
            if (!p.Docked || p.DockedPlace != "pl:tauPrima") { Error("NOT_DOCKED_AT_RACE_PORT"); return; }
            if (string.IsNullOrWhiteSpace(m.RequestId) || m.RequestId.Length > 64) { Error("INVALID_INPUT"); return; }
            var duplicate = _races.Values.FirstOrDefault(e => RaceOwner(e.Player) == RaceOwner(p) && e.Request == m.RequestId);
            if (duplicate is not null) { Joined(duplicate); return; }
            if (!config.Track.IsActive) { Error("TRACK_UNAVAILABLE"); return; }
            if (InMech(p) || InRace(p)) { Error("RACE_ALREADY_ACTIVE"); return; }
            var entry = new RaceEntry(p, m.RequestId, new RaceSession(config));
            _races.Add(entry.Session.Id, entry);
            _log.LogInformation("Race {Session} joined, config {Key}", entry.Session.Id, config.RankingKey);
            Joined(entry); return;
        }
        if (m.SessionId is null) { Error("SESSION_NOT_FOUND"); return; }
        var own = _races.GetValueOrDefault(m.SessionId);
        if (own is null)
        {
            var saved = _raceStore.Get(m.SessionId);
            if (m.Act == "result" && p.AccountId is not null && saved?.PlayerId == p.AccountId)
                connection.Send(new RaceEventMsg("result", m.RequestId, m.SessionId, Result: saved, ValidationStatus: "CONFIRMED"));
            else Error("SESSION_NOT_FOUND");
            return;
        }
        if (RaceOwner(own.Player) != RaceOwner(p)) { Error("SESSION_NOT_FOUND"); return; }
        own.Player = p;
        switch (m.Act)
        {
            case "ready":
                if (m.ConfigHash != own.Session.Config.Hash) { Error("CONFIG_MISMATCH"); return; }
                if (own.Session.State == "COUNTDOWN") break;
                if (!own.Session.Ready(m.ConfigHash)) Error("INVALID_STATE");
                break;
            case "input":
                if (own.InputWindow != Tick) { own.InputWindow = Tick; own.InputCount = 0; }
                if (++own.InputCount > 6) { Error("RATE_LIMITED"); return; }
                if (m.Input is null || !m.Input.Valid) { Error("INVALID_INPUT"); return; }
                own.Session.Apply(m.Input); break;
            case "abort": own.Session.Abort("USER_ABORT"); break;
            case "result":
                if (own.Result is null) { Error("RESULT_PENDING"); return; }
                Publish(own); break;
            default: Error("INVALID_INPUT"); break;
        }
        void Joined(RaceEntry e) => connection.Send(new RaceEventMsg("joined", m.RequestId, e.Session.Id,
            Config: e.Session.Config, Snapshot: Snapshot(e.Session)));
    }
    private static RaceSnapshot Snapshot(RaceSession s) => new(s.Id, s.Tick, s.Input.Seq, s.State,
        s.Body.Position, s.Body.Velocity, s.Body.Heading, s.Body.Omega, s.Body.Hp,
        s.CountdownMs, Math.Min(s.Config.Track.LapCount, s.LapTimesMs.Count + 1), s.NextCp,
        s.ElapsedMs, s.ElapsedMs - s.LapTimesMs.Sum(), s.LapTimesMs.ToArray());
    private void Publish(RaceEntry e)
    {
        e.Player.Connection?.Send(new RaceEventMsg("result", SessionId: e.Session.Id, Result: e.Result,
            Snapshot: Snapshot(e.Session),
            ValidationStatus: e.Confirmed ? "CONFIRMED" : "PENDING",
            Board: e.Confirmed ? _raceStore!.Board(e.Session.Config.RankingKey, "total", e.Player.AccountId) : null));
        e.Published = true;
    }
    private void StepRace()
    {
        if (_raceConfig is null || _raceStore is null) return;
        var now = Stopwatch.GetTimestamp();
        var overload = Stopwatch.GetElapsedTime(_raceClock, now).TotalSeconds > _raceConfig.Rules.OverloadSeconds;
        _raceClock = now;
        foreach (var e in _races.Values)
        {
            var s = e.Session;
            if (overload && s.Active && Stopwatch.GetElapsedTime(e.CreatedAt, now).TotalSeconds > s.Config.Rules.OverloadSeconds) s.Abort("SERVER_OVERLOAD");
            for (var i = 0; i < 3 && s.Active; i++) s.Step();
            if (s.Active)
            {
                e.Player.Connection?.Send(new RaceEventMsg("snapshot", SessionId: s.Id, Snapshot: Snapshot(s)));
                if (s.ContactLimit) _log.LogWarning("Race contact iteration limit {Session}", s.Id);
                continue;
            }
            if (e.Result is null)
            {
                e.Result = new(s.Id, e.Player.AccountId, e.Player.Name, s.Config.RankingKey,
                    s.State, s.Reason, s.ElapsedMs, s.State == "FINISHED" ? s.ElapsedMs : null,
                    s.LapTimesMs.ToArray(), DateTimeOffset.UtcNow);
                _log.LogInformation("Race {Session} ended {Status}/{Reason}, {Elapsed}ms, vn {NormalSpeed}", s.Id, s.State, s.Reason, s.ElapsedMs, s.LastNormalSpeed);
            }
            if (e.Save is { IsCompleted: true } task)
            {
                if (task.IsCompletedSuccessfully) { e.Confirmed = true; e.Save = null; Publish(e); }
                else
                {
                    _log.LogError(task.Exception, "Race result storage unavailable {Session}", s.Id);
                    e.Save = null; e.RetryAt = now + Stopwatch.Frequency * 5;
                    e.Player.Connection?.Send(new RaceEventMsg("error", SessionId: s.Id, Code: "STORAGE_UNAVAILABLE"));
                }
            }
            if (!e.Confirmed && e.Save is null && now >= e.RetryAt) e.Save = Task.Run(() => _raceStore.Save(e.Result));
            if (!e.Published) Publish(e);
        }
        // Durable results remain queryable; keep a bounded replay window in memory.
        foreach (var group in _races.Values.Where(e => e.Confirmed).GroupBy(e => RaceOwner(e.Player)).ToArray())
            foreach (var old in group.OrderByDescending(e => e.Result!.FinishedAt).Skip(64).ToArray()) _races.Remove(old.Session.Id);
    }
}
