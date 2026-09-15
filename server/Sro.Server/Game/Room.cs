using System.Globalization;
using System.Text;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Логика одной звёздной системы: игроки, сессии, NPC, шаг симуляции, бой, снапшоты. Вызывается только из потока тика
/// (<see cref="SystemRoom"/>), своих потоков и таймеров не имеет — поэтому тестируется напрямую.
/// </summary>
public sealed class Room
{
    public const int MaxNameLength = 16;
    public const string DefaultName = "Рейнджер";

    /// <summary>Столько корабль без связи ждёт возвращения игрока, потом удаляется.</summary>
    public const int ReconnectGraceTicks = 60 * SimConfig.TickRate;

    /// <summary>Код закрытия WebSocket: к кораблю подключилось новое соединение с той же сессией.</summary>
    public const int ReplacedCloseCode = 4001;

    private const int MinTokenLength = 16;
    private const int MaxTokenLength = 64;

    private readonly ILogger _log;
    private readonly Random _jitter;
    private readonly Random _ai;
    private readonly Battle _battle;
    private readonly Dictionary<int, Player> _players = [];
    private readonly Dictionary<int, Player> _byConnection = [];
    private readonly Dictionary<string, Player> _byToken = new(StringComparer.Ordinal);
    /// <summary>Все корабли системы — игроки и NPC; здесь ищется цель.</summary>
    private readonly Dictionary<int, ShipEntity> _ships = [];
    private readonly List<Drone> _drones = [];
    private readonly List<Pirate> _pirates = [];
    private readonly List<ShipDto> _shipDtos = [];
    private readonly List<ShotDto> _shots = [];
    private readonly List<KillDto> _kills = [];
    private readonly List<Player> _expired = [];
    private readonly MoveInput[] _steps = new MoveInput[InputBuffer.MaxBudget];
    private int _nextId;

    /// <param name="roll">Случайное число из [0, 1) для бросков попадания; тесты подставляют своё.</param>
    /// <param name="jitter">Разброс точки появления.</param>
    /// <param name="ai">Случайность ИИ пиратов (точки патруля) — отдельно, чтобы пираты не сдвигали разброс спауна.</param>
    public Room(Balance balance, ILogger log, Func<double>? roll = null, Random? jitter = null, Random? ai = null)
    {
        Balance = balance;
        _log = log;
        _jitter = jitter ?? Random.Shared;
        _ai = ai ?? Random.Shared;
        _battle = new Battle(roll ?? Random.Shared.NextDouble, log);
        SpawnDrones();
        SpawnPirates();
    }

    public long Tick { get; private set; }
    public Balance Balance { get; private set; }
    public IReadOnlyDictionary<string, HullParams> Hulls => Balance.Hulls;

    /// <summary>Число игроков; NPC не считаются.</summary>
    public int Count => _players.Count;

    /// <summary>Корабль по id — игрок или NPC.</summary>
    public ShipEntity? Entity(int id) => _ships.GetValueOrDefault(id);

    public void Join(IClientConnection connection, string? token, string? name, string? hull, string? weapon = null)
    {
        if (_byConnection.ContainsKey(connection.Id)) return;
        if (!IsValidToken(token)) token = null;
        var hullId = hull is not null && Hulls.ContainsKey(hull) ? hull : null;
        var weaponId = weapon is not null && Balance.Weapons.ContainsKey(weapon) ? weapon : null;

        if (token is not null && _byToken.TryGetValue(token, out var player))
        {
            // Старое соединение могло ещё не заметить обрыв (iOS усыпил вкладку) или это дубль вкладки.
            if (player.Connection is { } old)
            {
                _byConnection.Remove(old.Id);
                old.Close(ReplacedCloseCode, "replaced");
            }
            player.Attach(connection);
            player.Name = UniqueName(SanitizeName(name), player);
            if (hullId is not null) ChangeHull(player, hullId);
            if (weaponId is not null) player.WeaponId = weaponId;
            _byConnection[connection.Id] = player;
            connection.Send(Welcome(player, resumed: true));
            BroadcastPlayers();
            _log.LogInformation("Player {Id} '{Name}' resumed, online {Count}", player.Id, player.Name, OnlineCount());
            return;
        }

        player = new Player(
            ++_nextId,
            token,
            UniqueName(SanitizeName(name), null),
            hullId ?? SimConfig.DefaultHull,
            weaponId ?? SimConfig.DefaultWeapon);
        Spawn(player);
        player.Attach(connection);
        _players[player.Id] = player;
        _ships[player.Id] = player;
        _byConnection[connection.Id] = player;
        if (token is not null) _byToken[token] = player;
        connection.Send(Welcome(player, resumed: false));
        BroadcastPlayers();
        _log.LogInformation("Player {Id} '{Name}' joined as {Hull}, online {Count}", player.Id, player.Name, player.HullId, OnlineCount());
    }

