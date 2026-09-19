using System.Globalization;
using System.Text;
using Sro.Server.Accounts;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Логика одной звёздной системы: игроки, сессии, NPC, шаг симуляции, бой, снапшоты. Вызывается только из потока тика
/// (<see cref="GalaxyHost"/>), своих потоков и таймеров не имеет — поэтому тестируется напрямую.
/// Соседние системы и переходы между ними — забота <see cref="Galaxy"/>; без неё комната живёт одна, как до M7.
/// </summary>
public sealed partial class Room
{
    public const int MaxNameLength = 16;
    public const string DefaultName = "Рейнджер";

    /// <summary>Столько корабль без связи ждёт возвращения игрока, потом удаляется.</summary>
    public const int ReconnectGraceTicks = 60 * SimConfig.TickRate;

    /// <summary>Код закрытия WebSocket: к кораблю подключилось новое соединение с той же сессией.</summary>
    public const int ReplacedCloseCode = 4001;

    /// <summary>Запас радара: корабль на краю не мерцает, то появляясь, то пропадая с каждым тиком.</summary>
    public const double RadarMargin = 150;

    /// <summary>Подготовка прыжка сбивается, если корабль отошёл от врат дальше gateRange × столько.</summary>
    public const double GateSlack = 1.2;

    private const int MinTokenLength = 16;
    private const int MaxTokenLength = 64;

    private readonly ILogger _log;
    private readonly Random _jitter;
    private readonly Random _ai;
    private readonly Battle _battle;
    private readonly LootSystem _loot;
    private readonly MeteorSystem _meteors;
    private readonly MissileSystem _missiles;
    private readonly AccountStore? _accounts;
    private readonly IRoomHost? _host;
    private readonly Func<int> _newId;
    private readonly Dictionary<int, Player> _players = [];
    private readonly Dictionary<int, Player> _byConnection = [];
    private readonly Dictionary<string, Player> _byToken = new(StringComparer.Ordinal);
    /// <summary>Все корабли системы — игроки и NPC; здесь ищется цель.</summary>
    private readonly Dictionary<int, ShipEntity> _ships = [];
    private readonly List<Drone> _drones = [];
    private readonly List<Pirate> _pirates = [];
    private readonly List<Trader> _traders = [];
    /// <summary>Торговцы, которые долетели или погибли: убираются после шага.</summary>
    private readonly List<Trader> _goneTraders = [];
    private readonly List<ShipDto> _shipDtos = [];
    private readonly List<ShotDto> _shots = [];
    private readonly List<KillDto> _kills = [];
    private readonly List<Player> _expired = [];
    private readonly MoveInput[] _steps = new MoveInput[InputBuffer.MaxBudget];
    private readonly List<Player> _jumping = [];
    /// <summary>Погибшие в этот тик пилоты с грузом: их трюм высыпан.</summary>
    private readonly List<Player> _spilled = [];
    /// <summary>По кому попали в этот тик: их подготовка прыжка сбита.</summary>
    private readonly HashSet<int> _hit = [];
    /// <summary>Налётчики, которые ушли из системы или погибли: убираются после шага.</summary>
    private readonly List<Pirate> _gonePirates = [];
    private int _nextId;
    private int _raidCount;
    /// <summary>Раньше этого тика новый налёт не прилетит.</summary>
    private long _nextRaidTick;
    /// <summary>Налёты этого баланса — сравниваются по тексту: новый Balance при каждой правке любого файла.</summary>
    private string _raidsJson = "";
    /// <summary>Раньше этого тика новый торговец не появится.</summary>
    private long _nextTraderTick;
    /// <summary>Торговцы этого баланса вместе с их типом — тоже по тексту.</summary>
    private string _tradersJson = "";
    /// <summary>Кто напал на торговца — id корабля и до какого тика рейнджеры идут на него (<see cref="OffenderTicks"/>).</summary>
    private readonly Dictionary<int, long> _offenders = [];
    private readonly List<int> _forgiven = [];

    /// <summary>Столько рейнджеры помнят нападение на торговца: потом обидчик для них снова никто.</summary>
    public const int OffenderTicks = 30 * SimConfig.TickRate;

    /// <param name="roll">Случайное число из [0, 1) для бросков попадания; тесты подставляют своё.</param>
    /// <param name="jitter">Разброс точки появления.</param>
    /// <param name="ai">Случайность ИИ пиратов (точки патруля) — отдельно, чтобы пираты не сдвигали разброс спауна.</param>
    /// <param name="loot">Случайность дропа — тоже отдельно: иначе добыча сдвигала бы разброс спауна.</param>
    /// <param name="meteors">Случайность метеоритов: размер, трасса, скорость, интервал.</param>
    /// <param name="accounts">Аккаунты пилотов; null — сохранять некуда (тесты без аккаунтов).</param>
    /// <param name="host">Галактика: соседние системы, общий ростер имён; null — комната одна.</param>
    /// <param name="nextId">Общий счётчик id галактики: игрок не меняет id, перелетая из системы в систему.</param>
    /// <param name="orbitEpoch">Орбитальное время в тик 0, секунды; null — сейчас по unix-часам (<see cref="Now"/>).</param>
    public Room(
        Balance balance,
        ILogger log,
        Func<double>? roll = null,
        Random? jitter = null,
        Random? ai = null,
        Random? loot = null,
        Random? meteors = null,
        AccountStore? accounts = null,
        IRoomHost? host = null,
        Func<int>? nextId = null,
        double? orbitEpoch = null)
    {
        Balance = balance;
        OrbitEpoch = orbitEpoch ?? Now();
        _log = log;
        _host = host;
        _newId = nextId ?? (() => ++_nextId);
        _jitter = jitter ?? Random.Shared;
        _ai = ai ?? Random.Shared;
        _battle = new Battle(roll ?? Random.Shared.NextDouble, log);
        _loot = new LootSystem(_newId, loot ?? Random.Shared, log);
        _loot.SetContainers(balance.Loot.ContainerList);
        _meteors = new MeteorSystem(_newId, meteors ?? Random.Shared);
        _missiles = new MissileSystem(_newId);
        _accounts = accounts;
        SpawnDrones();
        SpawnPirates();
        StartRaids();
        StartTraders();
    }

    public long Tick { get; private set; }

    /// <summary>
    /// Орбитальное время в тик 0: станция и планеты ходят по unix-часам, поэтому перезапуск сервера их не сдвигает.
    /// Клиент получает его в системе и считает орбиты той же формулой от тика снапшота.
    /// </summary>
    public double OrbitEpoch { get; }

    /// <summary>Орбитальное время сейчас, секунды.</summary>
    public double OrbitSeconds => OrbitEpoch + Tick * SimConfig.Dt;

    /// <summary>Unix-время в секундах — начало орбитального отсчёта новой комнаты.</summary>
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>Где сейчас станция (даже в системе без станции — там это просто центр).</summary>
    public (double X, double Y) StationPosition => Balance.StationPath.At(OrbitSeconds);

    /// <summary>Id системы этой комнаты.</summary>
    public string SystemId => Balance.System;
    public Balance Balance { get; private set; }
    public IReadOnlyDictionary<string, HullParams> Hulls => Balance.Hulls;

    /// <summary>Число игроков; NPC не считаются.</summary>
    public int Count => _players.Count;

    /// <summary>Игроков на связи.</summary>
    public int OnlineCount => _byConnection.Count;

    /// <summary>Кораблей в космосе, включая NPC и метеориты.</summary>
    public int ShipCount => _ships.Count;

    /// <summary>Здесь есть корабль с таким ключом возврата — ждёт после обрыва связи или летает с другого устройства.</summary>
    public bool HasToken(string token) => _byToken.ContainsKey(token);

    /// <summary>Здесь ждёт гость с такой сессией.</summary>
    public bool HasGuest(string? token) => IsValidToken(token) && _byToken.TryGetValue(token!, out var p) && p.IsGuest;

    /// <summary>Корабль по id — игрок или NPC.</summary>
    public ShipEntity? Entity(int id) => _ships.GetValueOrDefault(id);

    /// <summary>Игрок по id — в том числе в доке, когда его корабля в космосе нет.</summary>
    public Player? Pilot(int id) => _players.GetValueOrDefault(id);

    /// <summary>Игрок этого соединения.</summary>
    public Player? PlayerOf(IClientConnection connection) => _byConnection.GetValueOrDefault(connection.Id);

    /// <summary>Все игроки системы, в том числе в доке и без связи.</summary>
    public IEnumerable<Player> Pilots => _players.Values;

    /// <summary>Метеориты в полёте.</summary>
    public IReadOnlyList<Meteor> Meteors => _meteors.Alive;

    /// <summary>Ракеты в полёте.</summary>
    public IReadOnlyList<MissileSystem.Missile> Missiles => _missiles.Alive;

