using System.Collections.Concurrent;
using System.Diagnostics;
using Sro.Server.Accounts;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Поток тика галактики: фиксированные 20 Гц на все системы сразу (GDD §44, §64). Состояние <see cref="Galaxy"/>
/// меняется только здесь; сетевые потоки кладут команды в очередь. Раз в <see cref="StatsSeconds"/> в лог уходит
/// нагрузка: игроки и корабли по системам, время тика, исходящий трафик снапшотов.
/// </summary>
public sealed class GalaxyHost : BackgroundService
{
    /// <summary>Отстали сильнее (сервер подвис) — пропускаем тики, а не прокручиваем их пачкой.</summary>
    private const int MaxCatchUpTicks = 5;

    private const int StatsSeconds = 10;

    private readonly ILogger<GalaxyHost> _log;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Galaxy _galaxy;
    private readonly List<double> _tickMs = new(StatsSeconds * SimConfig.TickRate);
    private readonly double[] _roomMs;
    private long _tick;
    private long _statsFromBytes;
    private long _statsFromTimestamp = Stopwatch.GetTimestamp();

    public GalaxyHost(BalanceStore balance, AccountStore accounts, ILogger<GalaxyHost> log, ILogger<Room> roomLog)
    {
        _log = log;
        _galaxy = new Galaxy(balance.Balance, roomLog, accounts);
        _roomMs = new double[_galaxy.Rooms.Count];
        balance.Changed += b => _commands.Enqueue(() => _galaxy.ApplyBalance(b));
    }

    public long Tick => Interlocked.Read(ref _tick);

    /// <summary>
    /// Текущий баланс — сетевому потоку: он проверяет путь в <c>hello</c> (M15.5) до входа,
    /// а значит до того, как команда доберётся до тика галактики.
    /// </summary>
    public Balance Balance => _galaxy.Balance;

    public void Join(IClientConnection connection, HelloMsg hello) =>
        _commands.Enqueue(() => _galaxy.Join(connection, hello.Token, hello.Name, hello.Hull, hello.Weapon));

    /// <summary>Пилот с аккаунтом: вход уже проверен в сетевом потоке.</summary>
    /// <param name="career">Путь нового пилота (M15.5); null — общий стартовый набор.</param>
    public void JoinAccount(IClientConnection connection, string accountId, string name, string? career = null) =>
        _commands.Enqueue(() => _galaxy.JoinAccount(connection, accountId, name, career));

    public void Leave(IClientConnection connection) => _commands.Enqueue(() => _galaxy.Disconnect(connection));

    public void Input(IClientConnection connection, InputMsg message)
    {
        // §51: мусор отбрасывается ещё в сетевом потоке, тяга зажимается в [0, 1].
        if (!MoveInput.TryCreate(message.Dx, message.Dy, message.Th, out var input)) return;
        With(connection, r => r.Input(connection, message.Seq, input));
    }

    public void SetHull(IClientConnection connection, string? hullId) => With(connection, r => r.SetHull(connection, hullId));

    public void Transport(IClientConnection connection, string? hullId) => With(connection, r => r.Transport(connection, hullId));

    public void SetWeapon(IClientConnection connection, string? weaponId) => With(connection, r => r.SetWeapon(connection, weaponId));

    public void SetTarget(IClientConnection connection, int targetId) => With(connection, r => r.SetTarget(connection, targetId));

    public void SetFire(IClientConnection connection, bool on) => With(connection, r => r.SetFire(connection, on));

    public void SetPvp(IClientConnection connection, bool on) => With(connection, r => r.SetPvp(connection, on));

    public void SetLootTarget(IClientConnection connection, int lootId) => With(connection, r => r.SetLootTarget(connection, lootId));

    public void Grab(IClientConnection connection) => With(connection, r => r.Grab(connection));

    public void Sell(IClientConnection connection, string? item, int count = 0) =>
        With(connection, r => r.Sell(connection, item, count));

    public void Jettison(IClientConnection connection, string? item) =>
        With(connection, r => r.Jettison(connection, item));

    public void BuyGoods(IClientConnection connection, string? item, int count) =>
        With(connection, r => r.BuyGoods(connection, item, count));

    public void Rename(IClientConnection connection, string? name) => With(connection, r => r.Rename(connection, name));

    public void Dock(IClientConnection connection, bool on, string? place = null) => With(connection, r => r.Dock(connection, on, place));

