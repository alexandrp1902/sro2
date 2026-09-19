using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Sro.Server.Net;

// Нагрузочный тест (M7): N ботов-гостей в стартовой системе летают, крутятся и стреляют по NPC, как игроки.
// Каждый собирает снапшоты тем же декодером, что проверяют тесты, и считает трафик и рассинхроны дельт.
// Нагрузку сервера (время тика, КБ/с) смотрите в его логе: строка «Load: …» раз в 10 с.
//   dotnet run --project server/Sro.Bots -- --url ws://localhost:5000/ws --count 50 --seconds 60

var url = Arg("--url", "ws://localhost:5000/ws");
var count = int.Parse(Arg("--count", "50"));
var seconds = int.Parse(Arg("--seconds", "60"));
var rampMs = int.Parse(Arg("--ramp", "100"));

Console.WriteLine($"{count} bots → {url}, {seconds} s");
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var bots = Enumerable.Range(1, count).Select(i => new Bot(i, i == 1)).ToList();
var runs = new List<Task>();
foreach (var bot in bots)
{
    runs.Add(bot.RunAsync(new Uri(url), stop.Token));
    await Task.Delay(rampMs); // не все разом: так входят и настоящие игроки
}
await Task.WhenAll(runs);

var live = bots.Where(b => b.Frames > 0).ToList();
if (live.Count == 0)
{
    Console.WriteLine("No bot received a snapshot. Is the server running?");
    return 1;
}
Console.WriteLine();
Console.WriteLine($"bots with snapshots: {live.Count} of {count}");
Console.WriteLine($"snapshots per second: {live.Min(b => b.SnapshotRate):0.0} … {live.Max(b => b.SnapshotRate):0.0}");
Console.WriteLine($"received per bot:     {live.Average(b => b.KbPerSecond):0.0} KB/s (max {live.Max(b => b.KbPerSecond):0.0}), all bots {live.Sum(b => b.KbPerSecond):0} KB/s");
Console.WriteLine($"average frame:        {live.Average(b => b.AverageFrame):0} B, ships in view {live.Average(b => b.AverageShips):0.0}");
var sample = bots[0];
if (sample.JsonBytes > 0)
    Console.WriteLine($"same snapshots as JSON (bot 1): {sample.JsonKbPerSecond:0.0} KB/s — binary deltas are {sample.JsonBytes / (double)sample.FrameBytes:0.0}× smaller");
var desyncs = bots.Sum(b => b.Desyncs);
Console.WriteLine($"delta desyncs:        {desyncs}");
Console.WriteLine($"errors:               {bots.Count(b => b.Error is not null)}{(bots.FirstOrDefault(b => b.Error is not null)?.Error is { } e ? $" (first: {e})" : "")}");
return desyncs == 0 && live.Count == count ? 0 : 1;

string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

/// <summary>Бот-гость: летает вокруг станции, раз в несколько секунд стреляет по ближайшему кораблю.</summary>
sealed class Bot(int number, bool measureJson)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { AllowOutOfOrderMetadataProperties = true };

    private readonly SnapshotCodec.Decoder _decoder = new();
    private readonly Random _random = new(number);
    private readonly Stopwatch _clock = new();
    private long _shipsSeen;
    private int _id;
    private int _seq;
    private SnapshotMsg? _last;

    public long Frames { get; private set; }
    public long FrameBytes { get; private set; }
    public long JsonBytes { get; private set; }
    public int Desyncs => _decoder.Desyncs;
    public string? Error { get; private set; }

    private double Seconds => Math.Max(_clock.Elapsed.TotalSeconds, 1e-3);
    public double SnapshotRate => Frames / Seconds;
    public double KbPerSecond => FrameBytes / 1024.0 / Seconds;
    public double JsonKbPerSecond => JsonBytes / 1024.0 / Seconds;
    public double AverageFrame => Frames > 0 ? FrameBytes / (double)Frames : 0;
    public double AverageShips => Frames > 0 ? _shipsSeen / (double)Frames : 0;

    public async Task RunAsync(Uri url, CancellationToken stop)
    {
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(url, stop);
            await Send(socket, new { t = "hello", name = $"Бот {number}", hull = "light", weapon = "pulse" }, stop);
            var receiving = ReceiveAsync(socket, stop);
            await FlyAsync(socket, stop);
            await receiving;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is WebSocketException or IOException)
        {
            Error = e.Message;
        }
        finally
        {
            _clock.Stop();
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                catch (WebSocketException) { /* сервер уже закрыл */ }
            }
        }
    }

    /// <summary>Вход каждые 50 мс, как у клиента: курс меняется раз в пару секунд, у края — к станции.</summary>
    private async Task FlyAsync(ClientWebSocket socket, CancellationToken stop)
    {
        var angle = _random.NextDouble() * 2 * Math.PI;
        var throttle = 1.0;
        var nextTurn = 0L;
        var nextFire = 60L;
        var firing = false;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(stop))
        {
            if (_id == 0) continue;
            _seq++;
            var me = _last?.Ships.FirstOrDefault(s => s.Id == _id);
            if (_seq >= nextTurn)
            {
                nextTurn = _seq + 20 + _random.Next(40);
                angle = me is not null && Math.Sqrt(me.X * me.X + me.Y * me.Y) > 1800
                    ? Math.Atan2(-me.Y, -me.X) + (_random.NextDouble() - 0.5)
                    : _random.NextDouble() * 2 * Math.PI;
                throttle = 0.3 + _random.NextDouble() * 0.7;
            }
            await Send(socket, new { t = "input", seq = _seq, dx = Math.Cos(angle), dy = Math.Sin(angle), th = throttle }, stop);

            if (_seq >= nextFire && me is not null)
            {
                nextFire = _seq + 60 + _random.Next(100);
                firing = !firing;
                var target = _last!.Ships.Where(s => s.Id != _id).OrderBy(s => (s.X - me.X) * (s.X - me.X) + (s.Y - me.Y) * (s.Y - me.Y)).FirstOrDefault();
                if (firing && target is not null) await Send(socket, new { t = "target", id = target.Id }, stop);
                await Send(socket, new { t = "fire", on = firing && target is not null }, stop);
            }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[1 << 20];
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(length), stop);
                length += result.Count;
            } while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Close) return;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (!_clock.IsRunning) _clock.Start();
                Frames++;
                FrameBytes += length;
                _last = _decoder.Decode(buffer.AsMemory(0, length).ToArray());
                _shipsSeen += _last.Ships.Count;
                if (measureJson) JsonBytes += Protocol.Encode(_last).Length;
            }
            else if (_id == 0 && JsonSerializer.Deserialize<ServerMessage>(buffer.AsSpan(0, length), Json) is WelcomeMsg welcome)
            {
                _id = welcome.Id;
            }
        }
    }

    private static Task Send(ClientWebSocket socket, object message, CancellationToken stop) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, stop);
}
