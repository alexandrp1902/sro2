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
    /// <summary>Игроки — враги властей этой системы: рейнджеры идут на них, пираты не трогают (M13).</summary>
    private readonly HashSet<int> _outlaws = [];

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
        StartMarket();
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

    /// <summary>Пираты и рейнджеры в системе: логова, налёты, вторжения и звенья заданий.</summary>
    public IReadOnlyList<Pirate> Pirates => _pirates;

    /// <summary>Что лежит в космосе: обломки, содержимое контейнеров и скриптованный груз сюжета (M20a).</summary>
    public IReadOnlyList<LootDrop> Drops => _loot.Drops;

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

    /// <summary>
    /// Стопка груза в точке (x, y) — для тестов и отладки. Обычно груз появляется сам: из обломков,
    /// из контейнеров или из трюма выброшенного за борт.
    /// </summary>
    public void SpillAt(string item, int count, double x, double y) =>
        _loot.SpillOne(Balance.Loot, item, count, x, y, 0, 0, Tick);

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
            Credits = Balance.Economy.StartCredits,
            Home = SystemId,
        };
        Refit(player, player.Fit);
        // Гость — это тесты, смоук-скрипты и боты: обучения у него нет, доска заданий есть.
        player.Missions.Seed = Random.Shared.Next();
        Enter(player, connection);
    }

    /// <summary>
    /// Пилот с аккаунтом: ник и пароль (или ключ устройства) уже проверены в сетевом потоке, см. <see cref="AccountStore"/>.
    /// Корабль, который ещё ждёт после обрыва, достаётся новому соединению — даже с другого устройства (GDD §61).
    /// </summary>
    /// <param name="career">
    /// Путь нового пилота (M15.5): корабль, кредиты, груз, место и первое отношение мира. null — общий
    /// стартовый набор. У вернувшегося пилота путь уже в профиле, и переписать его отсюда нельзя.
    /// </param>
    public void JoinAccount(IClientConnection connection, string accountId, string name, string? career = null)
    {
        if (_byConnection.ContainsKey(connection.Id)) return;
        if (_byToken.TryGetValue(accountId, out var player))
        {
            Resume(player, connection);
            return;
        }

        var profile = _accounts?.Profile(accountId);
        player = new Player(_newId(), accountId, UniqueName(SanitizeName(name), null), SimConfig.DefaultHull, SimConfig.DefaultWeapon, accountId);
        // Путь берётся только у нового аккаунта: у вернувшегося он свой, из профиля.
        var path = profile is null ? Balance.Careers.Of(career) : null;
        player.Career = profile is not null ? profile.Career : path is null ? null : career ?? Balance.Careers.Default;
        if (profile is null)
        {
            player.Credits = path?.Credits ?? Balance.Economy.StartCredits; // GDD §54: новый пилот получает стартовый капитал
            if (path is { Hull: { } hull } && Hulls.ContainsKey(hull))
            {
                player.HullId = hull;
                player.Hulls.Add(hull);
            }
            if (path is not null)
            {
                player.Fit = CareerRules.FitOf(path);
                // Часть капитала уже в товаре: торговцу есть что везти с первой минуты.
                foreach (var (item, count) in path.CargoMap) if (count > 0) player.Cargo.Add(item, count);
            }
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
        // Бака больше нет (M15.6), а модуль был оплачен — выкупаем его по legacy-прайсу. Считать надо было
        // до Refit: он про слот «бак» уже не знает, а из profile.Storage баки и так не дошли — IsItem их
        // не признал. Поэтому источник один: сырой профиль.
        var tanksSold = TankRefund(profile);
        player.Credits += tanksSold;
        Refit(player, player.Fit);
        // Дом — система, где пилот появился: её выбрала галактика по профилю.
        player.Home = SystemId;
        // Место последней стыковки (M15). У профиля старше M15 его нет — там домом звалась система,
        // и это её станция; место могло и пропасть из баланса, тогда берём главное место системы.
        var savedPlace = profile?.Place ?? path?.Place ?? (profile?.System is { } old ? PlaceKey.Station(old) : null);
        player.HomePlace = Balance.Place(savedPlace)?.Key ?? Balance.DefaultPlace?.Key;
        // Где стоят остальные корпуса ангара (M15.6). Профиль старше M15.6 этого не знает, и место
        // могло пропасть из баланса — тогда корабль ждёт дома: там пилот его и станет искать первым.
        foreach (var id in player.Hulls)
        {
            if (id == player.HullId) continue;
            var saved = profile?.Ships?.GetValueOrDefault(id);
            player.HullPlaces[id] = Balance.Galaxy.HasPlace(saved) ? saved! : player.HomePlace ?? PlaceKey.Station(SystemId);
            // Чем снаряжён каждый корабль ангара (M20). Профиль старше M20 этого не знает: такие корабли
            // голые, и пилот оденет их сам — то, что на них стояло, лежит на складе с прошлой пересадки.
            if (profile?.Fits?.GetValueOrDefault(id) is { } fit) player.HullFits[id] = fit;
        }
        // Обучение — только новому пилоту (GDD §54): профиль старше M8 считается прошедшим его.
        player.Missions.Tutorial = TutorialFrom(profile, player.Career);
        // Живое задание в комнату не возвращается: его конвой и звено остались в прошлом вылете (M14).
        // Профиль старше M15 зовёт заказчика и адрес по системе — переводим в ключи мест.
        player.Missions.Active =
            profile?.Mission is { } taken && !MissionRules.IsLive(taken.Offer.Kind) ? MissionRules.Upgrade(taken) : null;
        player.Missions.Seed = profile?.MissionSeed ?? Random.Shared.Next();
        player.Cargo.Reserved = Reserve(player.Missions.Active);
        // Сюжет (M20a): что пройдено и как пилот выбирал. Живое сюжетное задание, как и обычное живое,
        // в комнату не возвращается — но выполненное и флаги переживают всё.
        LoadStory(player, profile);
        // Репутация тает по часам: распад за время отсутствия применяется прямо здесь, на входе.
        player.Rep.Load(profile?.Reputation, profile?.RepAt, NowSeconds, Balance.Reputation);
        // Первое отношение мира — строго после Load: он ставит очки с нуля и затёр бы прибавку.
        // И через player.Rep.Add, а не RoomReputation.AddRep: тот шлёт уведомление, и новый пилот
        // увидел бы «+15 репутации» ещё до первого кадра.
        if (path is not null)
            foreach (var (key, delta) in path.RepMap) player.Rep.Add(key, delta, NowSeconds, Balance.Reputation);
        Enter(player, connection);
        // Прочность корпуса (M15.7): разбитый корабль остаётся разбитым и после выхода — иначе гибель
        // лечилась бы перезаходом. Строго после Enter: он ставит корабль в мир и корпус при этом полный.
        // Профиль старше M15.7 прочности не знает — такой пилот входит целым.
        if (profile?.Hp is { } savedHp) player.Hp = Math.Clamp(savedHp, 1, player.MaxHp(player.Effective(Balance)));
        // Профиль старше M18 хранит номер шага: переведённый в id, он записывается сразу.
        if (profile is null || profile.Fit is null || profile.TutorialStep is null) Save(player);
        if (tanksSold > 0)
        {
            connection.Send(new NoticeMsg(Protocol.TanksSoldNotice, tanksSold));
            // Без записи возврат повторился бы при каждом входе.
            Save(player);
            _log.LogInformation("Player {Id} got {Credits} credits back for fuel tanks", player.Id, tanksSold);
        }
    }

    /// <summary>
    /// Шаг обучения из профиля (M18). Новый пилот — первый шаг своего пути. С M18 шаг хранится по id;
    /// до него — номером в списке, каким тот был тогда (<see cref="MissionRules.LegacyStep"/>): кто стоял
    /// на «дроне», на нём и остаётся, а вставленное перед ним уже позади. Профиль старше M8 — пройдено.
    /// Шага с таким id в списке больше нет — тоже пройдено: лучше так, чем вернуть пилота в начало.
    /// </summary>
    private string? TutorialFrom(AccountProfile? profile, string? career)
    {
        var rules = Balance.Missions;
        var id = profile is null ? rules.First(career)
            : profile.TutorialStep is { } step ? step
            : profile.Tutorial is { } index ? MissionRules.LegacyStep(career, index)
            : null;
        return rules.Step(career, id) is null ? null : id;
    }

    /// <summary>
    /// Сколько вернуть за баки из профиля старше M15.6: за стоявший и за все, что лежали на складе.
    /// По полной цене, а не по sellShare: модуль отбирают решением разработчика, и скидка на это
    /// читалась бы как ошибка. 0 — баков не было или цен для них нет.
    /// </summary>
    private int TankRefund(AccountProfile? profile)
    {
        if (profile is null) return 0;
        var shop = Balance.Economy;
        var total = 0;
        if (profile.Fit?.Tank is { } fitted) total += shop.LegacyPrice(fitted) ?? 0;
        foreach (var (id, count) in profile.Storage ?? new Dictionary<string, int>())
            if (count > 0 && !IsItem(id) && shop.LegacyPrice(id) is { } price) total += price * count;
        return total;
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
        // Точка появления нужна и тому, кто входит в док: из неё считается DockOffset, и в неё же будет вылет.
        SpawnHere(player);
        player.Attach(connection);
        _players[player.Id] = player;
        // С M15.6 вход всегда в доке: корабль стоит там, где его оставили (AccountProfile.Place).
        // Возврат после обрыва связи — не вход: там корабль так и висел в космосе, см. Resume.
        // Док, закрытый для тебя (M13), не должен быть местом, где ты просыпаешься, — враг входит в полёте.
        var enemy = IsEnemy(player);
        if (HomePlaceOf(player) is { } home && !enemy)
        {
            player.Docked = true;
            player.DockedPlace = home.Key;
            player.HomePlace = home.Key;
            player.DockOffset = home.Orbit.ToLocal(OrbitSeconds, player.Ship.X, player.Ship.Y);
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
        if (player.Docked)
        {
            MakeRumours(player); // что здесь рассказывают — как при стыковке
            SendShop(player);    // витрина места, а не главного места системы
            SendMarket(player);  // иначе вкладка рынка откроется пустой
        }
        else if (enemy)
        {
            connection.Send(new NoticeMsg(Protocol.DockClosedNotice));
        }
        SendMissions(player);
        SendRep(player);
        _log.LogInformation(
            "Player {Id} '{Name}' joined as {Hull} {Where}, online {Count}",
            player.Id, player.Name, player.HullId, player.Docked ? $"docked at {player.DockedPlace}" : "in space", OnlineCount);
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
        // Вернулся в док — ему нужны и здешняя витрина, и здешние цены: без них вкладки пустые.
        if (player.Docked)
        {
            SendShop(player);
            SendMarket(player);
        }
        SendMissions(player);
        SendRep(player);
        _log.LogInformation("Player {Id} '{Name}' resumed, online {Count}", player.Id, player.Name, OnlineCount);
    }

    /// <summary>Соединение закрылось. Корабль остаётся ждать игрока, если у того есть сессия.</summary>
    public void Disconnect(IClientConnection connection)
    {
        // Соединение, которое уже вытеснили новым, здесь не найдётся и корабль не отцепит.
        if (!_byConnection.Remove(connection.Id, out var player)) return;
        // Корабль остаётся в мире, но за него больше никто не отвечает: сделку рвём, иначе второй
        // дожал бы её кнопкой в одиночку (M16b).
        _host?.Busy(player, TradeCodes.Left);
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
    /// <param name="arrival">Точка у врат; null — появление у своего места, как после гибели.</param>
    /// <param name="hullShare">
    /// Сколько корпуса дать появившемуся у места: 1 — целый. Возвращение домой после гибели приходит
    /// с <see cref="CombatRules.DeathHullShare"/> — иначе гибель вдали от дома чинила бы корабль даром (M16a).
    /// </param>
    public void Admit(Player player, (double X, double Y)? arrival, double hullShare = 1)
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
            SpawnHere(player, hullShare);
            // Разбитый корпус — сразу в профиль, как и при гибели дома: иначе выход из игры лечил бы.
            if (hullShare < 1) Save(player);
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
            SendRep(player); // «здесь» — уже другая система
        }
        // Доска — уже этой системы. Прыжок — шаг обучения; засчитывается после welcome, чтобы строка в ленте
        // пришла уже в новую систему.
        if (arrival is null || !Advance(player, new TutorialEvent(MissionRules.JumpStep, System: SystemId)))
            SendMissions(player);
        // Прилетел за скриптованным грузом — он уже ждёт: раскладывается по прибытии, а не по вылету
        // из дока, иначе пилот, взявший миссию в другой системе, нашёл бы пустые обломки (M20a).
        if (!player.Docked) StoryHere(player);
        BroadcastPlayers();
        _log.LogInformation("Player {Id} '{Name}' arrived in {System}", player.Id, player.Name, SystemId);
    }

    /// <summary>
    /// Корабль уходит в другую систему: из комнаты он исчезает целиком, но не сохраняется и связь не теряет.
    /// Ростер отсюда разошлёт галактика, когда корабль уже будет в новой системе: иначе в «онлайн» его бы не посчитали.
    /// </summary>
    public void Release(Player player)
    {
        // Конвой и звено остаются здесь — увезти их с собой нельзя, значит работа сорвана (M14).
        if (RunOf(player) is not null) Fail(player, Protocol.LeftFail);
        // Сюжетные ящики и вызванные корабли тоже остаются в прошлой системе: id пилота в новой комнате
        // будет другим, и хозяина у них там всё равно не найдётся. Вернётся — разложим заново.
        StoryEnd(player);
        if (!_players.Remove(player.Id)) return;
        if (player.Connection is { } connection) _byConnection.Remove(connection.Id);
        if (player.Token is not null) _byToken.Remove(player.Token);
        player.JumpTo = null;
        RemoveShip(player);
    }

    /// <summary>
    /// Гиперпрыжок (GDD §5): у врат в систему to — через jumpSeconds корабль уйдёт. Топлива прыжок не стоит
    /// с M15.6. to = null — отменить подготовку.
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
        if (Balance.SystemDef.GateTo(to) is not { } gate || galaxy.Link(SystemId, to) is null) return;
        if (!NearGate(player, gate, galaxy.GateRange))
        {
            connection.Send(new NoticeMsg(Protocol.GateFarNotice));
            return;
        }
        player.JumpTo = to;
        player.JumpAtTick = Tick + galaxy.JumpTicks;
        _log.LogInformation("Player {Id} charges a jump {From} → {To}", player.Id, SystemId, to);
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
    /// по готовности галактика переводит корабль в соседнюю систему.
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
            if (player.IsDead || player.Docked || player.Connection is null || gate is null ||
                galaxy.Link(SystemId, to) is null || !NearGate(player, gate, galaxy.GateRange * GateSlack))
            {
                CancelJump(player, notify: true);
                continue;
            }
            if (Tick < player.JumpAtTick) continue;
            player.JumpTo = null;
            player.JumpAtTick = 0;
            Save(player);
            _host!.Busy(player, TradeCodes.Jumped); // обмен через полгалактики не идёт (M16b)
            _log.LogInformation("Player {Id} jumps {From} → {To}", player.Id, SystemId, to);
            _host!.Depart(this, player, to, jump: true);
        }
    }

    public void Input(IClientConnection connection, int seq, MoveInput input)

    {
        if (_byConnection.TryGetValue(connection.Id, out var player)) player.Inputs.Enqueue(seq, input);
    }

    /// <summary>
    /// Поставить корпус из ангара (GDD §51): пилоту с аккаунтом — только свой и только в доке,
    /// и только там, где есть верфь. В поселении без верфи корабль не меняют — в этом и разница со станцией (M15).
    /// </summary>
    public void SetHull(IClientConnection connection, string? hullId)
    {
        if (hullId is null || !Hulls.ContainsKey(hullId) || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        if (!player.IsGuest && (!player.Docked || !player.OwnsHull(hullId))) return;
        if (PlaceOf(player) is { Shipyard: false })
        {
            connection.Send(new NoticeMsg(Protocol.NoShipyardNotice));
            return;
        }
        // Корабль стоит там, где его оставили (M15.6): сесть в него можно, только придя туда самому
        // или заказав перевозку. У гостя ангар свой, ненастоящий, — ему это правило ни к чему.
        if (!player.IsGuest && player.HullPlaces.TryGetValue(hullId, out var at) && at != PlaceOf(player)?.Key)
        {
            connection.Send(new NoticeMsg(Protocol.ShipElsewhereNotice));
            return;
        }
        ChangeHull(player, hullId);
        SendCargo(player); // у нового корпуса своя ёмкость; груз при этом не выбрасывается (GDD §24)
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} switched to {Hull}", player.Id, hullId);
    }

    /// <summary>
    /// Сколько стоит привезти сюда этот корпус (M15.6): 0 — он уже здесь, base — другое место этой же
    /// системы, дальше — по числу прыжков. null — корпус не свой, он под пилотом, услуги тут нет
    /// или пути по вратам нет вовсе.
    /// </summary>
    private int? TransportQuote(Player player, string hullId)
    {
        if (PlaceOf(player) is not { } here) return null;
        if (!player.HullPlaces.TryGetValue(hullId, out var from)) return null;
        if (from == here.Key) return 0;
        if (Balance.Galaxy.SystemOfPlace(from) is not { } system) return null;
        if (Balance.Galaxy.Jumps(SystemId, system) is not { } jumps) return null;
        return ShopOf(player).TransportCost(ShopOf(player).HullPrice(hullId) ?? 0, jumps);
    }

    /// <summary>
    /// Перегнать свой корпус из другого дока сюда (M15.6). Второй путь — слетать за ним самому
    /// и пересесть на месте: он бесплатен и работает по тому же правилу, что и <see cref="SetHull"/>.
    /// </summary>
    public void Transport(IClientConnection connection, string? hullId)
    {
        if (hullId is null || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        if (player.IsGuest || !player.Docked || !player.OwnsHull(hullId) || hullId == player.HullId) return;
        if (PlaceOf(player) is not { } here) return;
        // Двигать корабли — работа верфи: где её нет, ангар можно только посмотреть.
        if (!here.Shipyard)
        {
            connection.Send(new NoticeMsg(Protocol.NoShipyardNotice));
            return;
        }
        if (TransportQuote(player, hullId) is not { } cost)
        {
            connection.Send(new NoticeMsg(Protocol.NoRouteNotice));
            return;
        }
        if (player.Credits < cost)
        {
            connection.Send(new NoticeMsg(Protocol.NoCreditsNotice));
            return;
        }
        player.Credits -= cost;
        player.HullPlaces[hullId] = here.Key;
        SendCargo(player);
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} had {Hull} towed to {Place} for {Credits} credits", player.Id, hullId, here.Key, cost);
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

    /// <summary>
    /// Оснащение пачкой (M20): снять с корабля всё на склад или заполнить пустые слоты тем, что на складе
    /// лежит. Ручная развеска по одному модулю была главной работой в доке, а после покупки корпуса —
    /// единственной: новый корабль приходит голым, и одеть его надо целиком.
    ///
    /// Снять можно и с корабля, который стоит в этом же доке: слазить за модулями в ангар — то же самое,
    /// что снять их со своего, только корабль под рукой, а не под пилотом.
    /// </summary>
    /// <param name="mode"><see cref="Protocol.StripFit"/> или <see cref="Protocol.FillFit"/>.</param>
    /// <param name="hullId">Корпус из ангара, стоящий здесь; null — тот, под которым пилот сидит.</param>
    public void FitAll(IClientConnection connection, string? mode, string? hullId = null)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player)) return;
        // Пачкой работают только со складом: гостю его негде держать, и снятое пропало бы.
        if (player.IsGuest || !player.Docked) return;
        if (hullId is not null)
        {
            StripParked(connection, player, mode, hullId);
            return;
        }
        var changed = mode switch
        {
            Protocol.StripFit => Strip(player, player.Hull(Hulls), player.Fit) is var (fit, taken) && taken > 0 ? fit : null,
            Protocol.FillFit => Fill(player, player.Hull(Hulls), player.Fit),
            _ => null,
        };
        if (changed is null)
        {
            connection.Send(new NoticeMsg(Protocol.NothingToFitNotice));
            return;
        }
        Refit(player, changed);
        SendCargo(player);
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} {Mode} the whole fit", player.Id, mode);
    }

    /// <summary>Раздеть корабль из ангара: он должен стоять здесь же и на месте с верфью — двигать чужое железо негде.</summary>
    private void StripParked(IClientConnection connection, Player player, string? mode, string hullId)
    {
        if (mode != Protocol.StripFit || !player.OwnsHull(hullId) || !Hulls.TryGetValue(hullId, out var hull)) return;
        if (PlaceOf(player) is not { } here || !here.Shipyard)
        {
            connection.Send(new NoticeMsg(Protocol.NoShipyardNotice));
            return;
        }
        if (player.HullPlaces.GetValueOrDefault(hullId) != here.Key)
        {
            connection.Send(new NoticeMsg(Protocol.ShipElsewhereNotice));
            return;
        }
        var (fit, taken) = Strip(player, hull, player.HullFits.GetValueOrDefault(hullId, Fitting.Empty));
        if (taken == 0)
        {
            connection.Send(new NoticeMsg(Protocol.NothingToFitNotice));
            return;
        }
        // Приводим к корпусу тут же: пилот сядет в него когда-нибудь потом, а пустые обязательные слоты
        // должны закрыться стартовыми модулями сразу — иначе в ангаре стоял бы корабль без двигателя.
        player.HullFits[hullId] = Fitting.Refit(hull, fit, Balance.Weapons, Balance.Modules);
        SendCargo(player);
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} stripped parked {Hull}: {Taken} items", player.Id, hullId, taken);
    }

    /// <summary>
    /// Всё снаряжение — на склад, кроме обязательного: без двигателя, радара и генератора корабль не летает,
    /// и «снять всё» не должно оставлять его в доке навсегда.
    /// </summary>
    private (ShipFit Fit, int Taken) Strip(Player player, HullParams hull, ShipFit fit)
    {
        var taken = 0;
        foreach (var slot in SlotsOf(hull))
        {
            if (Fitting.RequiredSlots.Contains(slot) || fit.Get(slot) is not { } id) continue;
            player.Store(id);
            fit = fit.With(slot, null);
            taken++;
        }
        return (fit, taken);
    }

    /// <summary>
    /// Пустые слоты — тем, что лежит на складе: в каждый встаёт лучшее из подходящего, лучшее — самое дорогое
    /// по прайсу. Занятые слоты не трогаем: то, что пилот поставил сам, кнопка «поставить всё» менять не должна.
    /// </summary>
    /// <returns>null — ставить было нечего.</returns>
    private ShipFit? Fill(Player player, HullParams hull, ShipFit fit)
    {
        var shop = Balance.Economy;
        var put = 0;
        // Сперва модули, потом пушки: энергии на всё может не хватить, и щит важнее лишнего ствола —
        // без него корабль просто мягче, а без пушки он ещё и никого не убьёт, но живым вернётся.
        foreach (var slot in Fitting.ModuleSlots.Concat(SlotsOf(hull).Where(s => !Fitting.ModuleSlots.Contains(s))))
        {
            if (fit.Get(slot) is not null) continue;
            string? best = null;
            var bestPrice = -1;
            foreach (var (id, count) in player.Storage)
            {
                if (count <= 0 || id == best) continue;
                if (Fitting.CanInstall(hull, fit, slot, id, Balance.Weapons, Balance.Modules) is not null) continue;
                var price = shop.ItemPrice(id) ?? 0;
                // Равные по цене — по имени: иначе порядок зависел бы от того, как лёг словарь склада.
                if (price > bestPrice || (price == bestPrice && string.CompareOrdinal(id, best) < 0))
                {
                    best = id;
                    bestPrice = price;
                }
            }
            if (best is null) continue;
            player.Unstore(best);
            fit = fit.With(slot, best);
            put++;
        }
        return put > 0 ? fit : null;
    }

    /// <summary>Все слоты корпуса по порядку: пушки, модули, вспомогательные.</summary>
    private static IEnumerable<string> SlotsOf(HullParams hull)
    {
        for (var i = 0; i < hull.Slots.Count; i++) yield return Fitting.WeaponSlot(i);
        foreach (var slot in Fitting.ModuleSlots) yield return slot;
        for (var i = 0; i < hull.UtilitySlots; i++) yield return Fitting.UtilitySlot(i);
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
    }

    /// <summary>
    /// Продать со склада пушку или модуль — за долю цены (shop.json sellShare). id = null — продать
    /// весь склад разом (M16a): кнопка «Продать модули» на рынке, чтобы за этим не ходить по слотам.
    /// Стоящее на корабле не продаётся: на складе его нет по определению.
    /// </summary>
    public void SellItem(IClientConnection connection, string? id)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player) || player.IsGuest || !player.Docked) return;
        if (id is null)
        {
            SellStorage(player);
            return;
        }
        if (!player.Unstore(id)) return;
        var credits = ShopOf(player).SellPrice(id);
        player.Credits += credits;
        SendCargo(player);
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} sold {Item} for {Credits} credits", player.Id, id, credits);
    }

    /// <summary>
    /// Продать весь склад места одной сделкой (M16a). То, за что здесь не дают ни кредита, остаётся лежать:
    /// выбрасывать чужими руками нечего — для этого есть отдельная кнопка у самой строки.
    /// </summary>
    private void SellStorage(Player player)
    {
        var shop = ShopOf(player);
        var credits = 0;
        var sold = 0;
        foreach (var (id, count) in player.Storage.ToList())
        {
            var price = shop.SellPrice(id);
            if (price <= 0 || count <= 0) continue;
            player.Storage.Remove(id);
            credits += price * count;
            sold += count;
        }
        if (sold == 0) return;
        player.Credits += credits;
        SendCargo(player);
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} sold {Count} stored items for {Credits} credits", player.Id, sold, credits);
    }

    /// <summary>
    /// Продать корабль из ангара вместе со всем, что на нём стоит (M20c): доля местной цены корпуса
    /// и такая же доля за каждую вещь его оснащения (<see cref="ShopRules.SellShipPrice"/>).
    ///
    /// Обязательную тройку — двигатель, радар, генератор — тоже оплачиваем. «Снять всё» её нарочно
    /// оставляет, чтобы корабль в ангаре не застрял без хода, но проданному летать незачем, а куплена
    /// она была вместе с корпусом. Иначе «раздеть, потом продать» давало бы за тот же корабль больше.
    ///
    /// Репутация цену выкупа не двигает — как и при продаже модулей (<see cref="SellItem"/>).
    /// </summary>
    public void SellHull(IClientConnection connection, string? hullId)
    {
        if (hullId is null || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        // Гость, полёт, чужой корпус и тот, под которым сидят, — молча: таких кнопок клиент не рисует.
        // IsGuest первым: OwnsHull у гостя отвечает «своё» на что угодно.
        if (player.IsGuest || !player.Docked || !player.OwnsHull(hullId) || hullId == player.HullId) return;
        // Стартовый корабль заводится заново при каждом входе (Player.Hulls), так что продажа отменилась бы
        // сама собой — а дай кто-нибудь «light» ненулевую цену, это стало бы печатным станком.
        if (hullId == SimConfig.DefaultHull)
        {
            connection.Send(new NoticeMsg(Protocol.StarterHullNotice));
            return;
        }
        if (PlaceOf(player) is not { } here || !here.Shipyard)
        {
            connection.Send(new NoticeMsg(Protocol.NoShipyardNotice));
            return;
        }
        if (player.HullPlaces.GetValueOrDefault(hullId) != here.Key)
        {
            connection.Send(new NoticeMsg(Protocol.ShipElsewhereNotice));
            return;
        }
        var fit = player.HullFits.GetValueOrDefault(hullId, Fitting.Empty);
        var credits = ShopOf(player).SellShipPrice(hullId, fit.Items().Select(item => item.Id));
        player.Credits += credits;
        player.Hulls.Remove(hullId);
        player.HullPlaces.Remove(hullId);
        player.HullFits.Remove(hullId);
        SendCargo(player);
        SendHangar(player);
        Save(player);
        _log.LogInformation("Player {Id} sold hull {Hull} with its fit for {Credits} credits", player.Id, hullId, credits);
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
            // Предметы, которых больше нет в балансе, пропадают и со склада…
            foreach (var id in player.Storage.Keys.Where(id => !IsItem(id)).ToList()) player.Storage.Remove(id);
            // …и из трюма: иначе такой груз весит 0, стоит 0 и не продаётся — невидимый неудаляемый хлам.
            foreach (var id in player.Cargo.Items.Keys.Where(id => !balance.Loot.Knows(id)).ToList())
                player.Cargo.Remove(id, player.Cargo.Count(id));
            var removed = new List<string>();
            player.Fit = Fitting.Refit(player.Hull(Hulls), player.Fit, balance.Weapons, balance.Modules, removed);
            if (!player.IsGuest) foreach (var id in removed) player.Store(id);
            player.Rescale(from, player.Effective(balance));
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
            // Конвой задания переживает правку: он доигрывает со старыми параметрами типа, зато задание
            // не срывается из-за того, что кто-то поправил shared/ во время плейтеста (M14).
            foreach (var trader in _traders.Where(t => t.MissionId == 0).ToList())
            {
                RemoveShip(trader);
                _traders.Remove(trader);
            }
            StartTraders();
        }
        ValidateRuns();

        if (!old.Loot.ContainerList.SequenceEqual(balance.Loot.ContainerList)) _loot.SetContainers(balance.Loot.ContainerList);
        if (_loot.DropUnknown(balance.Loot)) ClearMissingLootTargets();
        // Склад места переживает правку market.json: сохраняется не запас, а его отклонение от нормы.
        RebaseMarkets(old, balance);

        var message = Protocol.Encode(new ConfigMsg(
            balance.Hulls, balance.Weapons, balance.Rules, balance.Npc, balance.Loot, balance.Meteors, balance.Economy,
            SystemInfo(), GalaxyInfo(balance.Galaxy), balance.Modules, balance.MarketSet, balance.ReputationSet,
            balance.Trade));
        foreach (var player in _players.Values)
        {
            player.Connection?.SendRaw(message);
            SendCargo(player); // объёмы предметов и ёмкость корпуса могли измениться
            SendHangar(player); // корпус, пушку или модуль могли убрать из баланса
            SendMissions(player); // шаги обучения и шаблоны доски
            if (player.Docked)
            {
                SendShop(player);  // ассортимент и цены места могли поехать вместе с балансом
                SendMarket(player); // цены станции могли поехать вместе с профилем
            }
            player.Rep.Scrub(balance.Galaxy); // системы могли пропасть из галактики — их очки больше ни к чему
            SendRep(player); // ступени, цены и гейт могли уехать вместе с reputation.json
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
            var input = drone.NextInput(Balance.Sun?.BurnRadius ?? 0);
            if (!drone.IsDead) Movement.Step(ref drone.Ship, input, drone.MoveHull(drone.Hull(Hulls), Tick), SimConfig.Dt);
        }
        // Уничтоженный пират не думает: иначе снова взял бы огонь, который Battle снял при смерти.
        ForgiveOffenders();
        foreach (var pirate in _pirates)
        {
            if (pirate.IsDead) continue;
            // До Think: он сбрасывает LastAttackerId, взяв обидчика на прицел, и выстрел остался бы незамеченным.
            // Заодно чинится старая дырка: раньше стрелявший по рейнджеру не становился нарушителем для остальных.
            if (pirate.Type.IsRanger && pirate.LastAttackerId != 0)
                Offend(pirate.LastAttackerId, Balance.Reputation.Event.RangerAttack, Protocol.RepRangerAttack);
            Scavenge(pirate);
            var before = pirate.TargetId;
            var chasing = pirate.TargetId;
            PirateBrain.Think(pirate, _ships, _pirates, Balance, Tick, _ai, _log, StationPosition, _offenders, _outlaws);
            // Пират, уже сидевший на хвосте у врага властей, отпускает его: иначе бой тянулся бы до поводка.
            if (!pirate.Type.IsRanger && chasing != 0 && _outlaws.Contains(chasing) && pirate.TargetId == chasing)
                pirate.TargetId = 0;
            if (pirate.Type.IsRanger && pirate.State == PirateState.Attack && pirate.TargetId != before &&
                _ships.GetValueOrDefault(pirate.TargetId) is Player { Connection: { } connection } && _offenders.ContainsKey(pirate.TargetId))
                connection.Send(new NoticeMsg(Protocol.RangersNotice));
            if (pirate.Gone) _gonePirates.Add(pirate);
            else Movement.Step(ref pirate.Ship, pirate.LastInput, pirate.MoveHull(pirate.Hull(Hulls), Tick), SimConfig.Dt);
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
                    Offend(trader.LastAttackerId, Balance.Reputation.Event.TraderAttack, Protocol.RepTraderAttack);
                    Distress(trader, trader.LastAttackerId, traders);
                }
                // По дороге он подбирает брошенный груз — как пират, только не сворачивая назад.
                Scavenge(trader, trader.ToStation ? station : (trader.DestX, trader.DestY));
                TraderBrain.Think(
                    trader, traders, Tick, Balance.Galaxy.JumpTicks, station, Balance.Loot.StationRange, heat, _ships, Balance.Npc.DropRange);
                if (trader.Gone) _goneTraders.Add(trader);
                else Movement.Step(ref trader.Ship, trader.LastInput, trader.MoveHull(trader.Hull(Hulls), Tick), SimConfig.Dt);
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
        _missiles.Step(Tick, _ships, Balance, _shots, CanSplash);
        _battle.Run(Tick, _ships, Balance, _shots, _kills, Spawn, CanAttack, Launch, _missiles, CanSplash);
        // Задания — до уборки налётчиков: погибший должен ещё найтись среди кораблей.
        foreach (var kill in _kills)
        {
            if (_players.GetValueOrDefault(kill.By) is { } killer) Credit(killer, _ships.GetValueOrDefault(kill.Id));
        }
        NoteInvasionDamage();
        foreach (var meteor in _meteors.Shatter(_loot, Balance.Loot, Tick)) RemoveShip(meteor);
        StepSos();
        StepMissions();
        RemoveGonePirates();
        StepRaids();
        RemoveGoneTraders();
        StepTraders();
        StepMarket();
        // Дроп после боя: предмет должен пролежать хотя бы тик, иначе игрок вплотную к убитому
        // увидит «ничего не выпало», а трюм молча пополнится.
        _spilled.Clear();
        foreach (var kill in _kills)
        {
            if (_players.GetValueOrDefault(kill.Id) is not { } dead) continue;
            _host?.Busy(dead, TradeCodes.Dead); // сбитому не до обмена: сделка снимается со стола сразу (M16b)
            if (!dead.Cargo.IsEmpty) _spilled.Add(dead);
        }
        _loot.DropFrom(_kills, _ships, Balance.Loot, Tick);
        StoryKills();
        foreach (var player in _spilled) LostCargo(player);
        // Подбор и продажа — по команде игрока, а не сами собой: см. Grab и Sell.
        if (_loot.Step(Tick, Balance.Loot)) ClearMissingLootTargets();
        InterruptJumps();
        StepJumps();
        MendDocked();

        // Дроны есть всегда: без игроков онлайн снапшот не нужен никому.
        if (_byConnection.Count > 0) SendSnapshot();
        _shots.Clear();
        _kills.Clear();
        _loot.ClearPicks();
    }

    /// <summary>
    /// Починка стоянкой (M15.7): у кого не хватило кредитов на ремонт, тот чинится сам — медленно и только
    /// пока стоит в доке и на связи. Пристыкованный корабль убран из _ships, и Battle его не видит, поэтому
    /// проход отдельный. Док шлём не каждый тик, а когда сменилось целое число корпуса: клиент видит
    /// округлённое, а SendHangar — это ещё и запись в профиль.
    /// </summary>
    private void MendDocked()
    {
        var rate = Balance.Rules.DockRepairPerTick;
        if (rate <= 0) return;
        foreach (var player in _players.Values)
        {
            if (!player.Docked || player.Connection is null) continue;
            var maxHp = player.MaxHp(player.Effective(Balance));
            if (player.Hp >= maxHp) continue;
            var before = Math.Ceiling(player.Hp);
            player.Hp = Math.Min(maxHp, player.Hp + rate);
            if (Math.Ceiling(player.Hp) <= before) continue;
            SendHangar(player);
            Save(player);
        }
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
        var hull = player.MoveHull(player.Effective(Balance), Tick);
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
        // Гибель гасит текущую злость NPC (M16a): возрождаться под тем же огнём, что тебя и убил, — тупик.
        // Репутация при этом остаётся, и в док врага системы по-прежнему не пустят.
        if (ship is Player dead) ForgetOffender(dead.Id);
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
        // Пилот возвращается разбитым (M15.7) — чинить корпус ему в доке; NPC как были, целыми.
        SpawnHere(ship, ship is Player ? Balance.Rules.DeathHullShare : 1);
        // Разбитый корпус — сразу в профиль: иначе гибель лечилась бы выходом до ближайшей записи.
        if (ship is Player broken) Save(broken);
    }

    /// <summary>
    /// Появление в этой системе: игрок — у своего места, с защитой (GDD §25). Дрон — у своего дома,
    /// пират — в логове; NPC без защиты.
    /// </summary>
    /// <param name="hullShare">
    /// Сколько корпуса дать: 1 — целый. Меньше единицы бывает только после гибели (M15.7) и приходит
    /// из <see cref="Spawn"/>; вход в игру и прибытие по прыжку корпус не трогают.
    /// </param>
    private void SpawnHere(ShipEntity ship, double hullShare = 1)
    {
        if (ship is Meteor) return; // разбитый камень не возвращается — его убирает MeteorSystem.Shatter
        var (x, y) = ship switch
        {
            Drone drone => drone.SpawnPoint,
            Pirate pirate => pirate.SpawnPoint,
            Player player => SpawnPoint(HomePlaceOf(player)),
            _ => SpawnPoint(),
        };
        ship.Ship = new ShipState { X = x, Y = y };
        ship.Revive(ship.Effective(Balance), ship is Player ? Tick + Balance.Rules.ProtectionTicks : 0, hullShare);
        if (ship is Pirate p) p.ResetAi();
    }

    /// <summary>
    /// Случайная точка в круге SpawnJitter вокруг спауна — корабли не появляются друг в друге. Спаун — у места
    /// с внешней стороны его орбиты, прочь от звезды.
    /// </summary>
    /// <param name="place">У какого места появиться; null — у главного места системы.</param>
    private (double X, double Y) SpawnPoint(PlaceDef? place = null)
    {
        var radius = Balance.Rules.SpawnJitter * Math.Sqrt(_jitter.NextDouble());
        var angle = _jitter.NextDouble() * 2 * Math.PI;
        var orbit = (place ?? Balance.DefaultPlace)?.Orbit ?? Balance.StationPath;
        var (x, y) = orbit.ToWorld(OrbitSeconds, SimConfig.SpawnX, SimConfig.SpawnY);
        return (x + radius * Math.Cos(angle), y + radius * Math.Sin(angle));
    }

    /// <summary>
    /// Место, где пилот появляется в этой комнате: его дом, если он здесь, иначе главное место системы.
    /// С M15 дом бывает и поселением, поэтому «у станции» больше не годится (в tau её и нет).
    /// </summary>
    private PlaceDef? HomePlaceOf(Player player) => Balance.Place(player.HomePlace) ?? Balance.DefaultPlace;

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

    /// <summary>
    /// Отметить нарушителя: рейнджеры идут на него <see cref="OffenderTicks"/>. Репутация снимается только
    /// при переходе «не был нарушителем → стал»: пока метка держится, очередное попадание — то же нападение,
    /// а не новое, иначе очки капали бы каждый тик, пока палец на гашетке.
    /// </summary>
    private void Offend(int id, double delta, string code)
    {
        var fresh = !_offenders.ContainsKey(id);
        _offenders[id] = Tick + OffenderTicks;
        if (fresh && _players.GetValueOrDefault(id) is { } player) AddSystemRep(player, delta, code);
    }

    /// <summary>
    /// Забыть нападение: метка обидчика снимается, и никто из NPC больше не держит этого пилота на прицеле (M16a).
    /// Репутацию это не трогает — она живёт своей жизнью и затухает по своим правилам. Зовётся при возрождении:
    /// с пилота, которого уже сбили, счёт снят, и снова стать целью он может только новым нападением.
    /// </summary>
    private void ForgetOffender(int id)
    {
        _offenders.Remove(id);
        foreach (var pirate in _pirates)
        {
            if (pirate.TargetId == id) pirate.TargetId = 0;
            if (pirate.Avenge == id) pirate.Avenge = 0;
        }
    }

    /// <summary>
    /// Обидчики, которых рейнджеры уже забыли, и корабли, которых больше нет в системе.
    /// Врагу системы репутация закрывает док и делает его своим для пиратов, но сама по себе огня не открывает
    /// (M16a): текущая злость рейнджеров — это метка <see cref="_offenders"/> за нападение, а не отношение.
    /// Пока полноценной системы пиратства нет, плохая репутация не должна означать вечную травлю
    /// после каждого возрождения.
    /// </summary>
    private void ForgiveOffenders()
    {
        _outlaws.Clear();
        foreach (var player in _players.Values)
        {
            if (player.IsDead || player.Docked || !IsEnemy(player)) continue;
            _outlaws.Add(player.Id);
        }
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
        for (var i = _traders.Count(t => t.MissionId == 0); i < traders.Count; i++) SpawnTrader(traders, midway: true);
        _nextTraderTick = Tick + traders.RespawnTicks;
    }

    /// <summary>Новый торговец, когда их меньше нормы и подошёл срок.</summary>
    private void StepTraders()
    {
        // Конвой задания считается отдельно: он не должен глушить обычный трафик системы (M14).
        if (Balance.Traders is not { } traders || Tick < _nextTraderTick ||
            _traders.Count(t => t.MissionId == 0) >= traders.Count)
            return;
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
        // To — система за вратами: по ней репутация знает, кого подвели, если конвой не дошёл (M13).
        var stops = new List<(double X, double Y, bool Station, string? To)>();
        if (Balance.HasStation)
        {
            var (sx, sy) = Balance.StationPath.ToWorld(OrbitSeconds, SimConfig.SpawnX, SimConfig.SpawnY);
            stops.Add((sx, sy, true, null));
        }
        var arrival = Balance.Galaxy.ArrivalOffset;
        foreach (var gate in Balance.SystemDef.GateList)
        {
            // Из врат — чуть ближе к центру, как игрок после прыжка.
            var r = Math.Sqrt(gate.X * gate.X + gate.Y * gate.Y);
            var k = r > arrival ? (r - arrival) / r : 1;
            stops.Add((gate.X * k, gate.Y * k, false, gate.To));
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
            Gate = to.To,
        };
        trader.Ship = new ShipState { X = x, Y = y, Rot = Math.Atan2(to.X - x, -(to.Y - y)) };
        trader.Revive(trader.Effective(Balance), 0);
        LoadTrader(trader, from.Station);
        _traders.Add(trader);
        _ships[trader.Id] = trader;
    }

    /// <summary>Сколько NPC пролетит за грузом в сторону от своей дороги.</summary>
    private const double ScavengeRange = 800;

    /// <summary>
    /// Корабль подбирает груз по дороге (GDD §31): подлетел — забрал, иначе намечает ближайший, что влезет
    /// в трюм. Куда ему при этом лететь, решает его же ИИ: система лута только отдаёт груз.
    /// </summary>
    /// <param name="hunting">Сейчас он вообще смотрит по сторонам: не в бою, не убегает, не уходит из системы.</param>
    /// <param name="anchorX">Якорь поводка: дальше leash от него за грузом не сворачивают.</param>
    private void Scavenge<T>(T ship, bool hunting, double anchorX, double anchorY, double leash, Func<LootDrop, bool>? allow = null)
        where T : ShipEntity, IScavenger
    {
        var loot = Balance.Loot;
        var capacity = ship.Hull(Hulls).Cargo;
        if (ship.LootId != 0 && _loot.TryScavenge(ship, ship.LootId, loot, capacity, ship.GrabRange(Balance)))
        {
            _log.LogInformation("{Ship} picked up loot, hold {Used}", ship, ship.Hold.Used(loot));
            ship.LootId = 0;
            ClearMissingLootTargets(); // пилот мог пометить этот же груз
        }
        var drop = hunting ? _loot.ScavengeTarget(ship, anchorX, anchorY, ScavengeRange, leash, capacity, loot, allow) : null;
        ship.LootId = drop?.Id ?? 0;
        if (drop is not null) (ship.LootX, ship.LootY) = (drop.X, drop.Y);
    }

    /// <summary>
    /// Пират или рейнджер на патруле: якорь — логово или пост, поводок тот же, что у самого патруля.
    /// В бою, в пути и налётчик, уже уходящий из системы, за грузом не летают.
    /// </summary>
    private void Scavenge(Pirate pirate) => Scavenge(
        pirate,
        pirate.State == PirateState.Patrol,
        pirate.HomeX,
        pirate.HomeY,
        Balance.Npc.PatrolRadius + ScavengeRange);

    /// <summary>Насколько торговец согласен удлинить рейс ради находки: крюк больше этого — уже не «по пути».</summary>
    private const double TraderDetour = 300;

    /// <summary>
    /// Торговец подбирает то, что лежит по дороге. «По дороге» здесь настоящее: крюк «до груза и от него
    /// до цели» должен быть длиннее прямого пути не больше чем на <see cref="TraderDetour"/>. Одного поводка
    /// мало — с ним он сворачивал бы на 800 вбок, а это уже не попутная находка, а рейс за ней.
    ///
    /// Конвой задания (M14) не подбирает ничего: у него работа, а игрок обязан держаться рядом —
    /// крюк за грузом сорвал бы ему сопровождение.
    /// </summary>
    /// <param name="dest">Куда он летит сейчас: станция ходит по орбите, и её точка своя в каждом тике.</param>
    private void Scavenge(Trader trader, (double X, double Y) dest)
    {
        var toDest = Hypot(dest.X - trader.Ship.X, dest.Y - trader.Ship.Y);
        var hunting = trader.MissionId == 0 && !trader.Fleeing && !trader.InDistress &&
            trader.LeaveAtTick == 0 && toDest > ScavengeRange;
        Scavenge(trader, hunting, dest.X, dest.Y, toDest, drop =>
            Hypot(drop.X - trader.Ship.X, drop.Y - trader.Ship.Y) +
            Hypot(dest.X - drop.X, dest.Y - drop.Y) <= toDest + TraderDetour);
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

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
                AddSystemRep(player, Balance.Reputation.Event.SosHelp, Protocol.RepSos);
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
            // Довёз поставку — только живой: сбитый конвой до склада не добрался, и товар остаётся дорогим.
            if (!trader.IsDead) DeliverTrader(trader);
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
    /// Смена корпуса. С M20 оснащение не переезжает: прежний корабль остаётся в ангаре таким, каким
    /// его оставили, а новый берёт своё — то, что на нём стояло, когда с него сходили. У только что
    /// купленного не стояло ничего, и <see cref="Fitting.Refit"/> добивает обязательные слоты стартовыми
    /// модулями: новый корабль всегда может взлететь, а хорошее железо пилот переставляет сам.
    /// Гостю оснащение по-прежнему переезжает: склада у него нет, и снятое было бы некуда положить.
    /// </summary>
    private void ChangeHull(Player player, string hullId)
    {
        if (player.HullId == hullId) return;
        // Единственное место, где меняется корабль под пилотом, — здесь же и бухгалтерия ангара (M15.6):
        // тот, из которого вышли, остаётся стоять в этом месте; тот, в который сели, больше нигде не стоит.
        if (PlaceOf(player) is { } here) player.HullPlaces[player.HullId] = here.Key;
        player.HullPlaces.Remove(hullId);
        var from = player.Effective(Balance);
        var fit = player.Fit;
        if (!player.IsGuest)
        {
            player.HullFits[player.HullId] = player.Fit;
            fit = player.HullFits.Remove(hullId, out var saved) ? saved : Fitting.Empty;
        }
        player.HullId = hullId;
        var removed = new List<string>();
        player.Fit = Fitting.Refit(player.Hull(Hulls), fit, Balance.Weapons, Balance.Modules, removed);
        if (!player.IsGuest) foreach (var id in removed) player.Store(id);
        player.Rescale(from, player.Effective(Balance));
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

    /// <summary>
    /// Кого задевает взрыв площадного оружия (M15.5). Строже <see cref="CanAttack"/>: осколки — не прицельный
    /// огонь, и случайно испортить ими репутацию нельзя. Мирные не задеваются никогда, даже там, где PvP свободен, —
    /// иначе площадью нельзя было бы пользоваться в бою рядом со станцией. Заметьте: CanAttack мирных не защищает,
    /// у него «не игрок против не игрока» пропускает всё.
    /// </summary>
    private bool CanSplash(ShipEntity shooter, ShipEntity ship)
    {
        // Камни осколками не бьём: иначе площадное оружие стало бы лучшим способом чистить пояс,
        // а «охота на метеориты» (M14) требует именно расстрела. И один залп сыпал бы минералы горстями.
        if (ship is Meteor) return false;
        if (ship.IsDead || ship.IsProtected(Tick)) return false;

        if (shooter is Player player)
        {
            // Попал в пирата вплотную к торговцу — торговец цел: ни урона, ни обиды, ни рейнджеров.
            if (ship is Trader or Pirate { Type.IsRanger: true }) return false;
            if (ship is not Player other) return true; // пираты и учебные дроны
            if (other.Id == player.Id) return false;
            if (_host?.SameParty(player.Id, other.Id) == true) return false;
            // Строже прямого огня и намеренно: по выключившему PvP прицельно попасть можно, осколками — нет.
            if (!player.PvpOn || !other.PvpOn) return false;
            return CanAttack(player, other);
        }

        // NPC по своим не бьют: иначе волна вторжения молча съедала бы сама себя.
        if (shooter is Pirate { Type.IsPirate: true } && ship is Pirate { Type.IsPirate: true }) return false;
        if (shooter is Pirate { Type.IsRanger: true } && ship is Trader or Pirate { Type.IsRanger: true }) return false;
        if (shooter is Trader && ship is Trader or Pirate { Type.IsRanger: true }) return false;
        return true;
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
            bool Visible(double x, double y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= range2;
            // Груз виден дальше кораблей, если есть чем смотреть (M19): особенность «Циркуля» или сканер.
            // Это про контейнеры и обломки, а не про корабли: пиратов из-за угла скан не показывает.
            var scan = player.ScanRange(Balance);
            var scan2 = scan * scan;
            var frame = scan > range
                ? player.View.Encode(world, player.Id, Visible, (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= scan2)
                : player.View.Encode(world, player.Id, Visible);
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
            },
            ship.IsSlowed(Tick) ? ship.SlowUntilTick : 0);
    }

    /// <summary>Состояния ИИ в снапшоте — по индексу <see cref="PirateState"/>.</summary>
    private static readonly string[] AiNames = ["patrol", "attack", "return", "leave"];

    private WelcomeMsg Welcome(Player player, bool resumed) =>
        new(player.Id, SimConfig.TickRate, Protocol.Version, Hulls, Balance.Weapons, Balance.Rules, resumed,
            Balance.Npc, Balance.Loot, Balance.Meteors, Balance.Economy, SystemInfo(), GalaxyInfo(Balance.Galaxy), Balance.Modules,
            Balance.MarketSet, Balance.ReputationSet, Balance.Trade);

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
            [.. system.GateList.Select(g => new GateDto(g.To, galaxy.System(g.To)?.Name ?? g.To, g.X, g.Y))],
            Balance.Sun,
            Balance.StationPath,
            Balance.GalaxySet is null ? [] : system.PlanetList,
            OrbitEpoch,
            Balance.Raids?.Base,
            system.StationSprite,
            system.DockScene,
            system.Region);
    }

    /// <summary>Карта галактики (GDD §55): все системы и маршруты с ценой прыжка.</summary>
    public static GalaxyDto GalaxyInfo(GalaxyRules galaxy) => new(
        [.. galaxy.SystemMap.Select(kv => new GalaxySystemDto(
            kv.Key, kv.Value.Name, kv.Value.Danger, kv.Value.Pvp, kv.Value.Station,
            kv.Value.Map?.X ?? 0, kv.Value.Map?.Y ?? 0, kv.Value.Region,
            [.. kv.Value.Places(kv.Key, 0).Select(p => new PlaceNameDto(p.Key, p.Name))],
            [.. kv.Value.GateList.Select(g => g.To)]))],
        [.. galaxy.LinkList.Select(l => new LinkDto(l.A, l.B))],
        galaxy.Regions is null ? null : [.. galaxy.RegionMap.Select(kv => new RegionDto(kv.Key, kv.Value.Name, kv.Value.Color))]);

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
        if (RunOf(player) is not null) Fail(player, Protocol.LeftFail);
        StoryEnd(player);
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

        switch (_loot.TryGrab(
            player, player.SelectedLootId, Balance.Loot, player.Effective(Balance).Cargo, player.GrabRange(Balance), Tick,
            out var taken))
        {
            case LootSystem.GrabResult.Taken:
                player.SelectedLootId = 0;
                SendCargo(player);
                SendHangar(player); // подобранное снаряжение попадает на склад
                Save(player);
                if (!Advance(player, new TutorialEvent(MissionRules.GrabStep))) SendCollect(player);
                // Сюжет узнаёт о подборе здесь: по нему приходит звено в шестой миссии и открывается выбор.
                if (taken is not null) StoryPicked(player, taken);
                break;
            case LootSystem.GrabResult.NotYours:
                player.SelectedLootId = 0;
                connection.Send(new NoticeMsg(Protocol.NotYoursNotice));
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
    /// <param name="count">Сколько штук; 0 — вся стопка (а без item — весь трюм, что станция берёт).</param>
    public void Sell(IClientConnection connection, string? item, int count = 0)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player)) return;
        var loot = Balance.Loot;
        if (!loot.StationUnload || player.Cargo.IsEmpty) return;
        if (!player.Docked)
        {
            connection.Send(new NoticeMsg(Protocol.TooFarNotice));
            return;
        }

        var credits = 0;
        var sold = 0;
        // Что именно ушло — шаг обучения может ждать конкретный товар (M18).
        var goods = new List<string>();
        if (item is null)
        {
            // Весь трюм — каждый груз по здешней цене; чем тут не торгуют, то остаётся в трюме.
            foreach (var (id, have) in player.Cargo.Items.ToList())
            {
                // Снаряжение в трюме не товар: как груз оно стоит 0, и «продать всё» уничтожило бы
                // трофей задаром. В доке оно и так уезжает на склад (StoreGear).
                if (Balance.Loot.IsGear(id) || !Trades(player, id)) continue;
                credits += SellToStation(player, id, have);
                player.Cargo.Remove(id, have);
                sold += have;
                goods.Add(id);
            }
        }
        else
        {
            // Наличие проверяем до изъятия: иначе бесценный груз пропал бы, не принеся кредитов.
            var have = player.Cargo.Count(item);
            if (have <= 0) return;
            if (!Trades(player, item))
            {
                connection.Send(new NoticeMsg(Protocol.NoGoodsNotice));
                return;
            }
            sold = count <= 0 ? have : Math.Min(count, have);
            credits = SellToStation(player, item, sold);
            player.Cargo.Remove(item, sold);
            goods.Add(item);
        }
        if (sold <= 0)
        {
            connection.Send(new NoticeMsg(Protocol.NoGoodsNotice));
            return;
        }

        player.Credits += credits;
        player.CargoFullUntilTick = 0;
        connection.Send(new NoticeMsg(Protocol.UnloadedNotice));
        SendCargo(player);
        BroadcastMarket();
        Save(player);
        _log.LogInformation("Player {Id} sold {Count} cargo for {Credits} credits", player.Id, sold, credits);
        // Any останавливается на первом засчитанном: один шаг за одну продажу.
        if (!goods.Any(id => Advance(player, new TutorialEvent(MissionRules.SellStep, player.DockedPlace, id))))
            SendCollect(player);
    }

    /// <summary>
    /// Выбросить стопку груза за борт (M15.1). Только в полёте: груз ложится в космос рядом с кораблём,
    /// и подобрать его может кто угодно, включая самого пилота, — выброшенное не уничтожается.
    /// Количество не спрашивается: стопка целиком. Половинки — это ещё одно поле в сообщении и окно
    /// с цифрами ради случая, которого в полёте не бывает: место освобождают, когда его не хватает.
    /// Груз задания не выбрасывается — он вообще не предмет, а забронированный объём (см. Cargo.Reserved).
    /// </summary>
    public void Jettison(IClientConnection connection, string? item)
    {
        if (item is null || !_byConnection.TryGetValue(connection.Id, out var player) || player.IsDead) return;
        if (player.Docked)
        {
            // В доке за борт бросать некуда, да и незачем: там за это же дают кредиты.
            connection.Send(new NoticeMsg(Protocol.TooFarNotice));
            return;
        }
        // Сюжетный предмет за борт не летит (M20a): выбросить улику — это провалить цепочку молча,
        // и восстановить её было бы нечем.
        if (Balance.Loot.IsStory(item))
        {
            connection.Send(new NoticeMsg(Protocol.StoryItemNotice));
            return;
        }
        var count = player.Cargo.Count(item);
        if (count <= 0 || !player.Cargo.Remove(item, count)) return;
        _loot.SpillOne(Balance.Loot, item, count, player.Ship.X, player.Ship.Y, player.Ship.Vx, player.Ship.Vy, Tick);
        player.CargoFullUntilTick = 0;
        connection.Send(new NoticeMsg(Protocol.JettisonedNotice));
        SendCargo(player);
        SendCollect(player); // «собрать» считает по трюму: выбросил — счёт упал
        Save(player);
        _log.LogInformation("Player {Id} jettisoned {Count} {Item}", player.Id, count, item);
    }

    /// <summary>
    /// Обмен между игроками (M16b): всё проверяется до единой записи, и половина сделки не проходит никогда.
    /// Сессию держит <see cref="Galaxy"/>, а исполняется обмен здесь: оба по условию рядом, то есть в этой
    /// комнате, и только у неё есть баланс, трюм, счёт заданий и сохранение профиля.
    /// Резервировать заранее нечего: трюм до последнего принадлежит хозяину, а страж — эта самая проверка.
    /// </summary>
    /// <returns>Код отказа (<see cref="TradeCodes"/>) или null — обмен состоялся.</returns>
    public string? Swap(Player a, Player b, TradeOffer offerA, TradeOffer offerB)
    {
        if (Pilot(a.Id) != a || Pilot(b.Id) != b) return TradeCodes.Gone;
        if (a.IsDead || b.IsDead) return TradeCodes.Dead;
        if (a.Docked || b.Docked) return TradeCodes.Docked;
        var range = Balance.Trade.Range;
        if (Hypot(a.Ship.X - b.Ship.X, a.Ship.Y - b.Ship.Y) > range) return TradeCodes.TooFar;
        if (a.Credits < offerA.Credits || b.Credits < offerB.Credits) return TradeCodes.NoCredits;
        if (!Holds(a, offerA) || !Holds(b, offerB)) return TradeCodes.NoItems;
        if (!Room(a, offerA, offerB) || !Room(b, offerB, offerA)) return TradeCodes.NoRoom;

        Hand(a, b, offerA);
        Hand(b, a, offerB);
        foreach (var player in new[] { a, b })
        {
            player.CargoFullUntilTick = 0;
            SendCargo(player);
            SendCollect(player); // «собрать» считает по трюму: отдал груз — счёт упал, принял — вырос
            Save(player);
        }
        _log.LogInformation("Players {A} and {B} traded", a.Id, b.Id);
        return null;

        bool Holds(Player player, TradeOffer offer) =>
            offer.Items.All(kv => player.Cargo.Count(kv.Key) >= kv.Value);

        // Объём считается нетто: отдал две руды и взял две руды — место не кончилось.
        bool Room(Player player, TradeOffer gives, TradeOffer takes)
        {
            var loot = Balance.Loot;
            var used = player.Cargo.Used(loot);
            foreach (var (item, count) in gives.Items) used -= loot.Volume(item) * count;
            foreach (var (item, count) in takes.Items) used += loot.Volume(item) * count;
            return used <= player.Effective(Balance).Cargo;
        }

        static void Hand(Player from, Player to, TradeOffer offer)
        {
            from.Credits -= offer.Credits;
            to.Credits += offer.Credits;
            foreach (var (item, count) in offer.Items)
            {
                from.Cargo.Remove(item, count);
                to.Cargo.Add(item, count);
            }
        }
    }

    /// <summary>
    /// Стыковка и посадка (GDD §26, M15): в круге места корабль уходит в док — из космоса, из прицелов
    /// и с пути метеоритов. Место — станция или поселение на планете; для комнаты разницы нет.
    /// Вылет — там же, где вставали, стоя на месте и с защитой, как после появления (§25).
    /// </summary>
    /// <param name="place">Куда именно вставать; null — ближайшее подходящее (станция вперёд планет).</param>
    public void Dock(IClientConnection connection, bool on, string? place = null)
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
            if (PlaceAt(player, place) is not { } target)
            {
                connection.Send(new NoticeMsg(Protocol.TooFarNotice));
                return;
            }
            // Врагу здесь не открывают шлюз: ни торговли, ни ремонта, ни заданий, пока не исправится.
            if (IsEnemy(player))
            {
                connection.Send(new NoticeMsg(Protocol.DockClosedNotice));
                return;
            }
            player.Docked = true;
            player.DockedPlace = target.Key;
            player.DockOffset = target.Orbit.ToLocal(OrbitSeconds, player.Ship.X, player.Ship.Y);
            player.Ship.Vx = player.Ship.Vy = 0;
            _host?.Busy(player, TradeCodes.Docked); // из дока не меняются: обмен — дело двоих в космосе (M16b)
            player.FireHeld = false;
            player.TargetId = 0;
            player.SelectedLootId = 0;
            player.JumpTo = null;
            // Последнее место — дом: здесь пилот появится после гибели и после входа в игру.
            player.Home = SystemId;
            player.HomePlace = target.Key;
            StoreGear(player);
            RemoveShip(player);
            Save(player);
            _log.LogInformation("Player {Id} docked at {Place} in {System}", player.Id, target.Key, SystemId);
            // Груз доставки сдаётся сам, стоит пристыковаться к нужной станции.
            MakeRumours(player); // что здесь рассказывают — услышано один раз, на входе
            SendShop(player);   // витрина места: в поселении она не та, что на орбитальной станции
            SendMarket(player); // цены места нужны сразу: с ними открывается вкладка рынка
            SendRep(player); // и отношение: от него цены на витрине и что вообще выложат
            // Доска — тоже дело места (M15). До планет она была одна на систему и на стыковке не менялась;
            // теперь у станции и у поселения под ней работа разная, и прислать её надо здесь.
            SendMissions(player);
            // Ушёл в док, бросив конвой посреди системы, — это и есть «отстал» (M14).
            if (RunOf(player)?.Kind == MissionRules.EscortKind) Fail(player, Protocol.AwayFail);
            // Груз доставки и письмо сдаются сами, стоит встать в нужном месте. Именно в месте, а не
            // в системе: с M15 их в системе несколько, и доставка на планету не засчитывается на станции.
            if (player.Missions.Active?.Offer is { Kind: MissionRules.DeliverKind or MissionRules.CourierKind } errand &&
                errand.Destination == target.Key)
                Complete(player);
        }
        else
        {
            // Место могло исчезнуть из баланса, пока пилот стоял, — тогда вылет от главного места системы.
            var from = PlaceOf(player)?.Orbit ?? Balance.DefaultPlace?.Orbit ?? Balance.StationPath;
            player.Docked = false;
            player.DockedPlace = null;
            player.Rumours = []; // услышанное осталось в том месте
            // Место ушло по орбите, пока пилот был в доке, — вылет с той же его стороны.
            (player.Ship.X, player.Ship.Y) = from.ToWorld(OrbitSeconds, player.DockOffset.X, player.DockOffset.Y);
            player.ResetInputs();
            player.ProtectedUntilTick = Tick + Balance.Rules.ProtectionTicks;
            _ships[player.Id] = player;
            _log.LogInformation("Player {Id} undocked", player.Id);
        }
        SendHangar(player);
        if (on) return;
        // Шаг «остановиться» (M18) начинается заново с каждым вылетом: разгон и остановка — уже в космосе.
        player.Missions.Moved = false;
        player.Missions.StillSince = null;
        Advance(player, new TutorialEvent(MissionRules.UndockStep));
        StartRun(player); // конвой и звено выходят вместе с пилотом, а не ждут его в космосе (M14)
        StoryUndock(player); // сюжет раскладывает ящики и высылает тех, кто ждёт именно вылета (M20a)
    }

    /// <summary>
    /// Снятое с обломков снаряжение переезжает из трюма на склад. До дока трофей — обычный груз: занимает
    /// место и высыпается в космос вместе с остальным, если пилота сбили. Довёз — значит твоё.
    /// </summary>
    private void StoreGear(Player player)
    {
        var moved = player.Cargo.Items.Where(p => Balance.Loot.IsGear(p.Key)).ToList();
        if (moved.Count == 0) return;
        foreach (var (id, count) in moved)
        {
            player.Cargo.Remove(id, count);
            player.Store(id, count);
        }
        SendCargo(player);
    }

    /// <summary>
    /// Купить в доке (GDD §26, §30). Корпус — один раз, сразу ставится. Пушку или модуль — сколько угодно: со slot —
    /// сразу в этот слот (старое — на склад; не встаёт по классу или энергии — покупки нет), без slot — в первый
    /// свободный подходящий слот, а если такого нет — на склад.
    /// </summary>
    public void Buy(IClientConnection connection, string? kind, string? id, string? slot = null)
    {
        if (id is null || !_byConnection.TryGetValue(connection.Id, out var player) || !player.Docked) return;
        // Корпус продают только там, где есть верфь: в поселении без неё его негде собирать (M15).
        if (kind == Protocol.HullItem && PlaceOf(player) is { Shipyard: false })
        {
            connection.Send(new NoticeMsg(Protocol.NoShipyardNotice));
            return;
        }
        var shop = ShopOf(player);
        var price = kind switch
        {
            Protocol.HullItem when Hulls.ContainsKey(id) && !player.OwnsHull(id) => shop.SellsHull(id) ? shop.HullPrice(id) : null,
            Protocol.ItemKind when IsItem(id) => shop.SellsItem(id) ? shop.ItemPrice(id) : null,
            _ => null,
        };
        if (price is not { } listed)
        {
            // Здесь этого нет: за крейсером и Mk3 надо лететь на Рубеж (M11).
            if (kind == Protocol.HullItem ? Hulls.ContainsKey(id) && !player.OwnsHull(id) : IsItem(id))
                connection.Send(new NoticeMsg(Protocol.NotSoldNotice));
            return;
        }
        // Товар на витрине есть, но не для всякого: Mk3 и топовые корпуса продают только своим (M13).
        var rep = Balance.Reputation;
        if (!rep.Allows(GateRep(player), id, hull: kind == Protocol.HullItem))
        {
            connection.Send(new NoticeMsg(Protocol.NeedRepNotice));
            return;
        }
        // Цена — по станции: сюда регион не вмешивается, иначе штраф растворялся бы в среднем по соседям.
        var cost = rep.Price(listed, PlaceRep(player));
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
            : Balance.Modules?.GetValueOrDefault(id) is { } module
                ? module.Slot == Fitting.UtilityKind ? Enumerable.Range(0, hull.UtilitySlots).Select(Fitting.UtilitySlot) : [module.Slot]
                : [];
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
        var shop = ShopOf(player);
        var cost = Balance.Reputation.Price(
            shop.RepairCost(maxHp - player.Hp, maxHp, shop.HullPrice(player.HullId) ?? 0),
            PlaceRep(player));
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

    /// <summary>Где место стоит сейчас: станция и планеты ходят по орбитам вокруг звезды.</summary>
    public (double X, double Y) PlacePosition(PlaceDef place) => place.At(OrbitSeconds);

    /// <summary>
    /// Место, где стоит пилот; null — он в космосе или его место убрала горячая правка.
    /// По нему идут витрина, рынок, доска и репутация: с M15 их в системе несколько.
    /// </summary>
    public PlaceDef? PlaceOf(Player player) => player.Docked ? Balance.Place(player.DockedPlace) : null;

    /// <summary>Витрина того места, где стоит пилот. Вне дока — общие правила без ассортимента.</summary>
    private ShopRules ShopOf(Player player) => Balance.ShopAt(PlaceOf(player)?.Key);

    /// <summary>
    /// Место, к которому пилот достаточно близко, чтобы встать. Ключ задан — проверяется только оно
    /// (клиент говорит, куда именно садится); без ключа берётся первое подходящее, а станция в списке первая.
    /// </summary>
    private PlaceDef? PlaceAt(Player player, string? key = null)
    {
        foreach (var place in Balance.Places)
        {
            if (key is not null && place.Key != key) continue;
            var (px, py) = PlacePosition(place);
            var dx = player.Ship.X - px;
            var dy = player.Ship.Y - py;
            if (dx * dx + dy * dy <= place.Range * place.Range) return place;
        }
        return null;
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
            player.Home ?? SystemId,
            (int)Math.Round(Fitting.Power(player.Fit, Balance.Weapons, Balance.Modules)),
            Balance.Modules is null ? 0 : (int)Math.Round(Fitting.Output(player.Fit, Balance.Modules)),
            player.IsGuest,
            PlaceOf(player) is { } place ? new PlaceDto(place.Key, place.Kind, place.Name, place.Scene, place.Shipyard) : null,
            player.HullPlaces.Count == 0 ? null : new SortedDictionary<string, string>(player.HullPlaces, StringComparer.Ordinal),
            player.HullFits.Count == 0 ? null : new SortedDictionary<string, ShipFit>(player.HullFits, StringComparer.Ordinal)));
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
        // Все аргументы по именам: полей много, среди них соседние строки и словари, и позиционный
        // вызов молча переживал бы перестановку или удаление поля профиля.
        _accounts.Save(player.AccountId, new AccountProfile(
            Credits: player.Credits,
            Hull: player.HullId,
            Weapon: player.WeaponId ?? "",
            Hulls: [.. player.Hulls.Order(StringComparer.Ordinal)],
            Weapons: [],
            Cargo: new Dictionary<string, int>(player.Cargo.Items),
            System: player.Home,
            // Номер шага (до M18) больше не пишется: шаг хранится по id, «done» — пройдено.
            TutorialStep: player.Missions.Tutorial ?? MissionLog.Finished,
            Mission: player.Missions.Active,
            MissionSeed: player.Missions.Seed,
            Fit: player.Fit,
            Storage: new SortedDictionary<string, int>(player.Storage, StringComparer.Ordinal),
            Reputation: SaveRep(player),
            RepAt: player.Rep.At,
            Place: player.HomePlace,
            Career: player.Career,
            Ships: player.HullPlaces.Count == 0 ? null : new SortedDictionary<string, string>(player.HullPlaces, StringComparer.Ordinal),
            Fits: player.HullFits.Count == 0 ? null : new SortedDictionary<string, ShipFit>(player.HullFits, StringComparer.Ordinal),
            Hp: player.Hp,
            Story: SaveStory(player)));
    }

    /// <summary>Очки в профиль: сперва догоняем их до «сейчас», иначе на диск уехало бы вчерашнее число.</summary>
    private Dictionary<string, double>? SaveRep(Player player)
    {
        player.Rep.Touch(NowSeconds, Balance.Reputation);
        var values = player.Rep.Values;
        return values.Count == 0 ? null : new Dictionary<string, double>(values, StringComparer.Ordinal);
    }

    /// <summary>Часы для репутации: она тает по реальному времени, а не по тикам комнаты.</summary>
    private static long NowSeconds => (long)Now();

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
                // Доска и сюжет — один список: иначе «Взять» в разделе «Сюжет» не нашло бы, что взять.
                var offer = OffersFor(player).FirstOrDefault(o => o.Id == id);
                if (offer is null)
                {
                    SendMissions(player); // доска успела смениться: пусть клиент увидит новую
                    return;
                }
                if (offer.Kind == MissionRules.DeliverKind && offer.Story is null &&
                    player.Cargo.Used(Balance.Loot) + offer.Count > player.Effective(Balance).Cargo)
                {
                    connection.Send(new NoticeMsg(Protocol.CargoFullNotice));
                    return;
                }
                // Срок письма идёт по стенным часам, а не по тикам: пилот уходит из игры, а гонец ждать не станет.
                log.Active = new ActiveMission(offer, Until: offer.Seconds > 0 ? NowSeconds + offer.Seconds : 0);
                // Сюжетный груз выдаётся здесь же; не влез — работа не берётся, и доска остаётся как была.
                if (offer.Story is { } story && !StoryAccept(player, story))
                {
                    log.Active = null;
                    return;
                }
                log.Seed++;
                player.Cargo.Reserved = Reserve(log.Active);
                _log.LogInformation("Player {Id} took a {Kind} mission for {Reward} credits", player.Id, offer.Kind, offer.Reward);
                // Последний шаг обучения (M18) — взять работу: трюм, задания и профиль Advance уже отправил.
                if (Advance(player, new TutorialEvent(MissionRules.BoardStep)))
                {
                    SendMarket(player);
                    return;
                }
                break;
            }
            case Protocol.AbandonMission:
                if (!Abandon(player)) return;
                break;
            case Protocol.CompleteMission:
            {
                if (log.Active?.Offer is not { Kind: MissionRules.CollectKind, Item: { } item } collect) return;
                if (!player.Docked)
                {
                    connection.Send(new NoticeMsg(Protocol.TooFarNotice));
                    return;
                }
                // Обычное «собрать» сдаётся в любом доке, сюжетное — там, где его ждут (M20a).
                if (collect.Story is not null && collect.Destination != player.DockedPlace)
                {
                    connection.Send(new NoticeMsg(Protocol.TooFarNotice));
                    return;
                }
                if (!player.Cargo.Remove(item, collect.Count)) return;
                Complete(player);
                return;
            }
            case Protocol.SkipTutorial:
                if (log.Tutorial is null) return;
                log.Tutorial = null;
                break;
            case Protocol.ChooseStory:
                // Ответ в сюжетном диалоге: всё, что за ним следует, делает RoomStory — там же и проверки.
                StoryChoose(player, id);
                return;
            default:
                return;
        }
        SendCargo(player);
        SendMissions(player);
        // Взял или сдал «собрать» — витрина рынка меняется: груз задания с неё запирается и отпирается (M16a).
        if (player.Docked) SendMarket(player);
        Save(player);
    }

    /// <summary>
    /// Случилось то, что закрывает текущий шаг обучения, — засчитываем. Шаги идут строго по порядку,
    /// и шаг с местом, товаром или системой (M18) ждёт именно их: «продать машины на верфи» не
    /// закроет «продайте машины на Ледяной Веге».
    /// </summary>
    /// <returns>true — засчитан: состояние заданий уже ушло клиенту.</returns>
    private bool Advance(Player player, TutorialEvent happened)
    {
        var rules = Balance.Missions;
        if (rules.Step(player.Career, player.Missions.Tutorial) is not { } step || !MissionRules.Matches(step, happened))
            return false;
        var next = rules.Next(player.Career, step.Id);
        var last = next is null;
        player.Missions.Tutorial = next;
        player.Missions.Moved = false;
        player.Missions.StillSince = null;
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
            Advance(player, new TutorialEvent(MissionRules.DroneStep));
            return;
        }
        // Сбить пирата (M18) — шаг рейнджера после прыжка; дальше пират идёт в счёт задания как обычно.
        if (victim is Pirate { Type.IsPirate: true })
            Advance(player, new TutorialEvent(MissionRules.KillStep, System: SystemId));
        // Охота на метеориты (M14). Разбившийся о корабль пилота попадает сюда наравне с расстрелянным
        // (M16a): MeteorSystem.Ram проставляет камню убийцу, и дальше путь у них общий — один камень,
        // один плюс к счёту, кто бы его ни доломал.
        if (victim is Meteor rock)
        {
            if (player.Missions.Active is not { Offer.Kind: MissionRules.HuntKind } hunt) return;
            var hunted = hunt.Offer;
            if (hunted.System != SystemId || (hunted.Size is { } size && size != rock.SizeId)) return;
            player.Missions.Active = hunt with { Progress = hunt.Progress + 1 };
            if (hunt.Progress + 1 >= hunted.Count)
            {
                Complete(player);
                return;
            }
            SendMissions(player);
            Save(player);
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
        EndRun(player.Id);
        player.Missions.Active = null;
        player.Missions.Seed++;
        player.Cargo.Reserved = 0;
        player.Credits += active.Offer.Reward;
        SendCargo(player);
        SendMissions(player, new MissionDoneDto(Protocol.MissionDone, active.Offer.Reward, Mission: active.Offer));
        Save(player);
        // Сюжетная миссия платит отношением сама и только одному месту (M20a): обычная пара «место плюс
        // система» на неё не годится — властям Новы за шестую миссию спасибо говорить не за что.
        if (active.Offer.Story is { } story)
        {
            StoryComplete(player, story);
            SendCargo(player);
            SendMissions(player);
            _log.LogInformation("Player {Id} completed story mission {Mission}", player.Id, story.Mission);
            return;
        }
        // Станции, которая ждала работу, — много; её системе — мало: система в основном штрафная шкала.
        // Ждала не всегда та, что выдала: письму платит получатель (M14).
        var events = Balance.Reputation.Event;
        AddRep(player, active.Offer.Payer, events.MissionPlace, Protocol.RepMissionDone);
        if (Balance.Galaxy.SystemOfPlace(active.Offer.Payer) is { } paid)
            AddRep(player, Reputation.System(paid), events.MissionSystem, Protocol.RepMissionDone);
        _log.LogInformation(
            "Player {Id} completed a {Kind} mission for {Reward} credits", player.Id, active.Offer.Kind, active.Offer.Reward);
    }

    /// <summary>
    /// Задание провалено (M14): награды нет, доска обновляется, место, которое его ждало, это запомнит.
    /// Провал дороже честного отказа: там пилот вернул работу, здесь потерял конвой, звено или письмо.
    /// Зовётся и тогда, когда задание проваливать нечем, — смотрит только на взятое.
    /// </summary>
    /// <param name="code">Причина (<see cref="Protocol.TraderFail"/> и прочие) — по ней клиент пишет строку.</param>
    private void Fail(Player player, string code)
    {
        if (player.Missions.Active is not { } active) return;
        EndRun(player.Id);
        player.Missions.Active = null;
        player.Missions.Seed++;
        player.Cargo.Reserved = 0;
        SendCargo(player);
        SendMissions(player, new MissionDoneDto(Protocol.MissionFailed, 0, Mission: active.Offer, Reason: code));
        Save(player);
        // Провал сюжетной миссии отношения не трогает: она и так вернулась на доску, а штрафовать пилота
        // за то, что его сбили по дороге к точке перелома, — значит наказывать за попытку пройти сюжет.
        if (active.Offer.Story is not null)
        {
            StoryDropped(player);
            _log.LogInformation("Player {Id} failed a story mission: {Code}", player.Id, code);
            return;
        }
        AddRep(player, active.Offer.Payer, Balance.Reputation.Event.MissionFail, Protocol.RepMissionFail);
        _log.LogInformation("Player {Id} failed a {Kind} mission: {Code}", player.Id, active.Offer.Kind, code);
    }

    /// <summary>
    /// Пилот отказался от задания. Бьёт по месту, выдавшему работу, а не по тому, где пилот сейчас:
    /// бросил — подвёл заказчика. Отказ дешевле провала: работу он вернул, а не потерял.
    /// </summary>
    /// <returns>false — отказываться было не от чего.</returns>
    private bool Abandon(Player player)
    {
        if (player.Missions.Active is not { } dropped) return false;
        EndRun(player.Id);
        player.Missions.Active = null;
        player.Cargo.Reserved = 0;
        if (dropped.Offer.Story is not null)
        {
            // Отказ от сюжета — это «не сейчас», а не «подвёл заказчика»: кампания ждёт на доске.
            StoryDropped(player);
            return true;
        }
        AddRep(player, dropped.Offer.From, Balance.Reputation.Event.MissionAbandon, Protocol.RepMissionAbandon);
        return true;
    }

    /// <summary>У «собрать» прогресс — сколько такого в трюме: трюм изменился — клиенту новый счёт.</summary>
    private void SendCollect(Player player)
    {
        if (player.Missions.Active?.Offer.Kind == MissionRules.CollectKind) SendMissions(player);
    }

    /// <summary>Сколько места в трюме держит задание: груз доставки.</summary>
    private int Reserve(ActiveMission? active)
    {
        if (active?.Offer is not { Kind: MissionRules.DeliverKind } deliver) return 0;
        // Сюжетная доставка везёт настоящий предмет (M20a), и он уже занимает трюм: бронировать
        // под него объём второй раз — значит отнять у пилота место дважды за один ящик.
        if (StoryMissionOf(deliver.Story) is { GiveList.Count: > 0 }) return 0;
        return deliver.Count;
    }

    /// <summary>Доска станции этой системы для пилота; без станции — пусто.</summary>
    private IReadOnlyList<MissionOffer> Board(Player player)
    {
        // Доска — дело места (M15): у станции и у поселения под ней работа своя, и репутация тоже.
        if (PlaceKeyOf(player) is not { } place) return [];
        var rep = Balance.Reputation;
        var round = Balance.Missions.Round(OrbitSeconds); // доска сменяется и сама, по часам (M15.1)
        if (!rep.Any) return Balance.Missions.Board(Balance, place, player.Missions.Seed, round: round);
        // Магазин и доска смотрят на одни и те же очки: имя, заработанное в регионе, открывает и работу.
        // Сколько работы доверить и давать ли особый контракт — решает место.
        var here = PlaceRep(player);
        return Balance.Missions.Board(
            Balance, place, player.Missions.Seed, rep.Offers(here, Balance.Missions.Offers), rep.Elite(here), rep.EliteReward,
            // Патруль рейнджеры доверяют не всякому — им важна система, а не место (M14).
            SystemRep(player),
            round);
    }

    /// <summary>Обучение и задания — личное дело пилота, как и трюм.</summary>
    private void SendMissions(Player player, MissionDoneDto? done = null)
    {
        if (player.Connection is null) return;
        var active = player.Missions.Active;
        if (active?.Offer is { Kind: MissionRules.CollectKind, Item: { } item })
            active = active with { Progress = Math.Min(active.Offer.Count, player.Cargo.Items.GetValueOrDefault(item)) };
        player.Connection.Send(new MissionsMsg(
            TutorialOf(player),
            active,
            OffersFor(player),
            done,
            MarkOf(player),
            StoryState(player)));
    }

    /// <summary>Трюм — личное дело игрока: снапшот один на всех, места для него там нет.</summary>
    private void SendCargo(Player player)
    {
        if (player.Connection is null) return;
        var loot = Balance.Loot;
        player.Connection.Send(new CargoMsg(
            player.Cargo.Used(loot),
            player.Effective(Balance).Cargo,
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
                // Звено патруля и конвой задания — свои корабли на экране: их должно быть видно среди прочих (M14).
                Pirate p => new PlayerDto(
                    p.Id, p.Name, Online: true, Npc: true,
                    Ceiling(p.MaxHp(p.Hull(Hulls))), Ceiling(p.MaxShield(p.Hull(Hulls))),
                    // Внешность у типа своя (M20b): повстанец — буксир, корабли корпорации — свой корпус.
                    // Без неё вся «Тихая война» летала бы на одном силуэте шахтёрского буксира.
                    p.Type.Look ?? (p.Type.IsRanger
                        ? p.MissionId != 0 ? Protocol.WingKind : Protocol.RangerKind
                        : p.Story ? Protocol.RebelKind : Protocol.PirateKind)),
                Trader t => new PlayerDto(
                    t.Id, t.Name, Online: true, Npc: true,
                    Ceiling(t.MaxHp(t.Hull(Hulls))), Ceiling(t.MaxShield(t.Hull(Hulls))),
                    t.Type.Look ?? (t.MissionId != 0 ? Protocol.ConvoyKind : Protocol.TraderKind)),
                _ => new PlayerDto(s.Id, s.Name, Online: true, Npc: true),
            })
            .ToList();
        var message = Protocol.Encode(new PlayersMsg(list, _host?.OnlineTotal ?? OnlineCount));

        foreach (var player in _players.Values) player.Connection?.SendRaw(message);
    }
}