    public void Buy(IClientConnection connection, string? kind, string? id, string? slot) =>
        With(connection, r => r.Buy(connection, kind, id, slot));

    public void Fit(IClientConnection connection, string? slot, string? id) => With(connection, r => r.Fit(connection, slot, id));

    public void SellItem(IClientConnection connection, string? id) => With(connection, r => r.SellItem(connection, id));

    public void Repair(IClientConnection connection) => With(connection, r => r.Repair(connection));

    public void Jump(IClientConnection connection, string? to) => With(connection, r => r.Jump(connection, to));


    public void Mission(IClientConnection connection, string? action, string? id) => With(connection, r => r.Mission(connection, action, id));

    /// <summary>Группа живёт поверх систем — команда идёт галактике, а не комнате.</summary>
    public void Party(IClientConnection connection, string? action, int id) =>
        _commands.Enqueue(() => _galaxy.Party(connection, action, id));

    /// <summary>Обмен, как и группа, живёт поверх комнат: сессию держит галактика (M16b).</summary>
    public void Trade(IClientConnection connection, TradeMsg trade) =>
        _commands.Enqueue(() => _galaxy.Trade(connection, trade.Action, trade.Id, trade.Credits, trade.Items, trade.Rev));

    /// <summary>Комната выбирается в потоке тика: к моменту выполнения корабль мог уже перелететь в другую систему.</summary>
    private void With(IClientConnection connection, Action<Room> command) =>
        _commands.Enqueue(() => _galaxy.With(connection, command));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>
    /// Тики по абсолютному расписанию от Stopwatch. PeriodicTimer на Windows округляет период к ~15,6 мс
    /// и уплывает, а клиент шагает ровно 20 раз в секунду — сервер начал бы отставать.
    /// </summary>
    private void Run(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        long done = 0;
        while (!ct.IsCancellationRequested)
        {
            var due = (long)(clock.Elapsed.TotalSeconds * SimConfig.TickRate);
            for (var n = 0; done < due && n < MaxCatchUpTicks; n++, done++)
            {
                try { RunTick(); }
                catch (Exception e) { _log.LogError(e, "Galaxy tick failed"); }
            }
            if (done < due) done = due;

            var wait = TimeSpan.FromSeconds((done + 1) * SimConfig.Dt) - clock.Elapsed;
            if (wait > TimeSpan.Zero) ct.WaitHandle.WaitOne(wait);
        }
    }

    private void RunTick()
    {
        var started = Stopwatch.GetTimestamp();
        while (_commands.TryDequeue(out var command))
        {
            try { command(); }
            catch (Exception e) { _log.LogError(e, "Galaxy command failed"); }
        }
        _galaxy.Step();
        Interlocked.Exchange(ref _tick, _galaxy.Tick);

        _tickMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        for (var i = 0; i < _roomMs.Length; i++) _roomMs[i] += _galaxy.StepMs[i];
        if (_tickMs.Count >= StatsSeconds * SimConfig.TickRate) LogStats();
    }

    /// <summary>Нагрузка за последние StatsSeconds — только если в галактике кто-то есть, пустой сервер лог не засоряет.</summary>
    private void LogStats()
    {
        var bytes = WebSocketConnection.FrameBytes;
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_statsFromTimestamp, now).TotalSeconds;
        var kbPerSecond = (bytes - _statsFromBytes) / 1024.0 / Math.Max(seconds, 1e-3);
        var online = _galaxy.OnlineTotal;
        if (online > 0)
        {
            _tickMs.Sort();
            var p95 = _tickMs[(int)Math.Min(_tickMs.Count - 1, Math.Ceiling(_tickMs.Count * 0.95) - 1)];
            var rooms = string.Join("; ", _galaxy.Rooms.Select((r, i) =>
                $"{r.SystemId} {r.OnlineCount}/{r.Count} pilots, {r.ShipCount} ships, {_roomMs[i] / _tickMs.Count:0.00} ms"));
            _log.LogInformation(
                "Load: online {Online}, tick avg {Avg:0.00} ms, p95 {P95:0.00} ms, max {Max:0.00} ms; snapshots {Kb:0.0} KB/s ({PerPilot:0.0} KB/s per pilot); {Rooms}",
                online, _tickMs.Average(), p95, _tickMs[^1], kbPerSecond, kbPerSecond / online, rooms);
        }
        _tickMs.Clear();
        Array.Clear(_roomMs);
        _statsFromBytes = bytes;
        _statsFromTimestamp = now;
    }
}
