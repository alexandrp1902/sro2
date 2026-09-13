using System.Collections.Concurrent;
using System.Diagnostics;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Одна звёздная система = одна комната со своим фиксированным тиком (GDD §64).
/// Состояние комнаты меняется только в потоке тика; сетевые потоки кладут команды в очередь.
/// </summary>
public sealed class SystemRoom : BackgroundService
{
    private const int MaxNameLength = 16;

    /// <summary>Отстали сильнее (сервер подвис) — пропускаем тики, а не прокручиваем их пачкой.</summary>
    private const int MaxCatchUpTicks = 5;

    private readonly ILogger<SystemRoom> _log;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Dictionary<int, Player> _players = [];
    private readonly List<ShipDto> _ships = [];
    private IReadOnlyDictionary<string, HullParams> _hulls;
    private long _tick;

    public SystemRoom(BalanceStore balance, ILogger<SystemRoom> log)
    {
        _log = log;
        _hulls = balance.Hulls;
        balance.Changed += hulls => _commands.Enqueue(() => ApplyHulls(hulls));
    }

    public long Tick => Interlocked.Read(ref _tick);

    public void Join(IClientConnection connection, string? name, string? hull) => _commands.Enqueue(() =>
    {
        var hullId = hull is not null && _hulls.ContainsKey(hull) ? hull : SimConfig.DefaultHull;
        var player = new Player(connection, SanitizeName(name), hullId);
        _players[connection.Id] = player;
        connection.Send(new WelcomeMsg(connection.Id, SimConfig.TickRate, _hulls));
        BroadcastOnline();
        _log.LogInformation("Player #{Id} '{Name}' joined as {Hull}, online {Count}", connection.Id, player.Name, hullId, _players.Count);
    });

    public void Leave(IClientConnection connection) => _commands.Enqueue(() =>
    {
        if (!_players.Remove(connection.Id)) return;
        BroadcastOnline();
        _log.LogInformation("Player #{Id} left, online {Count}", connection.Id, _players.Count);
    });

    public void Input(IClientConnection connection, InputMsg message)
    {
        // §51: мусор отбрасывается ещё в сетевом потоке, тяга зажимается в [0, 1].
        if (!MoveInput.TryCreate(message.Dx, message.Dy, message.Th, out var input)) return;
        _commands.Enqueue(() =>
        {
            if (_players.TryGetValue(connection.Id, out var player)) player.Inputs.Enqueue(message.Seq, input);
        });
    }

    public void SetHull(IClientConnection connection, string? hullId) => _commands.Enqueue(() =>
    {
        if (hullId is null || !_hulls.ContainsKey(hullId) || !_players.TryGetValue(connection.Id, out var player)) return;
        player.HullId = hullId;
        _log.LogInformation("Player #{Id} switched to {Hull}", connection.Id, hullId);
    });

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>
    /// Тики по абсолютному расписанию от Stopwatch. PeriodicTimer на Windows округляет период к ~15,6 мс
    /// и уплывает, а клиент шагает ровно 20 раз в секунду — сервер начал бы отставать.
    /// </summary>
    private void Run(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var steps = new MoveInput[InputBuffer.MaxBudget];
        long done = 0;
        while (!ct.IsCancellationRequested)
        {
            var due = (long)(clock.Elapsed.TotalSeconds * SimConfig.TickRate);
            for (var n = 0; done < due && n < MaxCatchUpTicks; n++, done++)
            {
                try { RunTick(steps); }
                catch (Exception e) { _log.LogError(e, "Room tick failed"); }
            }
            if (done < due) done = due;

            var wait = TimeSpan.FromSeconds((done + 1) * SimConfig.Dt) - clock.Elapsed;
            if (wait > TimeSpan.Zero) ct.WaitHandle.WaitOne(wait);
        }
    }

    private void RunTick(MoveInput[] steps)
    {
        while (_commands.TryDequeue(out var command))
        {
            try { command(); }
            catch (Exception e) { _log.LogError(e, "Room command failed"); }
        }

        _ships.Clear();
        foreach (var player in _players.Values)
        {
            var hull = HullOf(player);
            var count = player.Inputs.Tick(steps);
            for (var i = 0; i < count; i++) Movement.Step(ref player.Ship, steps[i], hull, SimConfig.Dt);

            var s = player.Ship;
            _ships.Add(new ShipDto(player.Connection.Id, s.X, s.Y, s.Rot, s.Vx, s.Vy, player.HullId,
                player.Inputs.Last.Throttle, player.Inputs.AckSeq));
        }

        var tick = Interlocked.Increment(ref _tick);
        if (_ships.Count == 0) return;
        // Все получают одни и те же байты: ack каждого игрока лежит в записи его корабля.
        var snapshot = Protocol.Encode(new SnapshotMsg(tick, _ships));
        foreach (var player in _players.Values) player.Connection.SendRaw(snapshot);
    }

    private HullParams HullOf(Player player)
    {
        if (_hulls.TryGetValue(player.HullId, out var hull)) return hull;
        player.HullId = SimConfig.DefaultHull; // корпус убрали из hulls.json на лету
        return _hulls[SimConfig.DefaultHull];
    }

    private void ApplyHulls(IReadOnlyDictionary<string, HullParams> hulls)
    {
        _hulls = hulls;
        var message = new ConfigMsg(hulls);
        foreach (var player in _players.Values) player.Connection.Send(message);
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
