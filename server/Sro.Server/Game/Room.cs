using System.Globalization;
using System.Text;
using Sro.Server.Accounts;
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
    private readonly LootSystem _loot;
    private readonly MeteorSystem _meteors;
    private readonly AccountStore? _accounts;
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
    /// <param name="loot">Случайность дропа — тоже отдельно: иначе добыча сдвигала бы разброс спауна.</param>
    /// <param name="meteors">Случайность метеоритов: размер, трасса, скорость, интервал.</param>
    /// <param name="accounts">Аккаунты пилотов; null — сохранять некуда (тесты без аккаунтов).</param>
    public Room(
        Balance balance,
        ILogger log,
        Func<double>? roll = null,
        Random? jitter = null,
        Random? ai = null,
        Random? loot = null,
        Random? meteors = null,
        AccountStore? accounts = null)
    {
        Balance = balance;
        _log = log;
        _jitter = jitter ?? Random.Shared;
        _ai = ai ?? Random.Shared;
        _battle = new Battle(roll ?? Random.Shared.NextDouble, log);
        _loot = new LootSystem(() => ++_nextId, loot ?? Random.Shared, log);
        _loot.SetContainers(balance.Loot.ContainerList);
        _meteors = new MeteorSystem(() => ++_nextId, meteors ?? Random.Shared);
        _accounts = accounts;
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

    /// <summary>Игрок по id — в том числе в доке, когда его корабля в космосе нет.</summary>
    public Player? Pilot(int id) => _players.GetValueOrDefault(id);

    /// <summary>Метеориты в полёте.</summary>
    public IReadOnlyList<Meteor> Meteors => _meteors.Alive;

    /// <summary>
    /// Метеорит заданного размера в точке (x, y) со скоростью (vx, vy) — для тестов и отладки.
    /// Обычно они появляются сами, по таймеру из meteors.json.
    /// </summary>
    /// <returns>null — такого размера в meteors.json нет.</returns>
    public Meteor? LaunchMeteor(string sizeId, double x, double y, double vx, double vy)
    {
        var rules = Balance.Meteors;
        if (!rules.SizeMap.TryGetValue(sizeId, out var size)) return null;
        var meteor = _meteors.Add(sizeId, size, x, y, vx, vy, Tick + rules.LifetimeTicks);
        _ships[meteor.Id] = meteor;
        return meteor;
    }

    /// <summary>Гость без аккаунта: ничего не сохраняется, весь ангар открыт. Так входят тесты и смоук-скрипты.</summary>
    public void Join(IClientConnection connection, string? token, string? name, string? hull, string? weapon = null)
    {
        if (_byConnection.ContainsKey(connection.Id)) return;
        if (!IsValidToken(token)) token = null;
        var hullId = hull is not null && Hulls.ContainsKey(hull) ? hull : null;
        var weaponId = weapon is not null && Balance.Weapons.ContainsKey(weapon) ? weapon : null;

        if (token is not null && _byToken.TryGetValue(token, out var player) && player.IsGuest)
        {
            player.Name = UniqueName(SanitizeName(name), player);
            if (hullId is not null) ChangeHull(player, hullId);
            if (weaponId is not null) player.WeaponId = weaponId;
            Resume(player, connection);
            return;
        }

        player = new Player(
            ++_nextId,
            token,
            UniqueName(SanitizeName(name), null),
            hullId ?? SimConfig.DefaultHull,
            weaponId ?? SimConfig.DefaultWeapon)
        {
            Credits = Balance.Shop.StartCredits,
        };
        Enter(player, connection);
    }

    /// <summary>
    /// Пилот с аккаунтом: ник и пароль (или ключ устройства) уже проверены в сетевом потоке, см. <see cref="AccountStore"/>.
    /// Корабль, который ещё ждёт после обрыва, достаётся новому соединению — даже с другого устройства (GDD §61).
    /// </summary>
    public void JoinAccount(IClientConnection connection, string accountId, string name)
    {
        if (_byConnection.ContainsKey(connection.Id)) return;
        if (_byToken.TryGetValue(accountId, out var player))
        {
            Resume(player, connection);
            return;
        }

        var profile = _accounts?.Profile(accountId);
        player = new Player(++_nextId, accountId, UniqueName(SanitizeName(name), null), SimConfig.DefaultHull, SimConfig.DefaultWeapon, accountId);
        if (profile is null)
        {
            player.Credits = Balance.Shop.StartCredits; // GDD §54: новый пилот получает стартовый капитал
        }
        else
        {
            player.Credits = profile.Credits;
            // Корпус или пушку могли убрать из баланса, пока пилот отсутствовал, — такие просто пропадают из ангара.
            foreach (var id in profile.Hulls) if (Hulls.ContainsKey(id)) player.Hulls.Add(id);
            foreach (var id in profile.Weapons) if (Balance.Weapons.ContainsKey(id)) player.Weapons.Add(id);
            if (player.Hulls.Contains(profile.Hull) && Hulls.ContainsKey(profile.Hull)) player.HullId = profile.Hull;
            if (player.Weapons.Contains(profile.Weapon) && Balance.Weapons.ContainsKey(profile.Weapon)) player.WeaponId = profile.Weapon;
            foreach (var (item, count) in profile.Cargo) if (count > 0) player.Cargo.Add(item, count);
        }
        Enter(player, connection);
        if (profile is null) Save(player);
    }

    /// <summary>Новый корабль в системе: у станции, с защитой (GDD §25).</summary>
    private void Enter(Player player, IClientConnection connection)
    {
        Spawn(player);
        player.Attach(connection);
        _players[player.Id] = player;
        _ships[player.Id] = player;
        _byConnection[connection.Id] = player;
        if (player.Token is not null) _byToken[player.Token] = player;
        connection.Send(Welcome(player, resumed: false));
        BroadcastPlayers();
        SendCargo(player); // трюм пуст, но клиенту нужна ёмкость корпуса
        SendHangar(player);
        _log.LogInformation("Player {Id} '{Name}' joined as {Hull}, online {Count}", player.Id, player.Name, player.HullId, OnlineCount());
    }

    /// <summary>Возврат к кораблю, который ждал после обрыва связи.</summary>
    private void Resume(Player player, IClientConnection connection)
    {
        // Старое соединение могло ещё не заметить обрыв (iOS усыпил вкладку), или это дубль вкладки, или другое устройство.
        if (player.Connection is { } old)
        {
            _byConnection.Remove(old.Id);
            old.Close(ReplacedCloseCode, "replaced");
        }
        player.Attach(connection);
        _byConnection[connection.Id] = player;
        connection.Send(Welcome(player, resumed: true));
        BroadcastPlayers();
        SendCargo(player); // иначе вернувшийся видел бы пустой трюм до первого подбора
        SendHangar(player); // в том числе — что корабль всё ещё в доке
        _log.LogInformation("Player {Id} '{Name}' resumed, online {Count}", player.Id, player.Name, OnlineCount());
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

    /// <summary>Поставить корпус из ангара (GDD §51): пилоту с аккаунтом — только свой и только в доке.</summary>
    public void SetHull(IClientConnection connection, string? hullId)
    {
        if (hullId is null || !Hulls.ContainsKey(hullId) || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        if (!player.IsGuest && (!player.Docked || !player.OwnsHull(hullId))) return;
        ChangeHull(player, hullId);
        SendCargo(player); // у нового корпуса своя ёмкость; груз при этом не выбрасывается (GDD §24)
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} switched to {Hull}", player.Id, hullId);
    }

    /// <summary>Поставить пушку: пилоту с аккаунтом — только свою и только в доке.</summary>
    public void SetWeapon(IClientConnection connection, string? weaponId)
    {
        if (weaponId is null || !Balance.Weapons.ContainsKey(weaponId) || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        if (!player.IsGuest && (!player.Docked || !player.OwnsWeapon(weaponId))) return;
        player.WeaponId = weaponId;
        SendHangar(player);
        Save(player);
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

    /// <summary>Выбранный предмет (боевой документ §45): тап только помечает его, автопилота в MVP нет.</summary>
    public void SetLootTarget(IClientConnection connection, int lootId)
    {
        if (_byConnection.TryGetValue(connection.Id, out var player)) player.SelectedLootId = lootId;
    }

    /// <summary>Смена ника — только у гостя: у пилота с аккаунтом ник и есть вход.</summary>
    public void Rename(IClientConnection connection, string? name)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player) || !player.IsGuest) return;
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
        // Корабли в доке — не в космосе, но корпус и пушка у них те же, что и у всех.
        foreach (var ship in _ships.Values.Concat(DockedPlayers()))
        {
            // У пирата корпус и максимумы — от типа, см. ниже; у метеорита корпуса нет, летящий камень баланс не меняет.
            if (ship is Pirate or Meteor) continue;
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

        if (!old.Loot.ContainerList.SequenceEqual(balance.Loot.ContainerList)) _loot.SetContainers(balance.Loot.ContainerList);
        if (_loot.DropUnknown(balance.Loot)) ClearMissingLootTargets();

        var message = Protocol.Encode(new ConfigMsg(balance.Hulls, balance.Weapons, balance.Rules, balance.Npc, balance.Loot, balance.Meteors, balance.Shop));
        foreach (var player in _players.Values)
        {
            player.Connection?.SendRaw(message);
            SendCargo(player); // объёмы предметов и ёмкость корпуса могли измениться
            SendHangar(player); // корпус или пушку могли убрать из баланса
        }

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
        _meteors.Move(Balance.Meteors);
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

        StepMeteors();
        // Таран — до боя: урон камня и урон пушек попадают в один свод смертей, и корабль, добитый
        // одновременно пиратом и метеоритом, погибает один раз.
        _meteors.Collide(_ships, Balance, Tick, _shots);
        _battle.Run(Tick, _ships, Balance, _shots, _kills, Spawn);
        foreach (var meteor in _meteors.Shatter(_loot, Balance.Loot, Tick)) RemoveShip(meteor);
        // Дроп после боя: предмет должен пролежать хотя бы тик, иначе игрок вплотную к убитому
        // увидит «ничего не выпало», а трюм молча пополнится.
        _loot.DropFrom(_kills, _ships, Balance.Loot, Tick);
        // Подбор и продажа — по команде игрока, а не сами собой: см. Grab и Sell.
        if (_loot.Step(Tick, Balance.Loot)) ClearMissingLootTargets();

        // Дроны есть всегда: без игроков онлайн снапшот не нужен никому.
        if (_byConnection.Count > 0) SendSnapshot();
        _shots.Clear();
        _kills.Clear();
        _loot.ClearPicks();
    }

    /// <summary>Улетевшие метеориты исчезают, новые появляются по таймеру — только пока в системе кто-то есть.</summary>
    private void StepMeteors()
    {
        var rules = Balance.Meteors;
        foreach (var meteor in _meteors.Expired(rules, Tick)) RemoveShip(meteor);
        if (!_meteors.Due(rules, _byConnection.Count > 0)) return;
        if (_meteors.Launch(rules, Balance.Npc.StationSafeRadius, Tick) is { } launched) _ships[launched.Id] = launched;
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
            if (!player.IsDead && !player.Docked) Movement.Step(ref player.Ship, player.StopInput, hull, SimConfig.Dt);
            return;
        }
        // В доке входы не нужны: клиент их не шлёт, а при вылете буфер начинается заново.
        if (player.Docked) return;

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
        if (ship is Meteor) return; // разбитый камень не возвращается — его убирает MeteorSystem.Shatter
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
        foreach (var ship in _ships.Values)
        {
            if (ship is not Meteor) _shipDtos.Add(ToDto(ship));
        }
        // Все получают одни и те же байты: ack каждого игрока лежит в записи его корабля.
        var snapshot = Protocol.Encode(new SnapshotMsg(
            Tick,
            _shipDtos,
            _shots.Count > 0 ? _shots : null,
            _kills.Count > 0 ? _kills : null,
            _loot.ToDtos(),
            _loot.Picks.Count > 0 ? _loot.Picks : null,
            _meteors.ToDtos()));
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
        new(player.Id, SimConfig.TickRate, Protocol.Version, Hulls, Balance.Weapons, Balance.Rules, resumed, Balance.Npc, Balance.Loot, Balance.Meteors, Balance.Shop);

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
        foreach (var ship in _ships.Values.Concat(DockedPlayers()))
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
        Save(player);
        _players.Remove(player.Id);
        if (player.Token is not null) _byToken.Remove(player.Token);
        RemoveShip(player);
    }

    /// <summary>
    /// Взять выбранный предмет (GDD §21). Подбор ручной: игрок помечает предмет, подлетает сам и забирает
    /// его командой — тракторный луч не хватает всё подряд по дороге.
    /// </summary>
    public void Grab(IClientConnection connection)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player) || player.IsDead) return;
        if (player.SelectedLootId == 0) return;

        switch (_loot.TryGrab(player, player.SelectedLootId, Balance.Loot, Hulls, Tick))
        {
            case LootSystem.GrabResult.Taken:
                player.SelectedLootId = 0;
                SendCargo(player);
                Save(player);
                break;
            case LootSystem.GrabResult.NoRoom:
                WarnCargoFull(player);
                break;
            case LootSystem.GrabResult.TooFar:
                connection.Send(new NoticeMsg(Protocol.TooFarNotice));
                break;
            default:
                player.SelectedLootId = 0; // предмет успели забрать или он протух
                break;
        }
    }

    /// <summary>
    /// Продать груз в доке: item — что именно, null — весь трюм. Сдача ручная, чтобы игрок решал,
    /// что везти дальше, а что менять на кредиты.
    /// </summary>
    public void Sell(IClientConnection connection, string? item)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player)) return;
        var loot = Balance.Loot;
        if (!loot.StationUnload || player.Cargo.IsEmpty) return;
        if (!player.Docked)
        {
            connection.Send(new NoticeMsg(Protocol.TooFarNotice));
            return;
        }

        int credits;
        if (item is null)
        {
            credits = player.Cargo.Price(loot);
            player.Cargo.Clear();
        }
        else
        {
            // Наличие проверяем до изъятия: иначе бесценный груз пропал бы, не принеся кредитов.
            if (!player.Cargo.Items.ContainsKey(item)) return;
            credits = player.Cargo.Take(item, loot);
        }

        player.Credits += credits;
        player.CargoFullUntilTick = 0;
        connection.Send(new NoticeMsg(Protocol.UnloadedNotice));
        SendCargo(player);
        Save(player);
        _log.LogInformation("Player {Id} sold cargo for {Credits} credits", player.Id, credits);
    }

    /// <summary>
    /// Стыковка (GDD §26): в круге станции корабль уходит в док — из космоса, из прицелов и с пути метеоритов.
    /// Вылет — там же, где стыковались, стоя на месте и с защитой, как после появления (§25).
    /// </summary>
    public void Dock(IClientConnection connection, bool on)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player)) return;
        if (on == player.Docked)
        {
            SendHangar(player); // клиент мог не знать, что уже там
            return;
        }
        if (on)
        {
            if (player.IsDead) return;
            if (!AtStation(player))
            {
                connection.Send(new NoticeMsg(Protocol.TooFarNotice));
                return;
            }
            player.Docked = true;
            player.Ship.Vx = player.Ship.Vy = 0;
            player.FireHeld = false;
            player.TargetId = 0;
            player.SelectedLootId = 0;
            RemoveShip(player);
            _log.LogInformation("Player {Id} docked", player.Id);
        }
        else
        {
            player.Docked = false;
            player.ResetInputs();
            player.ProtectedUntilTick = Tick + Balance.Rules.ProtectionTicks;
            _ships[player.Id] = player;
            _log.LogInformation("Player {Id} undocked", player.Id);
        }
        SendHangar(player);
    }

    /// <summary>Купить в доке корпус или пушку (GDD §26, §30). Купленное сразу ставится на корабль.</summary>
    public void Buy(IClientConnection connection, string? kind, string? id)
    {
        if (id is null || !_byConnection.TryGetValue(connection.Id, out var player) || !player.Docked) return;
        var shop = Balance.Shop;
        var (price, owned) = kind switch
        {
            Protocol.HullItem when Hulls.ContainsKey(id) => (shop.HullPrice(id), player.OwnsHull(id)),
            Protocol.WeaponItem when Balance.Weapons.ContainsKey(id) => (shop.WeaponPrice(id), player.OwnsWeapon(id)),
            _ => (null, false),
        };
        if (owned || price is not { } cost) return;
        if (player.Credits < cost)
        {
            connection.Send(new NoticeMsg(Protocol.NoCreditsNotice));
            return;
        }

        player.Credits -= cost;
        if (kind == Protocol.HullItem)
        {
            player.Hulls.Add(id);
            ChangeHull(player, id);
        }
        else
        {
            player.Weapons.Add(id);
            player.WeaponId = id;
        }
        SendCargo(player); // кредиты, а у корпуса — ещё и ёмкость трюма
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} bought {Kind} {Item} for {Credits} credits", player.Id, kind, id, cost);
    }

    /// <summary>Ремонт в доке: корпус и щит до полных, по repairPrice из shop.json за единицу корпуса.</summary>
    public void Repair(IClientConnection connection)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player) || !player.Docked) return;
        var hull = player.Hull(Hulls);
        var maxHp = player.MaxHp(hull);
        var maxShield = player.MaxShield(hull);
        if (player.Hp >= maxHp && player.Shield >= maxShield) return;
        var cost = Balance.Shop.RepairCost(maxHp - player.Hp);
        if (player.Credits < cost)
        {
            connection.Send(new NoticeMsg(Protocol.NoCreditsNotice));
            return;
        }
        player.Credits -= cost;
        player.Hp = maxHp;
        player.Shield = maxShield;
        SendCargo(player);
        SendHangar(player);
        if (cost > 0) Save(player);
    }

    private bool AtStation(Player player)
    {
        var dx = player.Ship.X - SimConfig.StationX;
        var dy = player.Ship.Y - SimConfig.StationY;
        var range = Balance.Loot.StationRange;
        return dx * dx + dy * dy <= range * range;
    }

    private IEnumerable<Player> DockedPlayers() => _players.Values.Where(p => p.Docked);

    /// <summary>Ангар — личное дело пилота, как и трюм.</summary>
    private void SendHangar(Player player)
    {
        if (player.Connection is null) return;
        var hull = player.Hull(Hulls);
        player.Connection.Send(new HangarMsg(
            player.HullId,
            player.WeaponId,
            player.IsGuest ? [.. Hulls.Keys] : [.. player.Hulls.Where(Hulls.ContainsKey).Order(StringComparer.Ordinal)],
            player.IsGuest ? [.. Balance.Weapons.Keys] : [.. player.Weapons.Where(Balance.Weapons.ContainsKey).Order(StringComparer.Ordinal)],
            player.Docked,
            (int)Math.Ceiling(player.Hp),
            (int)Math.Ceiling(player.MaxHp(hull))));
    }

    /// <summary>
    /// Состояние пилота — в аккаунт (GDD §62). Здесь только память: на диск его запишет AccountStore,
    /// поэтому звать можно после каждого изменения, не думая о цене.
    /// </summary>
    private void Save(Player player)
    {
        if (_accounts is null || player.AccountId is null) return;
        _accounts.Save(player.AccountId, new AccountProfile(
            player.Credits,
            player.HullId,
            player.WeaponId,
            [.. player.Hulls.Order(StringComparer.Ordinal)],
            [.. player.Weapons.Order(StringComparer.Ordinal)],
            new Dictionary<string, int>(player.Cargo.Items)));
    }

    /// <summary>Трюм — личное дело игрока: снапшот один на всех, места для него там нет.</summary>
    private void SendCargo(Player player)
    {
        if (player.Connection is null) return;
        var loot = Balance.Loot;
        player.Connection.Send(new CargoMsg(
            player.Cargo.Used(loot),
            player.Hull(Hulls).Cargo,
            player.Cargo.Items,
            player.Credits));
    }

    /// <summary>«Недостаточно места в трюме» (GDD §21) — не чаще раза в FullHoldSeconds, иначе это спам.</summary>
    private void WarnCargoFull(Player player)
    {
        if (Tick < player.CargoFullUntilTick) return;
        player.CargoFullUntilTick = Tick + Balance.Loot.FullHoldTicks;
        player.Connection?.Send(new NoticeMsg(Protocol.CargoFullNotice));
    }

    /// <summary>Предмет исчез (протух или его забрали): выбор, указывающий в пустоту, гасим.</summary>
    private void ClearMissingLootTargets()
    {
        foreach (var player in _players.Values)
        {
            if (player.SelectedLootId != 0 && !_loot.Has(player.SelectedLootId)) player.SelectedLootId = 0;
        }
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
        // Метеориты — не в ростере: их десятки за минуту, а ростер рассылается целиком при каждом изменении.
        // Пилоты в доке остаются в списке: они в системе, просто не в космосе.
        var list = _ships.Values
            .Where(s => s is not Meteor)
            .Concat(DockedPlayers())
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
