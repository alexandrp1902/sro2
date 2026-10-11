using System.Text.Json;
using Sro.Sim.Race;

namespace Sro.Server.Game;

public sealed record RaceResult(string SessionId, string? PlayerId, string Name, string RankingKey,
    string Status, string? Reason, int ElapsedMs, int? TotalMs, int[] LapTimesMs, DateTimeOffset FinishedAt, int SchemaVersion = 1);
public sealed record RaceRank(int Rank, string Name, int TimeMs, DateTimeOffset AchievedAt);
public sealed record RaceBoard(string RankingKey, string Kind, RaceRank[] Rows, RaceRank? Self);
public sealed class RaceResultStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly Dictionary<string, RaceResult> _results = [];
    private readonly SemaphoreSlim _writer = new(1);
    private readonly object _gate = new();
    public RaceResultStore(string directory)
    {
        _directory = directory; Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var result = JsonSerializer.Deserialize<RaceResult>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException("Invalid race result");
            _results.TryAdd(result.SessionId, result);
        }
    }
    public RaceResult? Get(string id) { lock (_gate) return _results.GetValueOrDefault(id); }
    public async Task Save(RaceResult result)
    {
        await _writer.WaitAsync();
        try
        {
            if (Get(result.SessionId) is not null) return;
            var final = Path.Combine(_directory, result.SessionId + ".json");
            var temp = final + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes); await stream.FlushAsync(); stream.Flush(true);
            }
            File.Move(temp, final, overwrite: true);
            lock (_gate) _results.TryAdd(result.SessionId, result);
        }
        finally { _writer.Release(); }
    }
    public RaceBoard Board(string key, string kind, string? player)
    {
        RaceResult[] results;
        lock (_gate) results = _results.Values.ToArray();
        var ranked = results.Where(r => r.RankingKey == key && r.PlayerId is not null &&
                (kind == "total" ? r.Status == "FINISHED" && r.TotalMs.HasValue :
                    r.Status is "FINISHED" or "CRASHED" && r.LapTimesMs.Length > 0))
            .Select(r => (r.PlayerId, r.Name, Time: kind == "total" ? r.TotalMs!.Value : r.LapTimesMs.Min(), r.FinishedAt))
            .GroupBy(r => r.PlayerId).Select(g => g.OrderBy(r => r.Time).ThenBy(r => r.FinishedAt).First())
            .OrderBy(r => r.Time).ThenBy(r => r.FinishedAt).ThenBy(r => r.PlayerId, StringComparer.Ordinal)
            .Select((r, i) => (r.PlayerId, Row: new RaceRank(i + 1, r.Name, r.Time, r.FinishedAt))).ToArray();
        return new(key, kind, ranked.Take(50).Select(r => r.Row).ToArray(), ranked.FirstOrDefault(r => r.PlayerId == player).Row);
    }
}