    /// <summary>Соединение закрылось. Корабль остаётся ждать игрока, если у того есть сессия.</summary>
    public void Disconnect(IClientConnection connection)
    {
        // Соединение, которое уже вытеснили новым, здесь не найдётся и корабль не отцепит.
        if (!_byConnection.Remove(connection.Id, out var player)) return;
        if (player.Token is null)
        {
            Remove(player);
            _log.LogInformation("Player {Id} left, online {Count}", player.Id, OnlineCount());
        }
        else
        {
            player.Detach(Tick);
            _log.LogInformation("Player {Id} lost connection, online {Count}", player.Id, OnlineCount());
        }
        BroadcastPlayers();
    }

    public void Input(IClientConnection connection, int seq, MoveInput input)
    {
        if (_byConnection.TryGetValue(connection.Id, out var player)) player.Inputs.Enqueue(seq, input);
    }

    public void SetHull(IClientConnection connection, string? hullId)
    {
        if (hullId is null || !Hulls.ContainsKey(hullId) || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        ChangeHull(player, hullId);
        _log.LogInformation("Player {Id} switched to {Hull}", player.Id, hullId);
    }

    public void SetWeapon(IClientConnection connection, string? weaponId)
    {
        if (weaponId is null || !Balance.Weapons.ContainsKey(weaponId) || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        player.WeaponId = weaponId;
        _log.LogInformation("Player {Id} switched to {Weapon}", player.Id, weaponId);
    }

    /// <summary>Цель выбирает клиент. Себя, несуществующий корабль и 0 сервер понимает как «цели нет».</summary>
    public void SetTarget(IClientConnection connection, int targetId)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player)) return;
        player.TargetId = targetId != player.Id && _ships.ContainsKey(targetId) ? targetId : 0;
    }

    public void SetFire(IClientConnection connection, bool on)
    {
        if (_byConnection.TryGetValue(connection.Id, out var player)) player.FireHeld = on;
    }

    public void Rename(IClientConnection connection, string? name)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player)) return;
        var unique = UniqueName(SanitizeName(name), player);
        if (unique == player.Name) return;
        _log.LogInformation("Player {Id} renamed '{Old}' → '{New}'", player.Id, player.Name, unique);
        player.Name = unique;
        BroadcastPlayers();
    }

    /// <summary>
    /// Баланс изменился на диске: доли корпуса и щита сохраняются. Дроны и пираты пересоздаются, если поменялся
    /// их список; иначе пираты получают новые параметры типа при тех же долях.
    /// </summary>
    public void ApplyBalance(Balance balance)
    {
        var old = Balance;
        foreach (var ship in _ships.Values)
        {
            if (ship is Pirate) continue; // у пирата корпус и максимумы — от типа, см. ниже
            var from = ship.Hull(old.Hulls);
            var hullId = balance.Hulls.ContainsKey(ship.HullId) ? ship.HullId : SimConfig.DefaultHull;
            ship.ChangeHull(from, hullId, balance.Hulls[hullId]);
            if (!balance.Weapons.ContainsKey(ship.WeaponId)) ship.WeaponId = SimConfig.DefaultWeapon;
        }
        Balance = balance;

        if (old.Npc.SpawnList.SequenceEqual(balance.Npc.SpawnList))
        {
            // Список логов тот же — значит, все их типы есть и в новом файле.
            foreach (var pirate in _pirates) pirate.Rebind(balance.Npc.TypeMap[pirate.Spawn.Type], balance.Npc, old.Hulls, balance.Hulls);
        }
        else
        {
            foreach (var pirate in _pirates) RemoveShip(pirate);
            _pirates.Clear();
            SpawnPirates();
        }

        var message = Protocol.Encode(new ConfigMsg(balance.Hulls, balance.Weapons, balance.Rules, balance.Npc));
        foreach (var player in _players.Values) player.Connection?.SendRaw(message);

        if (!old.Rules.DroneList.SequenceEqual(balance.Rules.DroneList))
        {
            foreach (var drone in _drones) RemoveShip(drone);
            _drones.Clear();
            SpawnDrones();
        }
        // Имена и максимумы NPC уходят клиентам только в списке кораблей.
        BroadcastPlayers();
    }

    /// <summary>Один тик: движение, бой и снапшот подключённым игрокам.</summary>
    public void Step()
    {
        foreach (var player in _players.Values) Move(player);
        foreach (var drone in _drones)
        {
            var input = drone.NextInput();
            if (!drone.IsDead) Movement.Step(ref drone.Ship, input, drone.Hull(Hulls), SimConfig.Dt);
        }
        // Уничтоженный пират не думает: иначе снова взял бы огонь, который Battle снял при смерти.
        foreach (var pirate in _pirates)
        {
            if (pirate.IsDead) continue;
            PirateBrain.Think(pirate, _ships, _pirates, Balance, Tick, _ai, _log);
            Movement.Step(ref pirate.Ship, pirate.LastInput, pirate.Hull(Hulls), SimConfig.Dt);
        }
        Tick++;

        if (_expired.Count > 0)
        {
            foreach (var player in _expired)
            {
                Remove(player);
                _log.LogInformation("Player {Id} did not come back, removed", player.Id);
            }
            _expired.Clear();
            BroadcastPlayers();
        }

        _battle.Run(Tick, _ships, Balance, _shots, _kills, Spawn);
        // Дроны есть всегда: без игроков онлайн снапшот не нужен никому.
        if (_byConnection.Count > 0) SendSnapshot();
        _shots.Clear();
        _kills.Clear();
    }

    /// <summary>Без управляющих и невидимых символов, пробелы схлопнуты, не длиннее MaxNameLength.</summary>
    public static string SanitizeName(string? name)
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsControl(c) || category == UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        var trimmed = sb.ToString().Trim();
        return trimmed.Length == 0 ? DefaultName : Truncate(trimmed, MaxNameLength);
    }

    public static bool IsValidToken(string? token) =>
        token is { Length: >= MinTokenLength and <= MaxTokenLength } &&
        token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Шаг игрока. Уничтоженный корабль входы потребляет — ack растёт, и после респауна клиент переигрывает
    /// только новые входы, — но не двигается.
    /// </summary>
    private void Move(Player player)
    {
        var hull = player.Hull(Hulls);
        if (player.Connection is null)
        {
            if (Tick - player.LostAtTick >= ReconnectGraceTicks)
            {
                _expired.Add(player);
                return;
            }
            if (!player.IsDead) Movement.Step(ref player.Ship, player.StopInput, hull, SimConfig.Dt);
            return;
        }

        var count = player.Inputs.Tick(_steps);
        if (player.IsDead) return;
        for (var i = 0; i < count; i++) Movement.Step(ref player.Ship, _steps[i], hull, SimConfig.Dt);
    }

    /// <summary>
    /// Появление при входе и после уничтожения: у станции, с защитой (GDD §25). Дрон — у своего дома, пират — в логове;
    /// NPC без защиты.
    /// </summary>
    private void Spawn(ShipEntity ship)
    {
        var (x, y) = ship switch
        {
            Drone drone => drone.SpawnPoint,
            Pirate pirate => pirate.SpawnPoint,
            _ => SpawnPoint(),
        };
        ship.Ship = new ShipState { X = x, Y = y };
        ship.Revive(ship.Hull(Hulls), ship is Player ? Tick + Balance.Rules.ProtectionTicks : 0);
        if (ship is Pirate p) p.ResetAi();
    }

    /// <summary>Случайная точка в круге SpawnJitter вокруг спауна — корабли не появляются друг в друге.</summary>
    private (double X, double Y) SpawnPoint()
    {
        var radius = Balance.Rules.SpawnJitter * Math.Sqrt(_jitter.NextDouble());
        var angle = _jitter.NextDouble() * 2 * Math.PI;
        return (SimConfig.SpawnX + radius * Math.Cos(angle), SimConfig.SpawnY + radius * Math.Sin(angle));
    }

    private void SpawnDrones()
    {
        foreach (var spec in Balance.Rules.DroneList)
        {
            var drone = new Drone(++_nextId, spec);
            Spawn(drone);
            _drones.Add(drone);
            _ships[drone.Id] = drone;
        }
    }

    /// <summary>Пираты по логовам; номер в логове сквозной для всех записей с одной точкой.</summary>
    private void SpawnPirates()
    {
        var npc = Balance.Npc;
        var slots = new Dictionary<(double, double), int>();
        foreach (var spawn in npc.SpawnList)
        {
            var type = npc.TypeMap[spawn.Type];
            for (var i = 0; i < spawn.Count; i++)
            {
                var slot = slots.GetValueOrDefault((spawn.X, spawn.Y));
                slots[(spawn.X, spawn.Y)] = slot + 1;
                var pirate = new Pirate(++_nextId, spawn, slot, type, npc);
                Spawn(pirate);
                _pirates.Add(pirate);
                _ships[pirate.Id] = pirate;
            }
        }
    }

    private void ChangeHull(ShipEntity ship, string hullId)
    {
        if (ship.HullId != hullId) ship.ChangeHull(ship.Hull(Hulls), hullId, Hulls[hullId]);
    }

    private void SendSnapshot()
    {
        _shipDtos.Clear();
        foreach (var ship in _ships.Values) _shipDtos.Add(ToDto(ship));
        // Все получают одни и те же байты: ack каждого игрока лежит в записи его корабля.
        var snapshot = Protocol.Encode(new SnapshotMsg(
            Tick,
            _shipDtos,
            _shots.Count > 0 ? _shots : null,
            _kills.Count > 0 ? _kills : null));
        foreach (var player in _players.Values) player.Connection?.SendRaw(snapshot);
    }

    private ShipDto ToDto(ShipEntity ship)
    {
        var s = ship.Ship;
        var (throttle, ack) = ship switch
        {
            Player p => (p.Connection is null || p.IsDead ? 0 : p.Inputs.Last.Throttle, p.Inputs.AckSeq),
            Drone d => (d.IsDead ? 0 : d.LastInput.Throttle, 0),
            Pirate p => (p.IsDead ? 0 : p.LastInput.Throttle, 0),
            _ => (0.0, 0),
        };
        var pirate = ship as Pirate;
        return new ShipDto(
            ship.Id, s.X, s.Y, s.Rot, s.Vx, s.Vy, ship.HullId, throttle, ack,
            (int)Math.Ceiling(ship.Hp),
            (int)Math.Ceiling(ship.Shield),
            ship.WeaponId,
            ship.DeadUntilTick,
            ship.IsProtected(Tick) ? ship.ProtectedUntilTick : 0,
            pirate is { IsDead: false, State: PirateState.Attack } ? pirate.TargetId : 0,
            pirate is null ? null : AiNames[(int)pirate.State]);
    }

    /// <summary>Состояния ИИ в снапшоте — по индексу <see cref="PirateState"/>.</summary>
    private static readonly string[] AiNames = ["patrol", "attack", "return"];

    private WelcomeMsg Welcome(Player player, bool resumed) =>
        new(player.Id, SimConfig.TickRate, Protocol.Version, Hulls, Balance.Weapons, Balance.Rules, resumed, Balance.Npc);

    /// <summary>Занятое другим игроком имя получает номер: «Имя 2», «Имя 3»…</summary>
    private string UniqueName(string name, Player? self)
    {
        var candidate = name;
        for (var n = 2; IsTaken(candidate, self); n++)
        {
            var suffix = $" {n}";
            candidate = Truncate(name, MaxNameLength - suffix.Length).TrimEnd() + suffix;
        }
        return candidate;
    }

    /// <summary>Имя занято другим игроком или NPC: игрок не назовётся «Пират Ур.2».</summary>
    private bool IsTaken(string name, Player? self)
    {
        foreach (var ship in _ships.Values)
        {
            if (ship != self && string.Equals(ship.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Не разрезает суррогатную пару (эмодзи) пополам.</summary>
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var length = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..length];
    }

    private void Remove(Player player)
    {
        _players.Remove(player.Id);
        if (player.Token is not null) _byToken.Remove(player.Token);
        RemoveShip(player);
    }

    /// <summary>Корабль ушёл из системы: у тех, кто в него целился, цели больше нет.</summary>
    private void RemoveShip(ShipEntity ship)
    {
        _ships.Remove(ship.Id);
        foreach (var other in _ships.Values)
        {
            if (other.TargetId == ship.Id) other.TargetId = 0;
        }
    }

    private int OnlineCount() => _byConnection.Count;

    private static int? Ceiling(double? value) => value is { } v ? (int)Math.Ceiling(v) : null;

    /// <summary>Игроки и NPC: имена нужны для подписей, флаг npc — чтобы клиент не писал о NPC в ленту, kind — для цвета.</summary>
    private void BroadcastPlayers()
    {
        var list = _ships.Values
            .OrderBy(s => s.Id)
            .Select(s => s switch
            {
                Player p => new PlayerDto(p.Id, p.Name, p.Connection is not null),
                Drone d => new PlayerDto(d.Id, d.Name, Online: true, Npc: true, Ceiling(d.Spec.Hp), Ceiling(d.Spec.Shield), Protocol.DroneKind),
                Pirate p => new PlayerDto(
                    p.Id, p.Name, Online: true, Npc: true,
                    Ceiling(p.MaxHp(p.Hull(Hulls))), Ceiling(p.MaxShield(p.Hull(Hulls))), Protocol.PirateKind),
                _ => new PlayerDto(s.Id, s.Name, Online: true, Npc: true),
            })
            .ToList();
        var message = Protocol.Encode(new PlayersMsg(list));
        foreach (var player in _players.Values) player.Connection?.SendRaw(message);
    }
}