    /// <summary>Торговцы в системе.</summary>
    public IReadOnlyList<Trader> Traders => _traders;

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
            if (weaponId is not null) Refit(player, player.Fit.With("w0", weaponId));
            Resume(player, connection);
            return;
        }

        player = new Player(
            _newId(),
            token,
            UniqueName(SanitizeName(name), null),
            hullId ?? SimConfig.DefaultHull,
            weaponId ?? SimConfig.DefaultWeapon)
        {
            Credits = Balance.Shop.StartCredits,
            Home = SystemId,
        };
        Refit(player, player.Fit);
        player.Fuel = Tank(player);
        // Гость — это тесты, смоук-скрипты и боты: обучения у него нет, доска заданий есть.
        player.Missions.Seed = Random.Shared.Next();
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
        player = new Player(_newId(), accountId, UniqueName(SanitizeName(name), null), SimConfig.DefaultHull, SimConfig.DefaultWeapon, accountId);
        if (profile is null)
        {
            player.Credits = Balance.Shop.StartCredits; // GDD §54: новый пилот получает стартовый капитал
        }
        else
        {
            player.Credits = profile.Credits;
            // Корпус, пушку или модуль могли убрать из баланса, пока пилот отсутствовал, — такие просто пропадают.
            foreach (var id in profile.Hulls) if (Hulls.ContainsKey(id)) player.Hulls.Add(id);
            if (player.Hulls.Contains(profile.Hull) && Hulls.ContainsKey(profile.Hull)) player.HullId = profile.Hull;
            var (fit, storage) = profile.Fit is { } saved ? (saved, profile.Storage) : MigrateFit(profile);
            foreach (var (id, count) in storage ?? new Dictionary<string, int>())
            {
                if (count > 0 && IsItem(id)) player.Store(id, count);
            }
            player.Fit = fit with { Weapons = [.. fit.Weapons ?? []] };
            foreach (var (item, count) in profile.Cargo) if (count > 0) player.Cargo.Add(item, count);
        }
        Refit(player, player.Fit);
        // Профиль старше M7 — бак полный. Дом — система, где пилот появился: её выбрала галактика по профилю.
        player.Fuel = Math.Clamp(profile?.Fuel ?? int.MaxValue, 0, Tank(player));
        player.Home = SystemId;
        // Обучение — только новому пилоту (GDD §54): профиль старше M8 считается прошедшим его.
        player.Missions.Tutorial = profile is null ? 0 : profile.Tutorial ?? MissionLog.Finished;
        player.Missions.Active = profile?.Mission;
        player.Missions.Seed = profile?.MissionSeed ?? Random.Shared.Next();
        player.Cargo.Reserved = Reserve(player.Missions.Active);
        Enter(player, connection);
        if (profile is null || profile.Fit is null) Save(player);
    }

    /// <summary>
    /// Профиль старше M9: одна пушка и список купленных. Активная встаёт в первый слот, остальные купленные — на склад,
    /// модули — стартовые.
    /// </summary>
    private static (ShipFit Fit, IReadOnlyDictionary<string, int> Storage) MigrateFit(AccountProfile profile)
    {
        var active = string.IsNullOrEmpty(profile.Weapon) ? SimConfig.DefaultWeapon : profile.Weapon;
        var storage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in profile.Weapons ?? []) if (id != active) storage[id] = 1;
        return (Fitting.Starter.With("w0", active), storage);
    }

    /// <summary>Такая пушка или модуль есть в балансе.</summary>
    private bool IsItem(string id) => Balance.Weapons.ContainsKey(id) || Balance.Modules?.ContainsKey(id) == true;

    /// <summary>
    /// Новый корабль в системе: у станции, с защитой (GDD §25). Новичок начинает в доке: первый шаг обучения —
    /// вылететь с базы (§54).
    /// </summary>
    private void Enter(Player player, IClientConnection connection)
    {
        SpawnHere(player);
        player.Attach(connection);
        _players[player.Id] = player;
        if (Balance.HasStation && Balance.Missions.Step(player.Missions.Tutorial)?.Id == MissionRules.UndockStep)
        {
            player.Docked = true;
            player.DockOffset = Balance.StationPath.ToLocal(OrbitSeconds, player.Ship.X, player.Ship.Y);
        }
        else
        {
            _ships[player.Id] = player;
        }
        _byConnection[connection.Id] = player;
        if (player.Token is not null) _byToken[player.Token] = player;
        connection.Send(Welcome(player, resumed: false));
        BroadcastPlayers();
        SendCargo(player); // трюм пуст, но клиенту нужна ёмкость корпуса
        SendHangar(player);
        SendMissions(player);
        _log.LogInformation("Player {Id} '{Name}' joined as {Hull}, online {Count}", player.Id, player.Name, player.HullId, OnlineCount);
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
        player.View.Reset();
        _byConnection[connection.Id] = player;
        connection.Send(Welcome(player, resumed: true));
        BroadcastPlayers();
        SendCargo(player); // иначе вернувшийся видел бы пустой трюм до первого подбора
        SendHangar(player); // в том числе — что корабль всё ещё в доке
        SendMissions(player);
        _log.LogInformation("Player {Id} '{Name}' resumed, online {Count}", player.Id, player.Name, OnlineCount);
    }

    /// <summary>Соединение закрылось. Корабль остаётся ждать игрока, если у того есть сессия.</summary>
    public void Disconnect(IClientConnection connection)
    {
        // Соединение, которое уже вытеснили новым, здесь не найдётся и корабль не отцепит.
        if (!_byConnection.Remove(connection.Id, out var player)) return;
        if (player.Token is null)
        {
            Remove(player);
            _log.LogInformation("Player {Id} left, online {Count}", player.Id, OnlineCount);
        }
        else
        {
            player.Detach(Tick);
            _log.LogInformation("Player {Id} lost connection, online {Count}", player.Id, OnlineCount);
        }
        BroadcastPlayers();
    }

    /// <summary>
    /// Корабль прилетел из другой системы (гиперпрыжок) или вернулся домой после гибели. Всё своё — трюм, кредиты,
    /// ангар, связь — у него с собой; клиент получает новый welcome и строит систему заново.
    /// </summary>
    /// <param name="arrival">Точка у врат; null — появление у станции целым, как после гибели.</param>
    public void Admit(Player player, (double X, double Y)? arrival)
    {
        player.Docked = false;
        player.JumpTo = null;
        player.TargetId = 0;
        player.FireHeld = false;
        player.SelectedLootId = 0;
        if (arrival is { } at)
        {
            // Нос — к центру системы: врата у края, лететь от них — внутрь.
            player.Ship = new ShipState { X = at.X, Y = at.Y, Rot = Math.Atan2(-at.X, at.Y) };
            player.ProtectedUntilTick = Tick + Balance.Rules.ProtectionTicks; // у врат могут поджидать (GDD §25)
        }
        else
        {
            SpawnHere(player);
        }
        player.ResetInputs();
        player.View.Reset();
        _players[player.Id] = player;
        _ships[player.Id] = player;
        if (player.Token is not null) _byToken[player.Token] = player;
        if (player.Connection is { } connection)
        {
            _byConnection[connection.Id] = player;
            connection.Send(Welcome(player, resumed: true));
            SendCargo(player);
            SendHangar(player);
        }
        // Доска — уже этой системы. Прыжок — шаг обучения; засчитывается после welcome, чтобы строка в ленте
        // пришла уже в новую систему.
        if (arrival is null || !Advance(player, MissionRules.JumpStep)) SendMissions(player);
        BroadcastPlayers();
        _log.LogInformation("Player {Id} '{Name}' arrived in {System}", player.Id, player.Name, SystemId);
    }

    /// <summary>
    /// Корабль уходит в другую систему: из комнаты он исчезает целиком, но не сохраняется и связь не теряет.
    /// Ростер отсюда разошлёт галактика, когда корабль уже будет в новой системе: иначе в «онлайн» его бы не посчитали.
    /// </summary>
    public void Release(Player player)
    {
        if (!_players.Remove(player.Id)) return;
        if (player.Connection is { } connection) _byConnection.Remove(connection.Id);
        if (player.Token is not null) _byToken.Remove(player.Token);
        player.JumpTo = null;
        RemoveShip(player);
    }

    /// <summary>
    /// Гиперпрыжок (GDD §5): у врат в систему to, с топливом на маршрут — через jumpSeconds корабль уйдёт.
    /// to = null — отменить подготовку.
    /// </summary>
    public void Jump(IClientConnection connection, string? to)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player)) return;
        if (to is null)
        {
            if (player.JumpTo is not null) CancelJump(player, notify: false);
            return;
        }
        if (player.IsDead || player.Docked || player.JumpTo == to || _host is null) return;
        var galaxy = Balance.Galaxy;
        if (Balance.SystemDef.GateTo(to) is not { } gate || galaxy.JumpCost(SystemId, to) is not { } cost) return;
        if (!NearGate(player, gate, galaxy.GateRange))
        {
            connection.Send(new NoticeMsg(Protocol.GateFarNotice));
            return;
        }
        if (player.Fuel < cost)
        {
            connection.Send(new NoticeMsg(Protocol.NoFuelNotice));
            return;
        }
        player.JumpTo = to;
        player.JumpAtTick = Tick + galaxy.JumpTicks;
        _log.LogInformation("Player {Id} charges a jump {From} → {To}", player.Id, SystemId, to);
    }

    /// <summary>Заправка в доке до полного бака (GDD §6, §26) по fuelPrice из shop.json.</summary>
    public void Refuel(IClientConnection connection)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player) || !player.Docked) return;
        var missing = Tank(player) - player.Fuel;
        if (missing <= 0) return;
        var cost = Balance.Shop.FuelCost(missing);
        if (player.Credits < cost)
        {
            connection.Send(new NoticeMsg(Protocol.NoCreditsNotice));
            return;
        }
        player.Credits -= cost;
        player.Fuel += missing;
        SendCargo(player);
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} refuelled {Fuel} for {Credits} credits", player.Id, missing, cost);
    }

    private static bool NearGate(Player player, GateDef gate, double range)
    {
        var dx = player.Ship.X - gate.X;
        var dy = player.Ship.Y - gate.Y;
        return dx * dx + dy * dy <= range * range;
    }

    private void CancelJump(Player player, bool notify)
    {
        player.JumpTo = null;
        player.JumpAtTick = 0;
        if (notify) player.Connection?.Send(new NoticeMsg(Protocol.JumpCancelledNotice));
    }

    /// <summary>
    /// Под огнём в портал не уйти: попадание сбивает подготовку прыжка — у пилота, у налётчика и у торговца.
    /// Пилот начинает прыжок заново сам; NPC у врат начнёт его снова в следующий тик, и новый выстрел снова собьёт.
    /// </summary>
    private void InterruptJumps()
    {
        _hit.Clear();
        foreach (var shot in _shots) if (shot.Hit) _hit.Add(shot.To);
        foreach (var id in _hit)
        {
            switch (_ships.GetValueOrDefault(id))
            {
                case Player { JumpTo: not null } player:
                    CancelJump(player, notify: false);
                    player.Connection?.Send(new NoticeMsg(Protocol.JumpHitNotice));
                    break;
                case Pirate { LeaveAtTick: > 0 } pirate:
                    pirate.LeaveAtTick = 0;
                    break;
                case Trader { LeaveAtTick: > 0 } trader:
                    trader.LeaveAtTick = 0;
                    break;
            }
        }
    }

    /// <summary>
    /// Подготовка прыжка: сбивается, если корабль погиб, ушёл в док, потерял связь, отошёл от врат или по нему попали;
    /// по готовности топливо списывается, и галактика переводит корабль в соседнюю систему.
    /// </summary>
    private void StepJumps()
    {
        _jumping.Clear();
        foreach (var player in _players.Values) if (player.JumpTo is not null) _jumping.Add(player);
        var galaxy = Balance.Galaxy;
        foreach (var player in _jumping)
        {
            var to = player.JumpTo!;
            var gate = Balance.SystemDef.GateTo(to);
            var cost = galaxy.JumpCost(SystemId, to);
            if (player.IsDead || player.Docked || player.Connection is null || gate is null || cost is null ||
                !NearGate(player, gate, galaxy.GateRange * GateSlack))
            {
                CancelJump(player, notify: true);
                continue;
            }
            if (Tick < player.JumpAtTick) continue;
            if (player.Fuel < cost)
            {
                CancelJump(player, notify: false);
                player.Connection.Send(new NoticeMsg(Protocol.NoFuelNotice));
                continue;
            }
            player.Fuel -= cost.Value;
            player.JumpTo = null;
            player.JumpAtTick = 0;
            Save(player);
            _log.LogInformation("Player {Id} jumps {From} → {To}, fuel left {Fuel}", player.Id, SystemId, to, player.Fuel);
            _host!.Depart(this, player, to, jump: true);
        }
    }

    /// <summary>Бак корабля: модуль бака, без modules.json — корпус (hulls.json fuel).</summary>
    private int Tank(Player player) => (int)Math.Floor(player.Effective(Balance).Fuel);

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

    /// <summary>
    /// Поставить в слот пушку или модуль со склада (GDD §20) или снять на склад (id = null). Пилоту с аккаунтом — только
    /// в доке и только своё; гостю — где угодно и что угодно. Снятое из слота уходит на склад. Слот, класс и энергию
    /// генератора (§18) проверяют правила <see cref="Fitting.CanInstall"/>.
    /// </summary>
    public void Fit(IClientConnection connection, string? slot, string? id)
    {
        if (slot is null || !Fitting.IsSlot(slot) || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        if (id is not null && !IsItem(id)) return;
        var old = player.Fit.Get(slot);
        if (old == id) return;
        if (!player.IsGuest)
        {
            if (!player.Docked) return;
            if (id is not null && player.Storage.GetValueOrDefault(id) <= 0) return;
        }
        if (Fitting.CanInstall(player.Hull(Hulls), player.Fit, slot, id, Balance.Weapons, Balance.Modules) is { } problem)
        {
            connection.Send(new NoticeMsg(FitNotice(problem)));
            return;
        }
        if (!player.IsGuest)
        {
            if (id is not null) player.Unstore(id);
            if (old is not null) player.Store(old);
        }
        Refit(player, player.Fit.With(slot, id));
        SendCargo(player);
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} fitted {Item} into {Slot}", player.Id, id ?? "nothing", slot);
    }

    /// <summary>Поставить пушку в первый слот одной командой — гостю любую, пилоту — со склада.</summary>
    public void SetWeapon(IClientConnection connection, string? weaponId)
    {
        if (weaponId is null || !Balance.Weapons.ContainsKey(weaponId)) return;
        Fit(connection, "w0", weaponId);
    }

    private static string FitNotice(string problem) => problem switch
    {
        FitProblem.Power => Protocol.NoPowerNotice,
        FitProblem.Class => Protocol.BadClassNotice,
        _ => Protocol.BadSlotNotice,
    };

    /// <summary>
    /// Новое оснащение (или то же после смены корпуса): приводится к корпусу, снятое — на склад,
    /// корпус и щит сохраняют доли, топливо — не больше бака.
    /// </summary>
    private void Refit(Player player, ShipFit fit)
    {
        var from = player.Effective(Balance);
        var removed = new List<string>();
        player.Fit = Fitting.Refit(player.Hull(Hulls), fit, Balance.Weapons, Balance.Modules, removed);
        if (!player.IsGuest) foreach (var id in removed) player.Store(id);
        player.Rescale(from, player.Effective(Balance));
        player.Fuel = Math.Min(player.Fuel, Tank(player));
    }

    /// <summary>Продать со склада пушку или модуль — за долю цены (shop.json sellShare).</summary>
    public void SellItem(IClientConnection connection, string? id)
    {
        if (id is null || !_byConnection.TryGetValue(connection.Id, out var player) || player.IsGuest || !player.Docked) return;
        if (!player.Unstore(id)) return;
        var credits = Balance.Shop.SellPrice(id);
        player.Credits += credits;
        SendCargo(player);
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} sold {Item} for {Credits} credits", player.Id, id, credits);
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

    public void SetPvp(IClientConnection connection, bool on)
    {
        if (_byConnection.TryGetValue(connection.Id, out var player)) player.PvpOn = on;
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
        // Максимумы пилотов — по старому балансу, пока он ещё текущий: доли корпуса и щита считаются от них.
        var pilots = _players.Values.Select(p => (Player: p, From: p.Effective(old))).ToList();
        foreach (var ship in _ships.Values)
        {
            // Пилоты — ниже; у пирата и торговца корпус и максимумы — от типа; у метеорита корпуса нет.
            if (ship is not Drone) continue;
            var from = ship.Hull(old.Hulls);
            var hullId = balance.Hulls.ContainsKey(ship.HullId) ? ship.HullId : SimConfig.DefaultHull;
            ship.ChangeHull(from, hullId, balance.Hulls[hullId]);
        }
        Balance = balance;
        // Корабли в доке — не в космосе, но корпус и оснащение у них те же, что и у всех.
        foreach (var (player, from) in pilots)
        {
            if (!Hulls.ContainsKey(player.HullId)) player.HullId = SimConfig.DefaultHull;
            // Предметы, которых больше нет в балансе, пропадают и со склада.
            foreach (var id in player.Storage.Keys.Where(id => !IsItem(id)).ToList()) player.Storage.Remove(id);
            var removed = new List<string>();
            player.Fit = Fitting.Refit(player.Hull(Hulls), player.Fit, balance.Weapons, balance.Modules, removed);
            if (!player.IsGuest) foreach (var id in removed) player.Store(id);
            player.Rescale(from, player.Effective(balance));
            player.Fuel = Math.Min(player.Fuel, Tank(player));
        }

        var raidsJson = System.Text.Json.JsonSerializer.Serialize(balance.Raids);
        var lairsSame = old.Npc.SpawnList.SequenceEqual(balance.Npc.SpawnList);
        var raidsSame = raidsJson == _raidsJson;
        // Список логов тот же — значит, все их типы есть и в новом файле; налётчики — тоже, если налёты те же.
        foreach (var pirate in _pirates.ToList())
        {
            // Вторжение идёт своим чередом: его пираты остаются, пока их тип есть в балансе.
            if (pirate.IsInvader ? balance.Npc.TypeMap.ContainsKey(pirate.Spawn.Type) : pirate.IsRaider ? raidsSame : lairsSame)
            {
                if (balance.Npc.TypeMap.TryGetValue(pirate.Spawn.Type, out var type)) pirate.Rebind(type, balance.Npc, old.Hulls, balance.Hulls);
                continue;
            }
            RemoveShip(pirate);
            _pirates.Remove(pirate);
        }
        if (!lairsSame) SpawnPirates();
        if (!raidsSame) StartRaids();
        if (TradersJson(balance) != _tradersJson)
        {
            foreach (var trader in _traders) RemoveShip(trader);
            _traders.Clear();
            StartTraders();
        }

        if (!old.Loot.ContainerList.SequenceEqual(balance.Loot.ContainerList)) _loot.SetContainers(balance.Loot.ContainerList);
        if (_loot.DropUnknown(balance.Loot)) ClearMissingLootTargets();

        var message = Protocol.Encode(new ConfigMsg(
            balance.Hulls, balance.Weapons, balance.Rules, balance.Npc, balance.Loot, balance.Meteors, balance.Shop,
            SystemInfo(), GalaxyInfo(balance.Galaxy), balance.Modules));
        foreach (var player in _players.Values)
        {
            player.Connection?.SendRaw(message);
            SendCargo(player); // объёмы предметов и ёмкость корпуса могли измениться
            SendHangar(player); // корпус, пушку или модуль могли убрать из баланса
            SendMissions(player); // шаги обучения и шаблоны доски
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
        // Дроны пришвартованы к станции: сначала их сносит вместе с ней, потом они летят сами.
        foreach (var drone in _drones) drone.Carry(DroneAnchor(drone.Spec, OrbitSeconds + SimConfig.Dt));
        foreach (var drone in _drones)
        {
            var input = drone.NextInput();
            if (!drone.IsDead) Movement.Step(ref drone.Ship, input, drone.Hull(Hulls), SimConfig.Dt);
        }
        // Уничтоженный пират не думает: иначе снова взял бы огонь, который Battle снял при смерти.
        ForgiveOffenders();
        foreach (var pirate in _pirates)
        {
            if (pirate.IsDead) continue;
            Scavenge(pirate);
            var before = pirate.TargetId;
            PirateBrain.Think(pirate, _ships, _pirates, Balance, Tick, _ai, _log, StationPosition, _offenders);
            if (pirate.Type.IsRanger && pirate.State == PirateState.Attack && pirate.TargetId != before &&
                _ships.GetValueOrDefault(pirate.TargetId) is Player { Connection: { } connection } && _offenders.ContainsKey(pirate.TargetId))
                connection.Send(new NoticeMsg(Protocol.RangersNotice));
            if (pirate.Gone) _gonePirates.Add(pirate);
            else Movement.Step(ref pirate.Ship, pirate.LastInput, pirate.Hull(Hulls), SimConfig.Dt);
        }
        if (Balance.Traders is { } traders)
        {
            var station = StationPosition;
            var heat = Balance.Sun?.BurnRadius ?? 0;
            foreach (var trader in _traders)
            {
                if (trader.IsDead) continue;
                // Напавший на торговца — обидчик: рейнджеры системы идут на него. А торговец зовёт на помощь.
                if (trader.LastAttackerId != 0)
                {
                    _offenders[trader.LastAttackerId] = Tick + OffenderTicks;
                    Distress(trader, trader.LastAttackerId, traders);
                }
                TraderBrain.Think(
                    trader, traders, Tick, Balance.Galaxy.JumpTicks, station, Balance.Loot.StationRange, heat, _ships, Balance.Npc.DropRange);
                if (trader.Gone) _goneTraders.Add(trader);
                else Movement.Step(ref trader.Ship, trader.LastInput, trader.Hull(Hulls), SimConfig.Dt);
            }
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
        Burn();
        // Таран — до боя: урон камня и урон пушек попадают в один свод смертей, и корабль, добитый
        // одновременно пиратом и метеоритом, погибает один раз.
        _meteors.Collide(_ships, Balance, Tick, _shots);
        // Ракеты — тоже до боя: их урон попадает в тот же свод смертей. Запущенные в этом тике полетят со следующего.
        _missiles.Step(Tick, _ships, Balance, _shots);
        _battle.Run(Tick, _ships, Balance, _shots, _kills, Spawn, CanAttack, Launch);
        // Задания — до уборки налётчиков: погибший должен ещё найтись среди кораблей.
        foreach (var kill in _kills)
        {
            if (_players.GetValueOrDefault(kill.By) is { } killer) Credit(killer, _ships.GetValueOrDefault(kill.Id));
        }
        NoteInvasionDamage();
        foreach (var meteor in _meteors.Shatter(_loot, Balance.Loot, Tick)) RemoveShip(meteor);
        StepSos();
        RemoveGonePirates();
        StepRaids();
        RemoveGoneTraders();
        StepTraders();
        // Дроп после боя: предмет должен пролежать хотя бы тик, иначе игрок вплотную к убитому
        // увидит «ничего не выпало», а трюм молча пополнится.
        _spilled.Clear();
        foreach (var kill in _kills) if (_players.GetValueOrDefault(kill.Id) is { Cargo.IsEmpty: false } spilled) _spilled.Add(spilled);
        _loot.DropFrom(_kills, _ships, Balance.Loot, Tick);
        foreach (var player in _spilled) LostCargo(player);
        // Подбор и продажа — по команде игрока, а не сами собой: см. Grab и Sell.
        if (_loot.Step(Tick, Balance.Loot)) ClearMissingLootTargets();
        InterruptJumps();
        StepJumps();

        // Дроны есть всегда: без игроков онлайн снапшот не нужен никому.
        if (_byConnection.Count > 0) SendSnapshot();
        _shots.Clear();
        _kills.Clear();
        _loot.ClearPicks();
    }

    /// <summary>
    /// Жар звезды: всё, что ближе burnRadius, теряет щит, потом корпус — у самого диска быстрее всего. Жжёт всех,
    /// даже защищённых после появления: это не нападение. Смерть уходит в общий свод боя с убийцей 0 — «звезда».
    /// </summary>
    private void Burn()
    {
        if (Balance.Sun is not { BurnDps: > 0 } sun) return;
        foreach (var ship in _ships.Values)
        {
            if (ship is Meteor || ship.IsDead) continue;
            var dps = sun.BurnAt(Math.Sqrt(ship.Ship.X * ship.Ship.X + ship.Ship.Y * ship.Ship.Y));
            if (dps <= 0) continue;
            Combat.ApplyDamage(ref ship.Hp, ref ship.Shield, dps * SimConfig.Dt);
            ship.LastDamageTick = Tick;
        }
    }

    /// <summary>Улетевшие метеориты исчезают, новые появляются по таймеру — только пока в системе кто-то есть.</summary>
    private void StepMeteors()
    {
        var rules = Balance.Meteors;
        foreach (var meteor in _meteors.Expired(rules, Tick)) RemoveShip(meteor);
        if (!_meteors.Due(rules, _byConnection.Count > 0)) return;
        // Укрытие станции камни обходят по всей дуге — станция тем временем идёт по орбите.
        var path = Balance.StationPath;
        var start = OrbitSeconds;
        Func<double, (double X, double Y)>? station = Balance.HasStation ? t => path.At(start + t) : null;
        var shelter = Balance.HasStation ? Balance.Npc.StationSafeRadius : 0;
        if (_meteors.Launch(rules, Balance.MeteorCore, Tick, station, shelter) is { } launched) _ships[launched.Id] = launched;
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
        var hull = player.Effective(Balance);
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
    /// Появление после уничтожения. Игрок возвращается к станции своего дома (GDD §24): если дом в другой системе,
    /// корабль туда переводит галактика, и появляется он уже там.
    /// </summary>
    private void Spawn(ShipEntity ship)
    {
        // Налётчик не возрождается: вместо него когда-нибудь прилетит новый налёт.
        if (ship is Pirate { IsRaider: true } raider)
        {
            _gonePirates.Add(raider);
            return;
        }
        // Торговец тоже: вместо него через срок появится новый.
        if (ship is Trader trader)
        {
            _goneTraders.Add(trader);
            return;
        }
        if (ship is Player { Home: { } home } player && home != SystemId && _host is not null)
        {
            _host.Depart(this, player, home, jump: false);
            return;
        }
        SpawnHere(ship);
    }

    /// <summary>
    /// Появление в этой системе: игрок — у станции, с защитой (GDD §25). Дрон — у своего дома, пират — в логове;
    /// NPC без защиты.
    /// </summary>
    private void SpawnHere(ShipEntity ship)
    {
        if (ship is Meteor) return; // разбитый камень не возвращается — его убирает MeteorSystem.Shatter
        var (x, y) = ship switch
        {
            Drone drone => drone.SpawnPoint,
            Pirate pirate => pirate.SpawnPoint,
            _ => SpawnPoint(),
        };
        ship.Ship = new ShipState { X = x, Y = y };
        ship.Revive(ship.Effective(Balance), ship is Player ? Tick + Balance.Rules.ProtectionTicks : 0);
        if (ship is Pirate p) p.ResetAi();
    }

    /// <summary>
    /// Случайная точка в круге SpawnJitter вокруг спауна — корабли не появляются друг в друге. Спаун — у станции
    /// с внешней стороны орбиты, прочь от звезды.
    /// </summary>
    private (double X, double Y) SpawnPoint()
    {
        var radius = Balance.Rules.SpawnJitter * Math.Sqrt(_jitter.NextDouble());
        var angle = _jitter.NextDouble() * 2 * Math.PI;
        var (x, y) = Balance.StationPath.ToWorld(OrbitSeconds, SimConfig.SpawnX, SimConfig.SpawnY);
        return (x + radius * Math.Cos(angle), y + radius * Math.Sin(angle));
    }

    /// <summary>Точка дрона в мире в момент seconds: в файле она задана в осях станции.</summary>
    private (double X, double Y) DroneAnchor(DroneSpec spec, double seconds) => Balance.StationPath.ToWorld(seconds, spec.X, spec.Y);

    private void SpawnDrones()
    {
        foreach (var spec in Balance.Rules.DroneList)
        {
            var drone = new Drone(_newId(), spec);
            drone.Carry(DroneAnchor(spec, OrbitSeconds));
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
                var pirate = new Pirate(_newId(), spawn, slot, type, npc);
                Spawn(pirate);
                _pirates.Add(pirate);
                _ships[pirate.Id] = pirate;
            }
        }
    }

    /// <summary>Первые налёты — сразу на местах: игрок не должен застать систему пустой.</summary>
    private void StartRaids()
    {
        _raidsJson = System.Text.Json.JsonSerializer.Serialize(Balance.Raids);
        if (Balance.Raids is not { } raids) return;
        for (var i = 0; i < raids.MaxGroups; i++) SpawnRaid(raids, onSite: true);
        _nextRaidTick = Tick + NextRaidWait(raids);
    }

    /// <summary>Новый налёт, когда групп меньше нормы и подошёл срок.</summary>
    private void StepRaids()
    {
        // Пока идёт вторжение, обычные налёты сюда не летят: у станции и так жарко.
        if (Balance.Raids is not { } raids || Tick < _nextRaidTick || InvasionId != 0) return;
        var active = _pirates.Where(p => p.IsRaider && !p.IsInvader).Select(p => p.RaidId).Distinct().Count();
        if (active >= raids.MaxGroups) return;
        SpawnRaid(raids, onSite: false);
        _nextRaidTick = Tick + NextRaidWait(raids);
        BroadcastPlayers();
    }

    /// <summary>Интервал из файла — среднее: налёты идут неровно, от половины до полутора.</summary>
    private long NextRaidWait(RaidRules raids) => (long)(raids.IntervalTicks * (0.5 + _ai.NextDouble()));

    /// <summary>
    /// Группа налётчиков: состав по весам, точка патруля — случайная. Прилетают из случайных врат (в пиратской системе —
    /// с базы) и летят к точке; onSite — сразу на точке и уже патрулируют, с частью срока позади.
    /// </summary>
    private void SpawnRaid(RaidRules raids, bool onSite)
    {
        var npc = Balance.Npc;
        if (raids.Pick(_ai.NextDouble()) is not { } group || !npc.TypeMap.TryGetValue(group.Type, out var type)) return;
        var gates = Balance.SystemDef.GateList;
        (double X, double Y, bool Gate)? exit = null;
        if (raids.Base is { } home) exit = (home.X, home.Y, false);
        else if (gates.Count > 0)
        {
            var gate = gates[_ai.Next(gates.Count)];
            exit = (gate.X, gate.Y, true);
        }
        if (exit is null) onSite = true; // уходить некуда — живут на точке, как в логове, пока не погибнут

        var (px, py) = RaidPoint();
        var spot = new NpcSpawn(group.Type, group.Level, px, py, group.Count);
        var patrolSeconds = raids.PatrolMinSeconds + (raids.PatrolMaxSeconds - raids.PatrolMinSeconds) * _ai.NextDouble();
        var patrolTicks = Combat.SecondsToTicks(patrolSeconds);
        var raidId = ++_raidCount;
        for (var slot = 0; slot < group.Count; slot++)
        {
            var pirate = new Pirate(_newId(), spot, slot, type, npc)
            {
                RaidId = raidId,
                ExitX = exit?.X ?? px,
                ExitY = exit?.Y ?? py,
                ExitIsGate = exit?.Gate ?? false,
                PatrolTicks = exit is null ? long.MaxValue / 4 : patrolTicks,
            };
            SpawnHere(pirate);
            if (onSite)
            {
                // Уже какое-то время здесь: группы, появившиеся вместе при старте, уйдут в разное время.
                pirate.PatrolUntilTick = Tick + (long)(pirate.PatrolTicks * _ai.NextDouble());
            }
            else
            {
                // Из врат или с базы — носом к точке патруля; к ней пират и летит (как «домой»).
                var (sx, sy) = pirate.SpawnPoint;
                var x = pirate.ExitX + sx - px;
                var y = pirate.ExitY + sy - py;
                pirate.Ship = new ShipState { X = x, Y = y, Rot = Math.Atan2(px - x, -(py - y)) };
                pirate.State = PirateState.Return;
            }
            _pirates.Add(pirate);
            _ships[pirate.Id] = pirate;
        }
        _log.LogInformation(
            "Raid {Raid}: {Count} × {Type} Ур.{Level} {From} → ({X:0}, {Y:0})",
            raidId, group.Count, group.Type, group.Level, onSite ? "on site" : exit!.Value.Gate ? "from a gate" : "from the base", px, py);
    }

    /// <summary>
    /// Случайная точка патруля налётчиков: вне жара звезды и так, чтобы укрытие станции, где бы она ни была на орбите,
    /// не накрыло круг патруля.
    /// </summary>
    private (double X, double Y) RaidPoint()
    {
        var npc = Balance.Npc;
        var min = (Balance.Sun?.BurnRadius ?? 0) + GalaxyRules.HeatMargin + npc.PatrolRadius;
        if (Balance.HasStation) min = Math.Max(min, Balance.StationPath.Radius + npc.StationSafeRadius + npc.PatrolRadius + RaidShelterMargin);
        var max = NpcRules.WorldLimit - npc.PatrolRadius;
        if (min > max) min = max * 0.8;
        // Равномерно по площади кольца.
        var radius = Math.Sqrt(min * min + (max * max - min * min) * _ai.NextDouble());
        var angle = _ai.NextDouble() * 2 * Math.PI;
        return (radius * Math.Cos(angle), radius * Math.Sin(angle));
    }

    /// <summary>Обидчики, которых рейнджеры уже забыли, и корабли, которых больше нет в системе.</summary>
    private void ForgiveOffenders()
    {
        if (_offenders.Count == 0) return;
        _forgiven.Clear();
        foreach (var (id, until) in _offenders) if (Tick >= until || !_ships.ContainsKey(id)) _forgiven.Add(id);
        foreach (var id in _forgiven) _offenders.Remove(id);
    }

    /// <summary>Торговцы этого баланса и их тип — текстом: правка любого другого файла их не пересоздаёт.</summary>
    private static string TradersJson(Balance balance) =>
        balance.Traders is not { } traders
            ? ""
            : System.Text.Json.JsonSerializer.Serialize(new { traders, type = balance.Npc.TypeMap.GetValueOrDefault(traders.Type) });

    /// <summary>Первые торговцы — сразу в пути: игрок не должен застать систему пустой.</summary>
    private void StartTraders()
    {
        _tradersJson = TradersJson(Balance);
        if (Balance.Traders is not { } traders) return;
        for (var i = 0; i < traders.Count; i++) SpawnTrader(traders, midway: true);
        _nextTraderTick = Tick + traders.RespawnTicks;
    }

    /// <summary>Новый торговец, когда их меньше нормы и подошёл срок.</summary>
    private void StepTraders()
    {
        if (Balance.Traders is not { } traders || Tick < _nextTraderTick || _traders.Count >= traders.Count) return;
        SpawnTrader(traders, midway: false);
        _nextTraderTick = Tick + traders.RespawnTicks;
        BroadcastPlayers();
    }

    /// <summary>
    /// Торговец на маршруте между станцией и вратами (или между двумя вратами): вылетает из одной точки и летит
    /// в другую. midway — уже где-то на полпути.
    /// </summary>
    private void SpawnTrader(TraderRules rules, bool midway)
    {
        if (!Balance.Npc.TypeMap.TryGetValue(rules.Type, out var type)) return;
        var stops = new List<(double X, double Y, bool Station)>();
        if (Balance.HasStation)
        {
            var (sx, sy) = Balance.StationPath.ToWorld(OrbitSeconds, SimConfig.SpawnX, SimConfig.SpawnY);
            stops.Add((sx, sy, true));
        }
        var arrival = Balance.Galaxy.ArrivalOffset;
        foreach (var gate in Balance.SystemDef.GateList)
        {
            // Из врат — чуть ближе к центру, как игрок после прыжка.
            var r = Math.Sqrt(gate.X * gate.X + gate.Y * gate.Y);
            var k = r > arrival ? (r - arrival) / r : 1;
            stops.Add((gate.X * k, gate.Y * k, false));
        }
        if (stops.Count < 2) return;

        var i = _ai.Next(stops.Count);
        var j = _ai.Next(stops.Count - 1);
        if (j >= i) j++;
        var (from, to) = (stops[i], stops[j]);
        var (x, y) = (from.X, from.Y);
        if (midway)
        {
            var t = 0.2 + 0.6 * _ai.NextDouble();
            (x, y) = (from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
            // Середина пути может прийтись на звезду — тогда торговец только что вылетел.
            var heat = (Balance.Sun?.BurnRadius ?? 0) + GalaxyRules.HeatMargin;
            if (x * x + y * y < heat * heat) (x, y) = (from.X, from.Y);
        }
        var trader = new Trader(_newId(), rules.Type, type, Balance.Npc)
        {
            ToStation = to.Station,
            DestX = to.X,
            DestY = to.Y,
        };
        trader.Ship = new ShipState { X = x, Y = y, Rot = Math.Atan2(to.X - x, -(to.Y - y)) };
        trader.Revive(trader.Effective(Balance), 0);
        _traders.Add(trader);
        _ships[trader.Id] = trader;
    }

    /// <summary>Сколько пират пролетит за грузом с патруля.</summary>
    private const double ScavengeRange = 800;

    /// <summary>
    /// Пират на патруле подбирает груз (GDD §31): подлетел — забрал, иначе выбирает ближайший, что влезет в трюм.
    /// В бою, в пути и налётчик, уже уходящий из системы, за грузом не летают.
    /// </summary>
    private void Scavenge(Pirate pirate)
    {
        var loot = Balance.Loot;
        var capacity = pirate.Hull(Hulls).Cargo;
        if (pirate.LootId != 0 && _loot.TryScavenge(pirate, pirate.LootId, loot, capacity))
        {
            _log.LogInformation("{Pirate} picked up loot, hold {Used}", pirate, pirate.Hold.Used(loot));
            pirate.LootId = 0;
            ClearMissingLootTargets(); // пилот мог пометить этот же груз
        }
        var drop = pirate.State == PirateState.Patrol && !pirate.Type.IsRanger
            ? _loot.ScavengeTarget(pirate, ScavengeRange, Balance.Npc.PatrolRadius + ScavengeRange, capacity, loot)
            : null;
        pirate.LootId = drop?.Id ?? 0;
        if (drop is not null) (pirate.LootX, pirate.LootY) = (drop.X, drop.Y);
    }

    /// <summary>Пилот погиб — трюм высыпан в космос (GDD §24): клиенту новый трюм и строка в ленту.</summary>
    private void LostCargo(Player player)
    {
        SendCargo(player);
        SendCollect(player);
        Save(player);
        player.Connection?.Send(new NoticeMsg(Protocol.CargoLostNotice));
    }

    /// <summary>По торговцу стреляют: SOS всей системе (если он ещё не зовёт), и тишина отсчитывается заново.</summary>
    private void Distress(Trader trader, int attackerId, TraderRules rules)
    {
        trader.Aggressors.Add(attackerId);
        trader.Helpers.Remove(attackerId); // напал сам — за помощь не платят
        var start = !trader.InDistress;
        trader.SosUntilTick = Tick + rules.SosQuietTicks;
        if (!start) return;
        trader.SosPingTick = Tick + SimConfig.TickRate;
        SendSos(trader, Protocol.SosOn);
        _log.LogInformation("{Trader} sends SOS: attacked by {Attacker}", trader, attackerId);
    }

    /// <summary>
    /// SOS в этот тик: пилоты, попавшие по нападавшим или добившие их, — помощники; кто зовёт давно — снова говорит,
    /// где он; по кому давно не стреляли — спасён, и помощникам платят.
    /// </summary>
    private void StepSos()
    {
        foreach (var trader in _traders)
        {
            if (!trader.InDistress) continue;
            foreach (var shot in _shots) if (shot.Hit) NoteHelp(trader, shot.From, shot.To);
            foreach (var kill in _kills) NoteHelp(trader, kill.By, kill.Id);
            if (trader.IsDead) continue; // гибель разошлёт уборка торговцев
            if (Tick >= trader.SosUntilTick) EndSos(trader, saved: true);
            else if (Tick >= trader.SosPingTick)
            {
                trader.SosPingTick = Tick + SimConfig.TickRate;
                SendSos(trader, Protocol.SosOn);
            }
        }
    }

    private void NoteHelp(Trader trader, int by, int target)
    {
        if (trader.Aggressors.Contains(target) && _players.ContainsKey(by) && !trader.Aggressors.Contains(by)) trader.Helpers.Add(by);
    }

    /// <summary>SOS снят: торговец отбился или долетел (платит помощникам), либо погиб.</summary>
    private void EndSos(Trader trader, bool saved)
    {
        var reward = saved ? Balance.Traders?.SosReward ?? 0 : 0;
        foreach (var player in _players.Values)
        {
            var paid = reward > 0 && trader.Helpers.Contains(player.Id) ? reward : 0;
            if (paid > 0)
            {
                player.Credits += paid;
                SendCargo(player);
                Save(player);
            }
            player.Connection?.Send(new SosMsg(
                trader.Id, trader.Name, trader.Ship.X, trader.Ship.Y, saved ? Protocol.SosSaved : Protocol.SosLost, paid));
        }
        _log.LogInformation(
            "{Trader} SOS is over: {Outcome}, {Helpers} helpers paid {Reward}", trader, saved ? "saved" : "lost", trader.Helpers.Count, reward);
        trader.SosUntilTick = 0;
        trader.Aggressors.Clear();
        trader.Helpers.Clear();
    }

    private void SendSos(Trader trader, string state)
    {
        var message = new SosMsg(trader.Id, trader.Name, trader.Ship.X, trader.Ship.Y, state);
        foreach (var player in _players.Values) player.Connection?.Send(message);
    }

    /// <summary>Долетевшие и погибшие торговцы покидают систему; через срок появятся новые.</summary>
    private void RemoveGoneTraders()
    {
        if (_goneTraders.Count == 0) return;
        foreach (var trader in _goneTraders)
        {
            if (!_traders.Remove(trader)) continue;
            // Долетел, пока звал на помощь, — спасён; сбит — погиб.
            if (trader.InDistress) EndSos(trader, saved: !trader.IsDead);
            RemoveShip(trader);
        }
        _goneTraders.Clear();
        if (Balance.Traders is { } rules) _nextTraderTick = Math.Max(_nextTraderTick, Tick + rules.RespawnTicks);
        BroadcastPlayers();
    }

    /// <summary>Налётчик не выбирает точку ближе этого к краю укрытия (с учётом круга патруля).</summary>
    private const double RaidShelterMargin = 200;

    /// <summary>Ушедшие и погибшие налётчики покидают систему; через срок прилетят новые.</summary>
    private void RemoveGonePirates()
    {
        if (_gonePirates.Count == 0) return;
        foreach (var pirate in _gonePirates)
        {
            if (!_pirates.Remove(pirate)) continue;
            RemoveShip(pirate);
        }
        _gonePirates.Clear();
        if (Balance.Raids is { } raids) _nextRaidTick = Math.Max(_nextRaidTick, Tick + NextRaidWait(raids));
        BroadcastPlayers();
    }

    /// <summary>
    /// Смена корпуса: оснащение переходит на новый, что не влезло по слотам, классу или энергии — на склад;
    /// в меньший бак больше не влезет.
    /// </summary>
    private void ChangeHull(Player player, string hullId)
    {
        if (player.HullId == hullId) return;
        var from = player.Effective(Balance);
        player.HullId = hullId;
        var removed = new List<string>();
        player.Fit = Fitting.Refit(player.Hull(Hulls), player.Fit, Balance.Weapons, Balance.Modules, removed);
        if (!player.IsGuest) foreach (var id in removed) player.Store(id);
        player.Rescale(from, player.Effective(Balance));
        player.Fuel = Math.Min(player.Fuel, Tank(player));
    }

    /// <summary>Ракета с корабля шуттера — в полёт; PvP уже проверил Battle.</summary>
    private void Launch(ShipEntity shooter, ShipEntity target, int slot, WeaponParams weapon) =>
        _missiles.Launch(shooter, target, slot, weapon, Tick);

    /// <summary>
    /// PvP по правилам системы (GDD §34): off — игроки друг друга не бьют; border — не бьют у станции (§26):
    /// ни по кораблю в укрытии, ни из укрытия; free — бьют везде. NPC, дроны и метеориты — всегда честная добыча.
    /// Пилот с выключенным PvP не бьёт никого мирного: ни игроков, ни торговцев, ни рейнджеров.
    /// </summary>
    private bool CanAttack(ShipEntity shooter, ShipEntity target)
    {
        if (shooter is Player { PvpOn: false } && target is Player or Trader or Pirate { Type.IsRanger: true }) return false;
        if (shooter is not Player || target is not Player) return true;
        if (_host?.SameParty(shooter.Id, target.Id) == true) return false; // по своим не стреляют (GDD §37)
        return Balance.SystemDef.Pvp switch
        {
            GalaxyRules.PvpFree => true,
            GalaxyRules.PvpBorder => !InCore(shooter) && !InCore(target),
            _ => false,
        };
    }

    /// <summary>В укрытии станции; в системе без станции укрытия нет.</summary>
    private bool InCore(ShipEntity ship)
    {
        if (!Balance.HasStation) return false;
        var (sx, sy) = StationPosition;
        var dx = ship.Ship.X - sx;
        var dy = ship.Ship.Y - sy;
        var radius = Balance.Npc.StationSafeRadius;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>
    /// Каждому игроку — своё: только то, что видит радар его корпуса (GDD §10), и только изменения
    /// с прошлого кадра (<see cref="SnapshotCodec"/>). Свой корабль — всегда: в нём ack для сверки предсказания.
    /// </summary>
    private void SendSnapshot()
    {
        _shipDtos.Clear();
        foreach (var ship in _ships.Values)
        {
            if (ship is not Meteor) _shipDtos.Add(ToDto(ship));
        }
        var world = new SnapshotCodec.World(Tick, _shipDtos, _loot.ToDtos(), _meteors.ToDtos(), _shots, _kills, _loot.Picks, _missiles.ToDtos());
        foreach (var player in _players.Values)
        {
            if (player.Connection is not { } connection) continue;
            var range = player.Effective(Balance).Radar + RadarMargin;
            var range2 = range * range;
            var cx = player.Ship.X;
            var cy = player.Ship.Y;
            var frame = player.View.Encode(world, player.Id, (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= range2);
            if (!connection.SendFrame(frame)) player.View.Reset(); // кадр выброшен — следующий должен быть ключевым
        }
    }

    private ShipDto ToDto(ShipEntity ship)
    {
        var s = ship.Ship;
        var (throttle, ack) = ship switch
        {
            Player p => (p.Connection is null || p.IsDead ? 0 : p.Inputs.Last.Throttle, p.Inputs.AckSeq),
            Drone d => (d.IsDead ? 0 : d.LastInput.Throttle, 0),
            Pirate p => (p.IsDead ? 0 : p.LastInput.Throttle, 0),
            Trader t => (t.IsDead ? 0 : t.LastInput.Throttle, 0),
            _ => (0.0, 0),
        };
        var pirate = ship as Pirate;
        return new ShipDto(
            ship.Id, s.X, s.Y, s.Rot, s.Vx, s.Vy, ship.HullId, throttle, ack,
            (int)Math.Ceiling(ship.Hp),
            (int)Math.Ceiling(ship.Shield),
            ship.MainWeaponId,
            ship.DeadUntilTick,
            ship.IsProtected(Tick) ? ship.ProtectedUntilTick : 0,
            ship switch
            {
                Pirate { IsDead: false, State: PirateState.Attack } attacking => attacking.TargetId,
                Pirate { IsDead: false, FireHeld: true } firing => firing.TargetId, // огрызается на ходу
                Trader { IsDead: false, FireHeld: true } firing => firing.TargetId,
                _ => 0,
            },
            pirate is null ? null : AiNames[(int)pirate.State],
            ship switch
            {
                Player { JumpTo: not null } jumper => jumper.JumpAtTick,
                Pirate { LeaveAtTick: > 0 } leaving => leaving.LeaveAtTick,
                Trader { LeaveAtTick: > 0 } leaving => leaving.LeaveAtTick,
                _ => 0,
            });
    }

    /// <summary>Состояния ИИ в снапшоте — по индексу <see cref="PirateState"/>.</summary>
    private static readonly string[] AiNames = ["patrol", "attack", "return", "leave"];

    private WelcomeMsg Welcome(Player player, bool resumed) =>
        new(player.Id, SimConfig.TickRate, Protocol.Version, Hulls, Balance.Weapons, Balance.Rules, resumed,
            Balance.Npc, Balance.Loot, Balance.Meteors, Balance.Shop, SystemInfo(), GalaxyInfo(Balance.Galaxy), Balance.Modules);

    /// <summary>Эта система для клиента: небо, станция, укрытие, врата с именами соседей и ценой прыжка.</summary>
    private SystemDto SystemInfo()
    {
        var galaxy = Balance.Galaxy;
        var system = Balance.SystemDef;
        return new SystemDto(
            SystemId,
            system.Name,
            system.Danger,
            system.Pvp,
            system.Station,
            system.Seed,
            system.Station ? Balance.Npc.StationSafeRadius : 0,
            galaxy.GateRange,
            galaxy.JumpSeconds,
            [.. system.GateList.Select(g => new GateDto(g.To, galaxy.System(g.To)?.Name ?? g.To, g.X, g.Y, galaxy.JumpCost(SystemId, g.To) ?? 0))],
            Balance.Sun,
            Balance.StationPath,
            Balance.GalaxySet is null ? [] : system.PlanetList,
            OrbitEpoch,
            Balance.Raids?.Base);
    }

    /// <summary>Карта галактики (GDD §55): все системы и маршруты с ценой прыжка.</summary>
    public static GalaxyDto GalaxyInfo(GalaxyRules galaxy) => new(
        [.. galaxy.SystemMap.Select(kv => new GalaxySystemDto(
            kv.Key, kv.Value.Name, kv.Value.Danger, kv.Value.Pvp, kv.Value.Station, kv.Value.Map?.X ?? 0, kv.Value.Map?.Y ?? 0))],
        [.. galaxy.LinkList.Select(l => new LinkDto(l.A, l.B, galaxy.Cost(l)))]);

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

    /// <summary>Имя занято другим игроком или NPC — во всей галактике, если комната в ней.</summary>
    private bool IsTaken(string name, Player? self) => _host?.IsNameTaken(name, self) ?? NameTaken(name, self);

    /// <summary>Имя занято другим игроком или NPC этой системы: игрок не назовётся «Пират Ур.2».</summary>
    public bool NameTaken(string name, Player? self)
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
        _host?.Gone(player);
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
                if (!Advance(player, MissionRules.GrabStep)) SendCollect(player);
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
        if (!Advance(player, MissionRules.SellStep)) SendCollect(player);
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
            player.DockOffset = Balance.StationPath.ToLocal(OrbitSeconds, player.Ship.X, player.Ship.Y);
            player.Ship.Vx = player.Ship.Vy = 0;
            player.FireHeld = false;
            player.TargetId = 0;
            player.SelectedLootId = 0;
            player.JumpTo = null;
            // Последняя станция — дом: здесь пилот появится после гибели и после входа в игру.
            player.Home = SystemId;
            RemoveShip(player);
            Save(player);
            _log.LogInformation("Player {Id} docked in {System}", player.Id, SystemId);
            // Груз доставки сдаётся сам, стоит пристыковаться к нужной станции.
            if (player.Missions.Active?.Offer is { Kind: MissionRules.DeliverKind } deliver && deliver.System == SystemId)
                Complete(player);
        }
        else
        {
            player.Docked = false;
            // Станция ушла по орбите, пока пилот был в доке, — вылет с той же её стороны.
            (player.Ship.X, player.Ship.Y) = Balance.StationPath.ToWorld(OrbitSeconds, player.DockOffset.X, player.DockOffset.Y);
            player.ResetInputs();
            player.ProtectedUntilTick = Tick + Balance.Rules.ProtectionTicks;
            _ships[player.Id] = player;
            _log.LogInformation("Player {Id} undocked", player.Id);
        }
        SendHangar(player);
        if (!on) Advance(player, MissionRules.UndockStep);
    }

    /// <summary>
    /// Купить в доке (GDD §26, §30). Корпус — один раз, сразу ставится. Пушку или модуль — сколько угодно: со slot —
    /// сразу в этот слот (старое — на склад; не встаёт по классу или энергии — покупки нет), без slot — в первый
    /// свободный подходящий слот, а если такого нет — на склад.
    /// </summary>
    public void Buy(IClientConnection connection, string? kind, string? id, string? slot = null)
    {
        if (id is null || !_byConnection.TryGetValue(connection.Id, out var player) || !player.Docked) return;
        var shop = Balance.Shop;
        var price = kind switch
        {
            Protocol.HullItem when Hulls.ContainsKey(id) && !player.OwnsHull(id) => shop.HullPrice(id),
            Protocol.ItemKind when IsItem(id) => shop.ItemPrice(id),
            _ => null,
        };
        if (price is not { } cost) return;
        if (kind == Protocol.ItemKind)
        {
            if (slot is null)
            {
                slot = FreeSlot(player, id);
            }
            else if (Fitting.CanInstall(player.Hull(Hulls), player.Fit, slot, id, Balance.Weapons, Balance.Modules) is { } problem)
            {
                // Слот назван явно — пилот хотел поставить; не встаёт — не продаём, чтобы не копить ненужное.
                connection.Send(new NoticeMsg(FitNotice(problem)));
                return;
            }
        }
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
        else if (slot is not null)
        {
            if (player.Fit.Get(slot) is { } old && !player.IsGuest) player.Store(old);
            Refit(player, player.Fit.With(slot, id));
        }
        else if (!player.IsGuest)
        {
            player.Store(id);
        }
        SendCargo(player); // кредиты, а у корпуса — ещё и ёмкость трюма
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} bought {Kind} {Item} for {Credits} credits", player.Id, kind, id, cost);
    }

    /// <summary>Пустой слот, куда предмет встаёт прямо сейчас; null — такого нет.</summary>
    private string? FreeSlot(Player player, string id)
    {
        var hull = player.Hull(Hulls);
        IEnumerable<string> slots = Balance.Weapons.ContainsKey(id)
            ? Enumerable.Range(0, hull.Slots.Count).Select(Fitting.WeaponSlot)
            : Balance.Modules?.GetValueOrDefault(id) is { } module ? [module.Slot] : [];
        return slots.FirstOrDefault(slot =>
            player.Fit.Get(slot) is null && Fitting.CanInstall(hull, player.Fit, slot, id, Balance.Weapons, Balance.Modules) is null);
    }

    /// <summary>Ремонт в доке: корпус и щит до полных, по repairPrice из shop.json за единицу корпуса.</summary>
    public void Repair(IClientConnection connection)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player) || !player.Docked) return;
        var hull = player.Effective(Balance);
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
        if (!Balance.HasStation) return false;
        var (sx, sy) = StationPosition;
        var dx = player.Ship.X - sx;
        var dy = player.Ship.Y - sy;
        var range = Balance.Loot.StationRange;
        return dx * dx + dy * dy <= range * range;
    }

    private IEnumerable<Player> DockedPlayers() => _players.Values.Where(p => p.Docked);

    /// <summary>Ангар — личное дело пилота, как и трюм.</summary>
    private void SendHangar(Player player)
    {
        if (player.Connection is null) return;
        var hull = player.Effective(Balance);
        player.Connection.Send(new HangarMsg(
            player.HullId,
            player.Fit,
            player.IsGuest ? [.. Hulls.Keys] : [.. player.Hulls.Where(Hulls.ContainsKey).Order(StringComparer.Ordinal)],
            new SortedDictionary<string, int>(player.Storage, StringComparer.Ordinal),
            player.Docked,
            (int)Math.Ceiling(player.Hp),
            (int)Math.Ceiling(player.MaxHp(hull)),
            player.Fuel,
            Tank(player),
            player.Home ?? SystemId,
            (int)Math.Round(Fitting.Power(player.Fit, Balance.Weapons, Balance.Modules)),
            Balance.Modules is null ? 0 : (int)Math.Round(Fitting.Output(player.Fit, Balance.Modules)),
            player.IsGuest));
    }

    /// <summary>Кредиты пилоту за вторжение: сразу в аккаунт и клиенту.</summary>
    public void Pay(Player player, int credits)
    {
        if (credits <= 0) return;
        player.Credits += credits;
        SendCargo(player);
        Save(player);
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
            player.WeaponId ?? "",
            [.. player.Hulls.Order(StringComparer.Ordinal)],
            [],
            new Dictionary<string, int>(player.Cargo.Items),
            player.Fuel,
            player.Home,
            player.Missions.Tutorial,
            player.Missions.Active,
            player.Missions.Seed,
            player.Fit,
            new SortedDictionary<string, int>(player.Storage, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Задания (GDD §36): взять с доски и сдать «собрать» — в доке, бросить — где угодно; пропустить обучение (§54).
    /// Взять можно только одно; груз доставки должен влезть в трюм.
    /// </summary>
    public void Mission(IClientConnection connection, string? action, string? id)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player)) return;
        var log = player.Missions;
        switch (action)
        {
            case Protocol.AcceptMission:
            {
                if (log.Active is not null) return;
                if (!player.Docked)
                {
                    connection.Send(new NoticeMsg(Protocol.TooFarNotice));
                    return;
                }
                var offer = Board(player).FirstOrDefault(o => o.Id == id);
                if (offer is null)
                {
                    SendMissions(player); // доска успела смениться: пусть клиент увидит новую
                    return;
                }
                if (offer.Kind == MissionRules.DeliverKind &&
                    player.Cargo.Used(Balance.Loot) + offer.Count > player.Hull(Hulls).Cargo)
                {
                    connection.Send(new NoticeMsg(Protocol.CargoFullNotice));
                    return;
                }
                log.Active = new ActiveMission(offer);
                log.Seed++;
                player.Cargo.Reserved = Reserve(log.Active);
                _log.LogInformation("Player {Id} took a {Kind} mission for {Reward} credits", player.Id, offer.Kind, offer.Reward);
                break;
            }
            case Protocol.AbandonMission:
                if (log.Active is null) return;
                log.Active = null;
                player.Cargo.Reserved = 0;
                break;
            case Protocol.CompleteMission:
            {
                if (log.Active?.Offer is not { Kind: MissionRules.CollectKind, Item: { } item } collect) return;
                if (!player.Docked)
                {
                    connection.Send(new NoticeMsg(Protocol.TooFarNotice));
                    return;
                }
                if (!player.Cargo.Remove(item, collect.Count)) return;
                Complete(player);
                return;
            }
            case Protocol.SkipTutorial:
                if (log.Tutorial == MissionLog.Finished) return;
                log.Tutorial = MissionLog.Finished;
                break;
            default:
                return;
        }
        SendCargo(player);
        SendMissions(player);
        Save(player);
    }

    /// <summary>Шаг обучения stepId сделан — если он сейчас текущий. Шаги идут строго по порядку.</summary>
    /// <returns>true — засчитан: состояние заданий уже ушло клиенту.</returns>
    private bool Advance(Player player, string stepId)
    {
        var rules = Balance.Missions;
        if (rules.Step(player.Missions.Tutorial) is not { } step || step.Id != stepId) return false;
        var last = rules.Step(player.Missions.Tutorial + 1) is null;
        player.Missions.Tutorial = last ? MissionLog.Finished : player.Missions.Tutorial + 1;
        player.Credits += step.Reward;
        SendCargo(player);
        SendMissions(player, new MissionDoneDto(Protocol.TutorialDone, step.Reward, step.Title, Last: last));
        Save(player);
        _log.LogInformation("Player {Id} did tutorial step {Step} for {Reward} credits", player.Id, step.Id, step.Reward);
        return true;
    }

    /// <summary>Пилот уничтожил корабль: учебный дрон — шаг обучения, пират — в счёт задания в той системе.</summary>
    private void CountKill(Player player, ShipEntity? victim)
    {
        if (victim is Drone)
        {
            Advance(player, MissionRules.DroneStep);
            return;
        }
        // Рейнджеры — не пираты: за них задание не засчитывается.
        if (victim is not Pirate { Type.IsPirate: true } pirate || player.Missions.Active is not { } active) return;
        var offer = active.Offer;
        if (offer.Kind != MissionRules.KillKind || offer.System != SystemId) return;
        if (offer.Npc is not null && offer.Npc != pirate.Spawn.Type) return;
        player.Missions.Active = active with { Progress = active.Progress + 1 };
        if (active.Progress + 1 >= offer.Count)
        {
            Complete(player);
            return;
        }
        SendMissions(player);
        Save(player);
    }

    /// <summary>Задание выполнено: награда, место в трюме свободно, доска обновляется.</summary>
    private void Complete(Player player)
    {
        if (player.Missions.Active is not { } active) return;
        player.Missions.Active = null;
        player.Missions.Seed++;
        player.Cargo.Reserved = 0;
        player.Credits += active.Offer.Reward;
        SendCargo(player);
        SendMissions(player, new MissionDoneDto(Protocol.MissionDone, active.Offer.Reward, Mission: active.Offer));
        Save(player);
        _log.LogInformation(
            "Player {Id} completed a {Kind} mission for {Reward} credits", player.Id, active.Offer.Kind, active.Offer.Reward);
    }

    /// <summary>У «собрать» прогресс — сколько такого в трюме: трюм изменился — клиенту новый счёт.</summary>
    private void SendCollect(Player player)
    {
        if (player.Missions.Active?.Offer.Kind == MissionRules.CollectKind) SendMissions(player);
    }

    /// <summary>Сколько места в трюме держит задание: груз доставки.</summary>
    private static int Reserve(ActiveMission? active) =>
        active?.Offer is { Kind: MissionRules.DeliverKind } deliver ? deliver.Count : 0;

    /// <summary>Доска станции этой системы для пилота; без станции — пусто.</summary>
    private IReadOnlyList<MissionOffer> Board(Player player) =>
        Balance.HasStation ? Balance.Missions.Board(Balance, SystemId, player.Missions.Seed) : [];

    /// <summary>Обучение и задания — личное дело пилота, как и трюм.</summary>
    private void SendMissions(Player player, MissionDoneDto? done = null)
    {
        if (player.Connection is null) return;
        var rules = Balance.Missions;
        var log = player.Missions;
        var step = rules.Step(log.Tutorial);
        var active = log.Active;
        if (active?.Offer is { Kind: MissionRules.CollectKind, Item: { } item })
            active = active with { Progress = Math.Min(active.Offer.Count, player.Cargo.Items.GetValueOrDefault(item)) };
        player.Connection.Send(new MissionsMsg(
            step is null ? null : new TutorialDto(log.Tutorial, rules.Steps.Count, step.Id, step.Title, step.Hint),
            active,
            Board(player),
            done));
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
            player.Credits,
            player.Cargo.Reserved));
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


    private static int? Ceiling(double? value) => value is { } v ? (int)Math.Ceiling(v) : null;

    /// <summary>
    /// Игроки и NPC системы: имена нужны для подписей, флаг npc — чтобы клиент не писал о NPC в ленту, kind — для цвета.
    /// Плюс сколько пилотов на связи во всей галактике — галактика зовёт это и тогда, когда кто-то вошёл в другой системе.
    /// </summary>
    public void BroadcastPlayers()
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
                    Ceiling(p.MaxHp(p.Hull(Hulls))), Ceiling(p.MaxShield(p.Hull(Hulls))),
                    p.Type.IsRanger ? Protocol.RangerKind : Protocol.PirateKind),
                Trader t => new PlayerDto(
                    t.Id, t.Name, Online: true, Npc: true,
                    Ceiling(t.MaxHp(t.Hull(Hulls))), Ceiling(t.MaxShield(t.Hull(Hulls))), Protocol.TraderKind),
                _ => new PlayerDto(s.Id, s.Name, Online: true, Npc: true),
            })
            .ToList();
        var message = Protocol.Encode(new PlayersMsg(list, _host?.OnlineTotal ?? OnlineCount));

        foreach (var player in _players.Values) player.Connection?.SendRaw(message);
    }
}
