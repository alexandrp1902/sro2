using System.Diagnostics;
using Sro.Server.Accounts;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>Что комната системы знает о галактике вокруг неё.</summary>
public interface IRoomHost
{
    /// <summary>Имя занято игроком или NPC в любой системе.</summary>
    bool IsNameTaken(string name, Player? self);

    /// <summary>Пилотов на связи во всей галактике.</summary>
    int OnlineTotal { get; }

    /// <summary>
    /// Корабль уходит в систему to — гиперпрыжком (jump) или домой после гибели. Переводит галактика, сразу после шага
    /// комнаты: посреди шага состав её кораблей меняться не должен.
    /// </summary>
    void Depart(Room from, Player player, string to, bool jump);
}

/// <summary>
/// Галактика (GDD §4, §63–64): комнаты всех систем в одном процессе и один поток тика на всех, поэтому
/// гиперпрыжок — синхронный перевод игрока из комнаты в комнату, без гонок. Сессии, ник и ключ возврата
/// ищутся по всей галактике: корабль, ждущий после обрыва связи, находится, в какой бы системе он ни был.
/// Вызывается только из потока тика (<see cref="GalaxyHost"/>) — поэтому тестируется напрямую, как <see cref="Room"/>.
/// </summary>
public sealed class Galaxy : IRoomHost
{
    private readonly ILogger _log;
    private readonly AccountStore? _accounts;
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Room> _byConnection = [];
    private readonly List<(Room From, Player Player, string To, bool Jump)> _departures = [];
    private readonly double[] _stepMs;
    private int _nextId;
    private int _lastTotal;

    /// <param name="roll">Броски попадания для всех комнат; тесты подставляют своё.</param>
    /// <param name="orbitEpoch">Орбитальное время в тик 0, секунды; null — сейчас по unix-часам.</param>
    public Galaxy(
        Balance balance,
        ILogger log,
        AccountStore? accounts = null,
        Func<double>? roll = null,
        Func<int, Random>? random = null,
        double? orbitEpoch = null)
    {
        Balance = balance;
        _log = log;
        _accounts = accounts;
        var seed = 0;
        var epoch = orbitEpoch ?? Room.Now();
        foreach (var id in balance.Galaxy.SystemMap.Keys)
        {
            // У каждой комнаты свои генераторы: общий Random.Shared из одного потока годится, но тестам нужен сид.
            Random? Next() => random?.Invoke(seed++);
            _rooms[id] = new Room(balance.ForSystem(id), log, roll, Next(), Next(), Next(), Next(), accounts, this, () => ++_nextId, epoch);
        }
        _stepMs = new double[_rooms.Count];
    }

    public Balance Balance { get; private set; }

    public IReadOnlyCollection<Room> Rooms => _rooms.Values;

    public Room this[string system] => _rooms[system];

    /// <summary>Тик галактики: комнаты шагают вместе, у всех он один.</summary>
    public long Tick => _rooms.Values.First().Tick;

    public int OnlineTotal => _rooms.Values.Sum(r => r.OnlineCount);

    /// <summary>Время шага каждой комнаты в последнем тике, мс, в порядке <see cref="Rooms"/>.</summary>
    public IReadOnlyList<double> StepMs => _stepMs;

    /// <summary>Комната, где сейчас корабль этого соединения.</summary>
    public Room? RoomOf(IClientConnection connection) => _byConnection.GetValueOrDefault(connection.Id);

    /// <summary>Гость: в системе, где ждёт его корабль, иначе — в стартовой.</summary>
    public void Join(IClientConnection connection, string? token, string? name, string? hull, string? weapon = null)
    {
        if (_byConnection.ContainsKey(connection.Id)) return;
        var room = _rooms.Values.FirstOrDefault(r => r.HasGuest(token)) ?? Start;
        room.Join(connection, token, name, hull, weapon);
        _byConnection[connection.Id] = room;
    }

    /// <summary>
    /// Пилот с аккаунтом: к кораблю, который ещё в игре (ждёт после обрыва или летает с другого устройства), —
    /// где бы тот ни был; иначе — к станции системы, где пилот стыковался последний раз.
    /// </summary>
    public void JoinAccount(IClientConnection connection, string accountId, string name)
    {
        if (_byConnection.ContainsKey(connection.Id)) return;
        var room = _rooms.Values.FirstOrDefault(r => r.HasToken(accountId)) ?? Home(_accounts?.Profile(accountId)?.System);
        room.JoinAccount(connection, accountId, name);
        _byConnection[connection.Id] = room;
    }

    public void Disconnect(IClientConnection connection)
    {
        if (_byConnection.Remove(connection.Id, out var room)) room.Disconnect(connection);
    }

    /// <summary>Команда игрока — в комнату, где сейчас его корабль.</summary>
    public void With(IClientConnection connection, Action<Room> command)
    {
        if (_byConnection.TryGetValue(connection.Id, out var room)) command(room);
    }

    /// <summary>Тик всех систем; переходы между ними — сразу после шага комнаты, откуда корабль уходит.</summary>
    public void Step()
    {
        var i = 0;
        foreach (var room in _rooms.Values)
        {
            var started = Stopwatch.GetTimestamp();
            room.Step();
            _stepMs[i++] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (_departures.Count > 0) Transfer();
        }

        // «Онлайн» в статусе — по всей галактике: вошли в одной системе — узнают и в остальных.
        var total = OnlineTotal;
        if (total == _lastTotal) return;
        _lastTotal = total;
        foreach (var room in _rooms.Values) room.BroadcastPlayers();
    }

    /// <summary>
    /// Баланс изменился на диске. Каждая комната получает свой вид; набор систем меняется только перезапуском —
    /// корабли в исчезнувшей системе было бы некуда деть.
    /// </summary>
    public void ApplyBalance(Balance balance)
    {
        var systems = balance.Galaxy.SystemMap.Keys;
        if (systems.Count() != _rooms.Count || !systems.All(_rooms.ContainsKey))
        {
            _log.LogWarning("Balance rejected: the set of systems in galaxy.json changed, restart the server to apply it");
            return;
        }
        Balance = balance;
        foreach (var (id, room) in _rooms) room.ApplyBalance(balance.ForSystem(id));
    }

    public bool IsNameTaken(string name, Player? self) => _rooms.Values.Any(r => r.NameTaken(name, self));

    public void Depart(Room from, Player player, string to, bool jump) => _departures.Add((from, player, to, jump));

    private void Transfer()
    {
        foreach (var (from, player, to, jump) in _departures)
        {
            if (from.Pilot(player.Id) != player) continue; // уже ушёл: две причины в одном тике
            var room = jump ? _rooms.GetValueOrDefault(to) : Home(to);
            if (room is null) continue;
            var arrival = jump ? Balance.Galaxy.Arrival(from.SystemId, room.SystemId) : null;
            from.Release(player);
            if (!jump) player.Home = room.SystemId; // дом без станции (правка баланса) — домом становится стартовая
            room.Admit(player, arrival);
            from.BroadcastPlayers();
            if (player.Connection is { } connection) _byConnection[connection.Id] = room;
            _log.LogInformation(
                "Player {Id} moved {From} → {To} ({Reason})", player.Id, from.SystemId, room.SystemId, jump ? "jump" : "respawn");
        }
        _departures.Clear();
    }

    private Room Start => _rooms.GetValueOrDefault(Balance.Galaxy.StartSystem) ?? _rooms.Values.First();

    /// <summary>Дом пилота: система со станцией; иначе — стартовая.</summary>
    private Room Home(string? system) =>
        system is not null && _rooms.TryGetValue(system, out var room) && room.Balance.HasStation ? room : Start;
}
