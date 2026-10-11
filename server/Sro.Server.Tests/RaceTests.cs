using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim.Race;

namespace Sro.Server.Tests;

public sealed class RaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-race-test-" + Guid.NewGuid().ToString("N"));
    private readonly RaceResultStore _store;
    public RaceTests() => _store = new(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    private static RaceResult Result(string id, string player, string status, int? total, int[] laps, int age = 0, string key = "key") =>
        new(id, player, player, key, status, null, total ?? 3000, total, laps, DateTimeOffset.UnixEpoch.AddSeconds(age));
    [Fact] public async Task RankingsExcludeAbortsGuestsAndIncompatibleVersionsButKeepDnfLaps()
    {
        await _store.Save(Result("a", "alice", "FINISHED", 3000, [900, 1000, 1100]));
        await _store.Save(Result("b", "bob", "CRASHED", null, [800]));
        await _store.Save(Result("c", "carol", "ABORTED", null, [100]));
        await _store.Save(Result("d", "dave", "FINISHED", 1, [1], key: "old"));
        await _store.Save(Result("g", "guest", "FINISHED", 1, [1]) with { PlayerId = null });
        Assert.Single(_store.Board("key", "total", "alice").Rows);
        var lap = _store.Board("key", "lap", "bob"); Assert.Equal(2, lap.Rows.Length);
        Assert.Equal("bob", lap.Rows[0].Name); Assert.Equal(1, lap.Self!.Rank);
    }
    [Fact] public async Task AtomicRecordsRecoverWithoutIndexAndRepeatedSessionPreservesFirstDate()
    {
        var first = Result("same", "alice", "FINISHED", 3000, [1000, 1000, 1000]);
        await Task.WhenAll(_store.Save(first), _store.Save(first));
        await _store.Save(first with { TotalMs = 1, FinishedAt = DateTimeOffset.UtcNow });
        var recovered = new RaceResultStore(_dir);
        Assert.Equal(3000, recovered.Get("same")!.TotalMs); Assert.Equal(first.FinishedAt, recovered.Get("same")!.FinishedAt);
        Assert.Single(Directory.GetFiles(_dir, "*.json")); Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
    [Fact] public async Task EqualTimesKeepFirstAchievementAndSelfIsShownBeyondTop50()
    {
        for (var i = 0; i < 51; i++) await _store.Save(Result($"r{i}", $"p{i}", "FINISHED", 1000 + i, [1000 + i], i));
        await _store.Save(Result("equal", "p0", "FINISHED", 1000, [1000], 100));
        await _store.Save(Result("tie", "tie", "FINISHED", 1000, [1000], 200));
        var board = _store.Board("key", "total", "p50");
        Assert.Equal(50, board.Rows.Length); Assert.Equal(52, board.Self!.Rank); Assert.Equal("p0", board.Rows[0].Name);
        Assert.Equal(DateTimeOffset.UnixEpoch, board.Rows[0].AchievedAt); Assert.Equal("tie", board.Rows[1].Name);
    }
    [Fact] public async Task DiskFailureDoesNotCreateConfirmedRecordAndRetrySucceeds()
    {
        var blocked = Path.Combine(_dir, "blocked"); Directory.CreateDirectory(blocked);
        var store = new RaceResultStore(blocked); Directory.Delete(blocked); File.WriteAllText(blocked, "blocked");
        var r = Result("retry", "alice", "FINISHED", 3000, [1000, 1000, 1000]);
        await Assert.ThrowsAnyAsync<IOException>(() => store.Save(r)); Assert.Null(store.Get("retry"));
        File.Delete(blocked); Directory.CreateDirectory(blocked); await store.Save(r); Assert.NotNull(store.Get("retry"));
    }
    [Fact] public void AccessIsCheckedFromDockAndForeignSessionCannotBeControlled()
    {
        var galaxy = new Galaxy(TestBalance.Create(), NullLogger.Instance);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "shared"))) repo = repo.Parent;
        var config = RaceConfig.Load(Path.Combine(repo!.FullName, "shared", "race")); galaxy.ConfigureRace(config, _store);
        var a = new FakeConnection(1); var b = new FakeConnection(2);
        galaxy.Join(a, null, "Alice", null); galaxy.Join(b, null, "Bob", null);
        galaxy.Race(a, new("join", "one")); Assert.Equal("NOT_DOCKED_AT_RACE_PORT", a.Last<RaceEventMsg>().Code);
        var player = galaxy.RoomOf(a)!.PlayerOf(a)!; player.Docked = true; player.DockedPlace = "pl:tauPrima";
        galaxy.Race(a, new("join", "one")); var joined = a.Last<RaceEventMsg>(); Assert.Equal("joined", joined.Event);
        galaxy.Race(a, new("join", "one")); Assert.Equal(joined.SessionId, a.Last<RaceEventMsg>().SessionId);
        galaxy.Race(a, new("join", "two")); Assert.Equal("RACE_ALREADY_ACTIVE", a.Last<RaceEventMsg>().Code);
        galaxy.Race(b, new("abort", SessionId: joined.SessionId)); Assert.Equal("SESSION_NOT_FOUND", b.Last<RaceEventMsg>().Code);
        Assert.True(galaxy.InRace(player));
        galaxy.RoomOf(a)!.SetHull(a, "heavy"); Assert.Equal("light", player.HullId);
        galaxy.RoomOf(a)!.Dock(a, false); Assert.True(player.Docked);
        galaxy.Race(a, new("abort", SessionId: joined.SessionId)); Assert.False(galaxy.InRace(player));
        galaxy.Race(a, new("join", "two")); Assert.NotEqual(joined.SessionId, a.Last<RaceEventMsg>().SessionId);
    }

    private sealed class RaceClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(double seconds) => _ticks += (long)(seconds * TimestampFrequency);
    }
    [Fact] public async Task BrowserRaceHasNoSnapshotsAndOfficialTimeUsesServerClock()
    {
        var galaxy = new Galaxy(TestBalance.Create(), NullLogger.Instance);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "shared"))) repo = repo.Parent;
        var config = RaceConfig.Load(Path.Combine(repo!.FullName, "shared", "race"));
        var clock = new RaceClock(); galaxy.ConfigureRace(config, _store, clock);
        var client = new FakeConnection(1); galaxy.Join(client, null, "Alice", null);
        var player = galaxy.RoomOf(client)!.PlayerOf(client)!; player.Docked = true; player.DockedPlace = "pl:tauPrima";
        galaxy.Race(client, new("join", "clock")); var joined = client.Last<RaceEventMsg>();
        galaxy.Race(client, new("ready", SessionId: joined.SessionId, ConfigHash: config.Hash));
        Assert.Equal("started", client.Last<RaceEventMsg>().Event); Assert.Equal(3000, client.Last<RaceEventMsg>().CountdownMs);
        var started = client.Last<RaceEventMsg>().StartedAt;
        clock.Advance(1); galaxy.Race(client, new("ready", SessionId: joined.SessionId, ConfigHash: config.Hash));
        Assert.Equal(started, client.Last<RaceEventMsg>().StartedAt); Assert.Equal(2000, client.Last<RaceEventMsg>().CountdownMs);
        clock.Advance(62);
        var count = client.Count<RaceEventMsg>();
        for (var i = 0; i < 100; i++) galaxy.Step();
        Assert.Equal(count, client.Count<RaceEventMsg>()); Assert.True(galaxy.InRace(player));
        galaxy.Race(client, new("finish", SessionId: joined.SessionId, Finish: new("FINISHED", LapTimesMs: [15000, 15000, 15000])));
        var result = client.Last<RaceEventMsg>().Result!;
        Assert.Equal(60000, result.TotalMs); Assert.Equal(60000, result.LapTimesMs.Sum()); Assert.False(galaxy.InRace(player));
        clock.Advance(10); galaxy.Race(client, new("finish", SessionId: joined.SessionId, Finish: new("FINISHED", LapTimesMs: [1, 1, 1])));
        Assert.Equal(60000, client.Last<RaceEventMsg>().Result!.TotalMs);
        for (var i = 0; i < 100 && client.Last<RaceEventMsg>().ValidationStatus != "CONFIRMED"; i++) { galaxy.Step(); await Task.Delay(5); }
        Assert.Equal("CONFIRMED", client.Last<RaceEventMsg>().ValidationStatus);
        Assert.Equal(60000, new RaceResultStore(_dir).Get(joined.SessionId!)!.TotalMs);
    }

    [Fact] public void ImmediateFakeFinishIsRejectedButCountdownCanBeCancelled()
    {
        var galaxy = new Galaxy(TestBalance.Create(), NullLogger.Instance);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "shared"))) repo = repo.Parent;
        var config = RaceConfig.Load(Path.Combine(repo!.FullName, "shared", "race"));
        var clock = new RaceClock(); galaxy.ConfigureRace(config, _store, clock);
        var c = new FakeConnection(1); galaxy.Join(c, null, "Alice", null);
        var p = galaxy.RoomOf(c)!.PlayerOf(c)!; p.Docked = true; p.DockedPlace = "pl:tauPrima";
        galaxy.Race(c, new("join", "fake")); var id = c.Last<RaceEventMsg>().SessionId;
        galaxy.Race(c, new("ready", SessionId: id, ConfigHash: config.Hash)); clock.Advance(4);
        galaxy.Race(c, new("finish", SessionId: id, Finish: new("FINISHED", LapTimesMs: [1, 1, 1])));
        Assert.Equal("INVALID_RESULT", c.Last<RaceEventMsg>().Code); Assert.True(galaxy.InRace(p));
        galaxy.Race(c, new("abort", SessionId: id));
        galaxy.Race(c, new("join", "cancel")); id = c.Last<RaceEventMsg>().SessionId;
        galaxy.Race(c, new("ready", SessionId: id, ConfigHash: config.Hash));
        galaxy.Race(c, new("finish", SessionId: id, Finish: new("ABORTED", "USER_ABORT", [])));
        Assert.Equal("ABORTED", c.Last<RaceEventMsg>().Result!.Status); Assert.Equal(0, c.Last<RaceEventMsg>().Result!.ElapsedMs);
    }
}
