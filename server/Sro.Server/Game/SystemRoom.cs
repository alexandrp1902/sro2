using System.Collections.Concurrent;
using System.Diagnostics;
using Sro.Server.Accounts;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Одна звёздная система = одна комната со своим фиксированным тиком (GDD §64).
/// Состояние <see cref="Room"/> меняется только в потоке тика; сетевые потоки кладут команды в очередь.
/// </summary>
public sealed class SystemRoom : BackgroundService
{
    /// <summary>Отстали сильнее (сервер подвис) — пропускаем тики, а не прокручиваем их пачкой.</summary>
    private const int MaxCatchUpTicks = 5;

    private readonly ILogger<SystemRoom> _log;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Room _room;
    private long _tick;

    public SystemRoom(BalanceStore balance, AccountStore accounts, ILogger<SystemRoom> log, ILogger<Room> roomLog)
    {
        _log = log;
        _room = new Room(balance.Balance, roomLog, accounts: accounts);
        balance.Changed += b => _commands.Enqueue(() => _room.ApplyBalance(b));
    }

    public long Tick => Interlocked.Read(ref _tick);

    public void Join(IClientConnection connection, HelloMsg hello) =>
        _commands.Enqueue(() => _room.Join(connection, hello.Token, hello.Name, hello.Hull, hello.Weapon));

    /// <summary>Пилот с аккаунтом: вход уже проверен в сетевом потоке.</summary>
    public void JoinAccount(IClientConnection connection, string accountId, string name) =>
        _commands.Enqueue(() => _room.JoinAccount(connection, accountId, name));

    public void Leave(IClientConnection connection) => _commands.Enqueue(() => _room.Disconnect(connection));

    public void Input(IClientConnection connection, InputMsg message)
    {
        // §51: мусор отбрасывается ещё в сетевом потоке, тяга зажимается в [0, 1].
        if (!MoveInput.TryCreate(message.Dx, message.Dy, message.Th, out var input)) return;
        _commands.Enqueue(() => _room.Input(connection, message.Seq, input));
    }

    public void SetHull(IClientConnection connection, string? hullId) => _commands.Enqueue(() => _room.SetHull(connection, hullId));

    public void SetWeapon(IClientConnection connection, string? weaponId) => _commands.Enqueue(() => _room.SetWeapon(connection, weaponId));

    public void SetTarget(IClientConnection connection, int targetId) => _commands.Enqueue(() => _room.SetTarget(connection, targetId));

    public void SetFire(IClientConnection connection, bool on) => _commands.Enqueue(() => _room.SetFire(connection, on));

    public void SetLootTarget(IClientConnection connection, int lootId) =>
        _commands.Enqueue(() => _room.SetLootTarget(connection, lootId));

    public void Grab(IClientConnection connection) => _commands.Enqueue(() => _room.Grab(connection));

    public void Sell(IClientConnection connection, string? item) => _commands.Enqueue(() => _room.Sell(connection, item));

    public void Rename(IClientConnection connection, string? name) => _commands.Enqueue(() => _room.Rename(connection, name));

    public void Dock(IClientConnection connection, bool on) => _commands.Enqueue(() => _room.Dock(connection, on));

    public void Buy(IClientConnection connection, string? kind, string? id) => _commands.Enqueue(() => _room.Buy(connection, kind, id));

    public void Repair(IClientConnection connection) => _commands.Enqueue(() => _room.Repair(connection));

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
                catch (Exception e) { _log.LogError(e, "Room tick failed"); }
            }
            if (done < due) done = due;

            var wait = TimeSpan.FromSeconds((done + 1) * SimConfig.Dt) - clock.Elapsed;
            if (wait > TimeSpan.Zero) ct.WaitHandle.WaitOne(wait);
        }
    }

    private void RunTick()
    {
        while (_commands.TryDequeue(out var command))
        {
            try { command(); }
            catch (Exception e) { _log.LogError(e, "Room command failed"); }
        }
        _room.Step();
        Interlocked.Exchange(ref _tick, _room.Tick);
    }
}
