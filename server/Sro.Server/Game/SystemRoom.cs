using System.Collections.Concurrent;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Одна звёздная система = одна комната со своим фиксированным тиком (GDD §64).
/// Состояние комнаты меняется только в потоке тика; сетевые потоки кладут команды в очередь.
/// </summary>
public sealed class SystemRoom(ILogger<SystemRoom> log) : BackgroundService
{
    private const int MaxNameLength = 16;

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Dictionary<int, Player> _players = [];
    private long _tick;

    public long Tick => Interlocked.Read(ref _tick);

    public void Join(IClientConnection connection, string? name) => _commands.Enqueue(() =>
    {
        var player = new Player(connection, SanitizeName(name));
        _players[connection.Id] = player;
        connection.Send(new WelcomeMsg(connection.Id, SimConfig.TickRate));
        BroadcastOnline();
        log.LogInformation("Player #{Id} '{Name}' joined, online {Count}", connection.Id, player.Name, _players.Count);
    });

    public void Leave(IClientConnection connection) => _commands.Enqueue(() =>
    {
        if (!_players.Remove(connection.Id)) return;
        BroadcastOnline();
        log.LogInformation("Player #{Id} left, online {Count}", connection.Id, _players.Count);
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SimConfig.Dt));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                while (_commands.TryDequeue(out var command))
                {
                    try { command(); }
                    catch (Exception e) { log.LogError(e, "Room command failed"); }
                }
                Interlocked.Increment(ref _tick);
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка сервера.
        }
    }

    private void BroadcastOnline()
    {
        var message = new OnlineMsg(_players.Count);
        foreach (var player in _players.Values) player.Connection.Send(message);
    }

    private static string SanitizeName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length == 0 ? "Ranger" : trimmed[..Math.Min(trimmed.Length, MaxNameLength)];
    }
}
