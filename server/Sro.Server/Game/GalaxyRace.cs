using Sro.Server.Net;
using Sro.Sim.Race;

namespace Sro.Server.Game;

public sealed partial class Galaxy
{
    // Only a clock and durable result metadata live on the server. No vehicle simulation.
    private sealed class RaceEntry(Player player, string request, RaceConfig config, long created)
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public Player Player = player;
        public readonly string Request = request;
        public readonly RaceConfig Config = config;
        public readonly long CreatedAt = created;
        public long? StartTick;
        public DateTimeOffset? StartedAt;
        public RaceResult? Result;
        public Task? Save;
        public long RetryAt;
        public bool Published;
        public bool Confirmed;
        public bool Active => Result is null;
    }
    private readonly Dictionary<string, RaceEntry> _races = [];
    private RaceConfig? _raceConfig;
    private RaceResultStore? _raceStore;
    private TimeProvider _raceTime = TimeProvider.System;
    public void ConfigureRace(RaceConfig config, RaceResultStore store, TimeProvider? time = null)
    { _raceConfig = config; _raceStore = store; _raceTime = time ?? TimeProvider.System; }
    private static string RaceOwner(Player p) => p.AccountId ?? $"guest:{p.Id}";
    public bool InRace(Player p) => _races.Values.Any(e => RaceOwner(e.Player) == RaceOwner(p) && e.Active);
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
            var entry = new RaceEntry(p, m.RequestId, config, _raceTime.GetTimestamp());
            _races.Add(entry.Id, entry);
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
                if (!own.Active) { Error("INVALID_STATE"); return; }
                if (m.ConfigHash != own.Config.Hash) { Error("CONFIG_MISMATCH"); return; }
                if (!own.StartTick.HasValue)
                {
                    var seconds = own.Config.Rules.CountdownSeconds;
                    own.StartTick = _raceTime.GetTimestamp() + (long)Math.Round(seconds * _raceTime.TimestampFrequency);
                    own.StartedAt = _raceTime.GetUtcNow().AddSeconds(seconds);
                }
                Started(own); break;
            case "finish":
                if (own.Result is not null) { Publish(own); return; }
                var finish = m.Finish;
                if ((!own.StartTick.HasValue || _raceTime.GetTimestamp() < own.StartTick.Value) && finish?.Status != "ABORTED")
                { Error("INVALID_STATE"); return; }
                var laps = finish?.LapTimesMs ?? [];
                if (finish is null || finish.Status is not ("FINISHED" or "CRASHED" or "ABORTED") ||
                    laps.Length > own.Config.Track.LapCount || laps.Any(t => t <= 0 || t > own.Config.Rules.TimeLimitSeconds * 1000) ||
                    (finish.Status == "FINISHED" && laps.Length != own.Config.Track.LapCount) ||
                    (finish.Status == "CRASHED" && finish.Reason is not ("WALL" or "COLUMN" or "ROCK")))
                { Error("INVALID_INPUT"); return; }
                var elapsed = Elapsed(own);
                if (laps.Length > 0 && elapsed < laps.Length * own.Config.Track.Length / own.Config.Rules.EngineLimit * 500)
                { Error("INVALID_RESULT"); return; }
                var minimum = own.Config.Track.Length * own.Config.Track.LapCount / own.Config.Rules.EngineLimit * 500;
                if (finish.Status == "FINISHED" && elapsed < minimum) { Error("INVALID_RESULT"); return; }
                if (elapsed > own.Config.Rules.TimeLimitSeconds * 1000) End(own, "ABORTED", "TIME_LIMIT", []);
                else End(own, finish.Status, finish.Status == "FINISHED" ? null : finish.Reason, ScaleLaps(laps, elapsed, finish.Status == "FINISHED"), elapsed);
                break;
            case "abort":
                if (own.Active) End(own, "ABORTED", "USER_ABORT", []); else Publish(own);
                break;
            case "result":
                if (own.Result is null) { Error("RESULT_PENDING"); return; }
                Publish(own); break;
            default: Error("INVALID_INPUT"); break;
        }
        void Joined(RaceEntry e) => connection.Send(new RaceEventMsg("joined", m.RequestId, e.Id, Config: e.Config));
        void Started(RaceEntry e) => connection.Send(new RaceEventMsg("started", SessionId: e.Id,
            CountdownMs: Math.Max(0, (int)Math.Ceiling(_raceTime.GetElapsedTime(_raceTime.GetTimestamp(), e.StartTick!.Value).TotalMilliseconds)), StartedAt: e.StartedAt));
    }
    private int Elapsed(RaceEntry e) => !e.StartTick.HasValue ? 0 : Math.Max(0, (int)Math.Round(_raceTime.GetElapsedTime(e.StartTick.Value).TotalMilliseconds));
    private static int[] ScaleLaps(int[] laps, int elapsed, bool finished)
    {
        if (laps.Length == 0) return [];
        var sum = laps.Sum(t => (long)t);
        var scale = finished || sum > elapsed ? elapsed / (double)sum : 1;
        var total = 0;
        long cumulative = 0;
        return laps.Select((t, i) => {
            cumulative += t;
            var boundary = (int)Math.Round(cumulative * scale);
            boundary = Math.Clamp(boundary, total + 1, elapsed - (laps.Length - i - 1));
            var value = boundary - total;
            total = boundary; return value;
        }).ToArray();
    }
    private void End(RaceEntry e, string status, string? reason, int[] laps, int? measuredElapsed = null)
    {
        var elapsed = measuredElapsed ?? Elapsed(e);
        e.Result = new(e.Id, e.Player.AccountId, e.Player.Name, e.Config.RankingKey, status, reason,
            elapsed, status == "FINISHED" ? elapsed : null, laps, _raceTime.GetUtcNow(), SchemaVersion: 2, StartedAt: e.StartedAt);
        Publish(e);
    }
    private void Publish(RaceEntry e)
    {
        e.Player.Connection?.Send(new RaceEventMsg("result", SessionId: e.Id, Result: e.Result,
            ValidationStatus: e.Confirmed ? "CONFIRMED" : "PENDING",
            Board: e.Confirmed ? _raceStore!.Board(e.Config.RankingKey, "total", e.Player.AccountId) : null));
        e.Published = true;
    }
    private void MaintainRaces()
    {
        if (_raceConfig is null || _raceStore is null) return;
        var now = _raceTime.GetTimestamp();
        foreach (var e in _races.Values)
        {
            if (e.Active)
            {
                if (!e.StartTick.HasValue && _raceTime.GetElapsedTime(e.CreatedAt, now).TotalSeconds >= e.Config.Rules.LoadTimeoutSeconds)
                    End(e, "ABORTED", "LOAD_TIMEOUT", []);
                else if (e.StartTick.HasValue && Elapsed(e) > e.Config.Rules.TimeLimitSeconds * 1000)
                    End(e, "ABORTED", "TIME_LIMIT", []);
                else continue;
            }
            if (e.Save is { IsCompleted: true } task)
            {
                if (task.IsCompletedSuccessfully) { e.Confirmed = true; e.Save = null; Publish(e); }
                else
                {
                    _log.LogError(task.Exception, "Race result storage unavailable {Session}", e.Id);
                    e.Save = null; e.RetryAt = now + _raceTime.TimestampFrequency * 5;
                    e.Player.Connection?.Send(new RaceEventMsg("error", SessionId: e.Id, Code: "STORAGE_UNAVAILABLE"));
                }
            }
            if (!e.Confirmed && e.Save is null && now >= e.RetryAt) e.Save = Task.Run(() => _raceStore.Save(e.Result!));
            if (!e.Published) Publish(e);
        }
        foreach (var group in _races.Values.Where(e => e.Confirmed).GroupBy(e => RaceOwner(e.Player)).ToArray())
            foreach (var old in group.OrderByDescending(e => e.Result!.FinishedAt).Skip(64).ToArray()) _races.Remove(old.Id);
    }
}
