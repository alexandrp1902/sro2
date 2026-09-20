using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Учебный дрон (GDD §54): не стреляет, стоит на месте или кружит вокруг своей точки.</summary>
/// <param name="OrbitRadius">0 — стоит на месте.</param>
/// <param name="Throttle">Тяга на орбите, 0…1.</param>
/// <param name="Hp">Своя прочность вместо корпусной; null — как у корпуса.</param>
/// <param name="Shield">Свой щит вместо корпусного; null — как у корпуса.</param>
/// <param name="Table">Таблица лута из loot.json, что выпадает с дрона; null — ничего (обучение, GDD §54: «подобрать выпавший ресурс»).</param>
public sealed record DroneSpec(
    string Name,
    string Hull,
    double X,
    double Y,
    double OrbitRadius = 0,
    double Throttle = 1,
    double? Hp = null,
    double? Shield = null,
    string? Table = null)
{
    public string? Validate(IReadOnlyDictionary<string, HullParams> hulls)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (Hull is null || !hulls.ContainsKey(Hull)) return $"unknown hull '{Hull}'";
        if (!(Math.Abs(X) <= Movement.WorldHalfSize) || !(Math.Abs(Y) <= Movement.WorldHalfSize)) return "x and y must be inside the world";
        if (!(OrbitRadius >= 0)) return "orbitRadius must not be negative";
        if (!(Throttle >= 0 && Throttle <= 1)) return "throttle must be within 0..1";
        if (Hp is { } hp && !(hp > 0)) return "hp must be positive";
        if (Shield is { } shield && !(shield >= 0)) return "shield must not be negative";
        return null;
    }
}

/// <summary>Правила боя из shared/combat.json.</summary>
/// <param name="RespawnSeconds">Через столько уничтоженный корабль появляется снова (GDD §24 — 30 с; на плейтесте короче).</param>
/// <param name="ProtectionSeconds">Защита после появления; снимается раньше, если корабль выстрелил (§25).</param>
/// <param name="ShieldRegenDelay">Щит восстанавливается, если столько секунд не было урона (§17).</param>
/// <param name="SpawnJitter">Разброс точки появления — корабли не появляются друг в друге.</param>
/// <param name="RepairDelay">Ремонтный блок (M11) чинит корпус, если столько секунд не было урона.</param>
/// <param name="SectorUnit">
/// Сколько единиц мира в одном «секторе» — мере дистанции для игрока. Примерно дальность пушки по умолчанию
/// и половина экрана телефона: «цель в 1.4 сектора» читается лучше, чем «в 980».
/// </param>
public sealed record CombatRules(
    double RespawnSeconds = 10,
    double ProtectionSeconds = 10,
    double ShieldRegenDelay = 5,
    double SpawnJitter = 120,
    double SectorUnit = 700,
    IReadOnlyList<DroneSpec>? Drones = null,
    double RepairDelay = 6)
{
    [JsonIgnore] public int RepairDelayTicks => Combat.SecondsToTicks(RepairDelay);
    [JsonIgnore] public int RespawnTicks => Math.Max(1, Combat.SecondsToTicks(RespawnSeconds));
    [JsonIgnore] public int ProtectionTicks => Combat.SecondsToTicks(ProtectionSeconds);
    [JsonIgnore] public int ShieldRegenDelayTicks => Combat.SecondsToTicks(ShieldRegenDelay);
    [JsonIgnore] public IReadOnlyList<DroneSpec> DroneList => Drones ?? [];

    /// <param name="hulls">Корпуса дронов должны существовать.</param>
    public string? Validate(IReadOnlyDictionary<string, HullParams> hulls)
    {
        if (!(RespawnSeconds >= 0) || !(ProtectionSeconds >= 0) || !(ShieldRegenDelay >= 0))
            return "respawnSeconds, protectionSeconds and shieldRegenDelay must not be negative";
        if (!(SpawnJitter >= 0)) return "spawnJitter must not be negative";
        if (!(SectorUnit > 0)) return "sectorUnit must be positive";
        if (!(RepairDelay >= 0)) return "repairDelay must not be negative";
        for (var i = 0; i < DroneList.Count; i++)
        {
            var problem = DroneList[i] is null ? "is null" : DroneList[i].Validate(hulls);
            if (problem is not null) return $"drones[{i}]: {problem}";
        }
        return null;
    }

    public static bool TryParse(string json, IReadOnlyDictionary<string, HullParams> hulls, out CombatRules rules, out string? error)
    {
        rules = new CombatRules();
        CombatRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CombatRules>(json, JsonCatalog.Options);
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }
        if (parsed is null)
        {
            error = "no rules";
            return false;
        }
        error = parsed.Validate(hulls);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}

/// <summary>Тексты файлов баланса — по имени, а не по позиции: восемь соседних строк легко переставить и не заметить.</summary>
/// <param name="Galaxy">galaxy.json; null — одна система, раскладка из npcs.json, loot.json и combat.json.</param>
/// <param name="Missions">missions.json; null — заданий и обучения нет.</param>
/// <param name="Modules">modules.json; null — модулей нет: щит, радар и бак пилоту даёт корпус, как до M9.</param>
/// <param name="Party">party.json; null — группы по умолчанию.</param>
/// <param name="Invasion">invasion.json; null — вторжений нет.</param>
/// <param name="Market">market.json; null — рынка товаров нет, груз сдаётся по плоской цене loot.json, как до M12.</param>
/// <param name="Careers">careers.json; null — путей нет, и все новые пилоты одинаковы, как до M15.5.</param>
/// <param name="Demand">demand.json; null — событий спроса нет.</param>
public sealed record BalanceSources(
    string Hulls, string Weapons, string Rules, string Npcs, string Loot, string Meteors, string Shop,
    string? Galaxy = null, string? Missions = null, string? Modules = null, string? Party = null, string? Invasion = null,
    string? Market = null,
    string? Reputation = null,
    string? Careers = null,
    string? Demand = null);

/// <summary>
/// Весь баланс: корпуса, пушки, правила боя, NPC, лут, метеориты, магазин станции, галактика, задания.
/// Меняется только целиком.
/// Комната системы получает свой вид баланса (<see cref="ForSystem"/>): логова, контейнеры и дроны — её собственные.
/// </summary>
/// <param name="Npcs">Пираты; null — NPC, кроме дронов, нет.</param>
/// <param name="Loots">Лут и трюм; null — добычи нет.</param>
/// <param name="MeteorSet">Метеориты; null — их нет.</param>
/// <param name="ShopSet">Магазин станции; null — ничего не продают, кредитов на старте нет.</param>
/// <param name="GalaxySet">Галактика; null — одна система <see cref="GalaxyRules.DefaultSystem"/> без врат.</param>
/// <param name="SystemId">Чей это вид баланса; null — стартовой системы.</param>
/// <param name="CoreRadius">
/// Метеориты пролетают не ближе этого к центру системы. Обычно — укрытие станции, но в системе без станции
/// укрытия нет, а звезда в центре никуда не делась.
/// </param>
/// <param name="MissionSet">Задания и обучение; null — их нет.</param>
/// <param name="Modules">Модули кораблей (GDD §13); null — их нет: щит, радар и бак пилоту даёт корпус, энергию не считают.</param>
/// <param name="PartySet">Группы игроков (GDD §37); null — по умолчанию.</param>
/// <param name="InvasionSet">Вторжения пиратов (GDD §38); null — их нет.</param>
/// <param name="MarketSet">Рынок товаров (M12); null — груз сдаётся по плоской цене loot.json и не покупается.</param>
/// <param name="ReputationSet">Репутация (M13); null — поступки не запоминаются, всё продаётся всем.</param>
/// <param name="CareerSet">Пути пилота (M15.5); null — новый пилот получает общий стартовый набор.</param>
/// <param name="DemandSet">События спроса (M15.5); null — их нет.</param>
/// <param name="PlaceList">
/// Места системы (M15): станция и поселения планет. Считается один раз в <see cref="ForSystem"/>,
/// а не на каждый запрос. Пусто — сесть в системе негде.
/// </param>
/// <param name="ShopByPlace">Магазин каждого места (M15); null — мест нет.</param>
/// <param name="MarketByPlace">Рынок каждого места (M15); null — рынка нет нигде.</param>
public sealed record Balance(
    IReadOnlyDictionary<string, HullParams> Hulls,
    IReadOnlyDictionary<string, WeaponParams> Weapons,
    CombatRules Rules,
    NpcRules? Npcs = null,
    LootRules? Loots = null,
    MeteorRules? MeteorSet = null,
    ShopRules? ShopSet = null,
    GalaxyRules? GalaxySet = null,
    string? SystemId = null,
    double? CoreRadius = null,
    MissionRules? MissionSet = null,
    IReadOnlyDictionary<string, ModuleParams>? Modules = null,
    PartyRules? PartySet = null,
    InvasionRules? InvasionSet = null,
    MarketRules? MarketSet = null,
    ReputationRules? ReputationSet = null,
    IReadOnlyList<PlaceDef>? PlaceList = null,
    IReadOnlyDictionary<string, ShopRules>? ShopByPlace = null,
    IReadOnlyDictionary<string, MarketRules>? MarketByPlace = null,
    CareerRules? CareerSet = null,
    DemandRules? DemandSet = null)
{
    public const string HullsFile = "hulls.json";
    public const string WeaponsFile = "weapons.json";
    public const string RulesFile = "combat.json";
    public const string NpcsFile = NpcRules.File;
    public const string LootFile = LootRules.File;
    public const string MeteorsFile = MeteorRules.File;
    public const string ShopFile = ShopRules.File;
    public const string GalaxyFile = GalaxyRules.File;
    public const string MissionsFile = MissionRules.File;
    public const string ModulesFile = ModuleCatalog.File;
    public const string PartyFile = PartyRules.File;
    public const string InvasionFile = InvasionRules.File;
    public const string MarketFile = MarketRules.File;
    public const string ReputationFile = ReputationRules.File;
    public const string CareersFile = CareerRules.File;
    public const string DemandFile = DemandRules.File;

    /// <summary>Все файлы баланса в порядке разбора.</summary>
    public static readonly string[] Files =
    [
        HullsFile, WeaponsFile, RulesFile, NpcsFile, LootFile, MeteorsFile, ShopFile, GalaxyFile, MissionsFile,
        ModulesFile, PartyFile, InvasionFile, MarketFile, ReputationFile, CareersFile, DemandFile,
    ];

    public NpcRules Npc => Npcs ?? NpcRules.None;

    public LootRules Loot => Loots ?? LootRules.None;

    public MeteorRules Meteors => MeteorSet ?? MeteorRules.None;

    /// <summary>
    /// Общее в магазине, одинаковое во всех местах: стартовые кредиты, доля выкупа, цена ремонта, тиры.
    /// За ценами и ассортиментом конкретного места — в <see cref="ShopAt"/>: с M15 их в системе несколько.
    /// </summary>
    public ShopRules Economy => ShopSet ?? ShopRules.None;

    public GalaxyRules Galaxy => GalaxySet ?? GalaxyRules.Single;

    public MissionRules Missions => MissionSet ?? MissionRules.None;

    public PartyRules Party => PartySet ?? PartyRules.Default;

    public InvasionRules Invasion => InvasionSet ?? InvasionRules.None;

    /// <summary>Пути пилота (M15.5); их нет — <see cref="CareerRules.Any"/> false, и старт один на всех, как до M15.5.</summary>
    public CareerRules Careers => CareerSet ?? CareerRules.None;

    /// <summary>События спроса (M15.5); их нет — <see cref="DemandRules.Any"/> false, и рынок живёт как до M15.5.</summary>
    public DemandRules Demand => DemandSet ?? DemandRules.None;

    /// <summary>
    /// Места системы (M15): станция и поселения планет. Считается в <see cref="ForSystem"/>; у баланса,
    /// собранного руками (тесты, система без galaxy.json), выводится из <see cref="SystemDef"/> —
    /// там это ровно одна станция в центре, как было до M15.
    /// </summary>
    public IReadOnlyList<PlaceDef> Places => PlaceList ?? SystemDef.Places(System, Loot.StationRange);

    /// <summary>В системе есть куда сесть. Не то же, что <see cref="HasStation"/>: у планеты свой док.</summary>
    public bool HasDock => Places.Count > 0;

    /// <summary>Место по ключу; null — такого здесь нет (например, его убрала горячая правка).</summary>
    public PlaceDef? Place(string? key) => key is null ? null : Places.FirstOrDefault(p => p.Key == key);

    /// <summary>
    /// Место системы по умолчанию: станция, а если её нет — первое поселение. Сюда попадает тот,
    /// у кого места ещё нет: новый пилот, гость и профиль старше M15.
    /// </summary>
    public PlaceDef? DefaultPlace => Places.Count > 0 ? Places[0] : null;

    /// <summary>
    /// Магазин места (M11, по местам — M15). У баланса без галактики мест нет — тогда это общий магазин,
    /// как было до M15; там, где места есть, незнакомый ключ не торгует ничем.
    /// </summary>
    public ShopRules ShopAt(string? key)
    {
        if (ShopByPlace is not { Count: > 0 } byPlace) return Economy;
        return key is not null && byPlace.TryGetValue(key, out var shop) ? shop : Economy with { Stock = [] };
    }

    /// <summary>
    /// Рынок места (M12, по местам — M15); рынка нет — <see cref="MarketRules.Any"/> false, цены плоские.
    /// Без галактики мест нет, и это общий рынок, как было до M15.
    /// </summary>
    public MarketRules MarketAt(string? key)
    {
        if (MarketByPlace is not { Count: > 0 } byPlace) return MarketSet ?? MarketRules.None;
        return key is not null && byPlace.TryGetValue(key, out var market) ? market : MarketRules.None;
    }

    /// <summary>
    /// Витрина главного места системы — то, что показывают по умолчанию. Комнате нужна не она,
    /// а витрина того места, где стоит пилот: см. <see cref="ShopAt"/>.
    /// </summary>
    public ShopRules MainShop => ShopAt(DefaultPlace?.Key);

    /// <summary>Рынок главного места системы; для пилота в доке — <see cref="MarketAt"/> его места.</summary>
    public MarketRules MainMarket => MarketAt(DefaultPlace?.Key);

    /// <summary>
    /// Репутация (M13); её нет — <see cref="ReputationRules.Any"/> false, и всё ведёт себя как до M13.
    /// По системам не сужается, в отличие от магазина и рынка: правила одни на галактику, разные только очки пилота.
    /// </summary>
    public ReputationRules Reputation => ReputationSet ?? ReputationRules.None;

    /// <summary>Система этого вида баланса.</summary>
    public string System => SystemId ?? Galaxy.StartSystem;

    public SystemDef SystemDef => Galaxy.System(System) ?? GalaxyRules.Lone(System);

    /// <summary>В системе есть станция: док, продажа, заправка и укрытие от пиратов.</summary>
    public bool HasStation => SystemDef.Station;

    /// <summary>Звезда в центре; null — её нет (одна система без galaxy.json).</summary>
    public SunDef? Sun => GalaxySet is null ? null : SystemDef.Sun;

    /// <summary>Налёты пиратов; null — только логова (и всегда так без galaxy.json).</summary>
    public RaidRules? Raids => GalaxySet is null ? null : SystemDef.Pirates;

    /// <summary>Торговцы; null — их нет (и всегда так без galaxy.json).</summary>
    public TraderRules? Traders => GalaxySet is null ? null : SystemDef.Traders;

    /// <summary>Орбита станции; у системы без galaxy.json станция стоит в центре.</summary>
    public OrbitDef StationPath => GalaxySet is null ? OrbitDef.Center : SystemDef.StationPath;

    /// <summary>См. параметр CoreRadius.</summary>
    public double MeteorCore => CoreRadius ?? Npc.StationSafeRadius;

    /// <summary>
    /// Вид баланса для комнаты системы: логова, контейнеры и дроны — из galaxy.json, метеориты — с множителем системы,
    /// укрытия от пиратов без станции нет. Без galaxy.json раскладка остаётся из npcs.json, loot.json и combat.json.
    /// </summary>
    public Balance ForSystem(string id)
    {
        if (GalaxySet is null) return this with { SystemId = id };
        var system = GalaxySet.System(id) ?? throw new ArgumentException($"unknown system '{id}'", nameof(id));
        return View(this, id, system) with { SystemId = id };
    }

    /// <summary>Все пушки и модули всех тиров.</summary>
    public IEnumerable<string> ItemIds => Weapons.Keys.Concat(Modules?.Keys ?? []);

    private static Balance View(Balance b, string id, SystemDef system)
    {
        var npc = b.Npc;
        var meteors = b.MeteorSet;
        if (meteors is not null && system.MeteorSizes is { } weights)
        {
            meteors = meteors with
            {
                Sizes = meteors.SizeMap.ToDictionary(p => p.Key, p => weights.TryGetValue(p.Key, out var w) ? p.Value with { Weight = w } : p.Value),
            };
        }
        var loot = b.Loot with { Containers = system.ContainerList };
        if (system.LootTables is { } aliases)
        {
            var tables = loot.TableMap.ToDictionary(p => p.Key, p => p.Value);
            foreach (var (type, table) in aliases) if (loot.TableMap.TryGetValue(table, out var found)) tables[type] = found;
            loot = loot with { Tables = tables };
        }
        if (meteors is not null && system.Meteors != 1)
        {
            meteors = system.Meteors <= 0 || meteors.MaxAlive <= 0
                ? meteors with { MaxAlive = 0 }
                : meteors with
                {
                    SpawnIntervalSeconds = meteors.SpawnIntervalSeconds / system.Meteors,
                    MaxAlive = Math.Max(1, (int)Math.Round(meteors.MaxAlive * system.Meteors)),
                };
        }
        // Места системы и их магазины считаются здесь, один раз на систему, а не при каждой покупке.
        var places = system.Places(id, loot.StationRange);
        var itemIds = b.ItemIds.ToList();
        var shops = new Dictionary<string, ShopRules>(StringComparer.Ordinal);
        var markets = new Dictionary<string, MarketRules>(StringComparer.Ordinal);
        foreach (var place in places)
        {
            if (b.ShopSet?.Local(place.Key, system.Region, itemIds) is { } shop) shops[place.Key] = shop;
            if (b.MarketSet?.Local(place.Key, system.Region) is { } market) markets[place.Key] = market;
        }
        var main = places.Count > 0 ? places[0].Key : null;
        return b with
        {
            Rules = b.Rules with { Drones = system.DroneList },
            Npcs = npc with { Spawns = system.SpawnList, StationSafeRadius = system.Station ? npc.StationSafeRadius : 0 },
            Loots = loot,
            MeteorSet = meteors,
            CoreRadius = npc.StationSafeRadius,
            PlaceList = places,
            ShopByPlace = shops,
            MarketByPlace = markets,
            // ShopSet и MarketSet остаются видом главного места: по ним едут общие правила и витрина в welcome.
            ShopSet = main is not null && shops.TryGetValue(main, out var mainShop) ? mainShop : b.ShopSet?.Local(id, system.Region, itemIds),
            MarketSet = main is not null ? markets.GetValueOrDefault(main) : null,
        };
    }

    /// <summary>Раскладка системы по правилам NPC, лута и боя: логова и контейнеры — не в укрытии станции, дроны — в мире.</summary>
    private static string? ValidateLayout(Balance b, string id, SystemDef system)
    {
        if (system.MeteorSizes is { } weights)
        {
            foreach (var size in weights.Keys) if (!b.Meteors.SizeMap.ContainsKey(size)) return $"meteorSizes: unknown size '{size}'";
        }
        if (system.LootTables is { } aliases)
        {
            foreach (var (type, table) in aliases)
            {
                if (!b.Npc.TypeMap.ContainsKey(type)) return $"lootTables: unknown npc type '{type}'";
                if (!b.Loot.TableMap.ContainsKey(table)) return $"lootTables: unknown loot table '{table}'";
            }
        }
        var view = View(b, id, system);
        if (view.Rules.Validate(b.Hulls) is { } rules) return rules;
        var orbit = system.Station ? system.StationPath.Radius : 0;
        foreach (var drone in view.Rules.DroneList)
        {
            if (drone.Table is { } table && !b.Loot.TableMap.ContainsKey(table)) return $"drones: unknown loot table '{table}'";
        }
        if (view.Npc.Validate(b.Hulls, b.Weapons, orbit) is { } npcs) return npcs;
        if (system.Pirates?.Validate(b.Npc.TypeMap) is { } raids) return $"pirates: {raids}";
        if (system.Traders?.Validate(b.Npc.TypeMap) is { } traders) return $"traders: {traders}";
        return view.Loot.Validate(view.Npc.StationSafeRadius, orbit);
    }

    /// <summary>
    /// Разбирает все файлы вместе: правила и NPC ссылаются на корпуса и пушки, лут — на укрытие из NPC,
    /// метеориты — на корпуса, укрытие и таблицы лута, магазин — на корпуса и пушки, галактика — на всё сразу,
/// задания — на типы пиратов и предметы.
    /// </summary>
    public static bool TryParse(BalanceSources sources, out Balance? balance, out string? error)
    {
        balance = null;
        if (!HullCatalog.TryParse(sources.Hulls, out var hulls, out error))
        {
            error = $"{HullsFile}: {error}";
            return false;
        }
        if (!WeaponCatalog.TryParse(sources.Weapons, out var weapons, out error))
        {
            error = $"{WeaponsFile}: {error}";
            return false;
        }
        IReadOnlyDictionary<string, ModuleParams>? modules = null;
        if (sources.Modules is not null)
        {
            if (!ModuleCatalog.TryParse(sources.Modules, out var parsedModules, out error))
            {
                error = $"{ModulesFile}: {error}";
                return false;
            }
            modules = parsedModules;
        }
        // Тиры Mk2/Mk3 (M11): каталоги раскрываются по множителям из shop.json до всего, что ссылается на пушки и модули.
        if (!ShopRules.TryReadTiers(sources.Shop, out var tiers, out error))
        {
            error = $"{ShopFile}: {error}";
            return false;
        }
        weapons = Tiers.Expand(weapons, tiers);
        if (modules is not null)
        {
            modules = Tiers.Expand(modules, tiers);
            // Новый пилот обязан взлететь: стартовый комплект должен влезть в стартовый корпус.
            var hull = hulls[SimConfig.DefaultHull];
            var starter = Fitting.Refit(hull, Fitting.Starter, weapons, modules);
            if (!starter.Items().SequenceEqual(Fitting.Starter.Items()))
            {
                error = $"{ModulesFile}: the starter kit does not fit the '{SimConfig.DefaultHull}' hull (class or generator power)";
                return false;
            }
        }
        if (!CombatRules.TryParse(sources.Rules, hulls, out var rules, out error))
        {
            error = $"{RulesFile}: {error}";
            return false;
        }
        if (!NpcRules.TryParse(sources.Npcs, hulls, weapons, out var npcs, out error))
        {
            error = $"{NpcsFile}: {error}";
            return false;
        }
        // Лут разбирается после NPC: контейнер нельзя поставить внутрь укрытия станции, а его радиус — там.
        var gear = weapons.Keys.Concat(modules?.Keys ?? []).ToHashSet();
        if (!LootRules.TryParse(sources.Loot, out var loot, out error, npcs.StationSafeRadius, gear))
        {
            error = $"{LootFile}: {error}";
            return false;
        }
        if (!MeteorRules.TryParse(sources.Meteors, hulls, npcs.StationSafeRadius, [.. loot.TableMap.Keys], out var meteors, out error))
        {
            error = $"{MeteorsFile}: {error}";
            return false;
        }
        if (!ShopRules.TryParse(sources.Shop, hulls, weapons, modules, out var shop, out error))
        {
            error = $"{ShopFile}: {error}";
            return false;
        }
        foreach (var (id, type) in npcs.TypeMap)
        {
            if (type.Table is { } table && !loot.TableMap.ContainsKey(table))
            {
                error = $"{NpcsFile}: types.{id}: unknown loot table '{table}'";
                return false;
            }
        }
        var parsed = new Balance(hulls, weapons, rules, npcs, loot, meteors, shop, Modules: modules);
        if (sources.Galaxy is not null)
        {
            if (!GalaxyRules.TryParse(sources.Galaxy, (id, system) => ValidateLayout(parsed, id, system), out var galaxy, out error))
            {
                error = $"{GalaxyFile}: {error}";
                return false;
            }
            foreach (var region in shop.Regions?.Keys ?? [])
            {
                if (!galaxy.RegionMap.ContainsKey(region))
                {
                    error = $"{ShopFile}: regions.{region}: unknown region in {GalaxyFile}";
                    return false;
                }
            }
            foreach (var place in shop.Places?.Keys ?? [])
            {
                if (!galaxy.HasPlace(place))
                {
                    error = $"{ShopFile}: places.{place}: no such place in {GalaxyFile}";
                    return false;
                }
            }
            parsed = parsed with { GalaxySet = galaxy };
        }
        if (sources.Missions is not null)
        {
            // После метеоритов: «охота» называет размер камня поимённо, и опечатку надо ловить при разборе (M14).
            if (!MissionRules.TryParse(sources.Missions, npcs.TypeMap, loot.ItemMap, meteors.SizeMap, out var missions, out error))
            {
                error = $"{MissionsFile}: {error}";
                return false;
            }
            parsed = parsed with { MissionSet = missions };
        }
        if (sources.Party is not null)
        {
            if (!PartyRules.TryParse(sources.Party, out var party, out error))
            {
                error = $"{PartyFile}: {error}";
                return false;
            }
            parsed = parsed with { PartySet = party };
        }
        if (sources.Invasion is not null)
        {
            if (!InvasionRules.TryParse(sources.Invasion, npcs.TypeMap, out var invasion, out error))
            {
                error = $"{InvasionFile}: {error}";
                return false;
            }
            parsed = parsed with { InvasionSet = invasion };
        }
        if (sources.Market is not null)
        {
            // Последним: рынку нужны и товары из loot.json, и станции с регионами из galaxy.json.
            var galaxySet = parsed.GalaxySet;
            var hasStation = galaxySet is null ? null : (Func<string, bool>)(key => galaxySet.HasPlace(key));
            var regions = galaxySet is null ? null : galaxySet.RegionMap.Keys.ToHashSet(StringComparer.Ordinal);
            if (!MarketRules.TryParse(sources.Market, loot.ItemMap, out var market, out error, hasStation, regions))
            {
                error = $"{MarketFile}: {error}";
                return false;
            }
            parsed = parsed with { MarketSet = market };
        }
        if (sources.Reputation is not null)
        {
            // После корпусов: гейт называет корпуса поимённо, и опечатку в них надо ловить при разборе.
            if (!ReputationRules.TryParse(sources.Reputation, parsed.Hulls, out var reputation, out error))
            {
                error = $"{ReputationFile}: {error}";
                return false;
            }
            parsed = parsed with { ReputationSet = reputation };
        }
        if (sources.Careers is not null)
        {
            // Последним: путь называет поимённо корпус, пушки, модули, товар, место и систему — всё это
            // уже разобрано, и опечатку в наборе видно сразу, а не при первом входе нового пилота.
            if (!CareerRules.TryParse(
                    sources.Careers, parsed.Hulls, parsed.Weapons, parsed.Modules, parsed.Loots, parsed.GalaxySet,
                    out var careers, out error))
            {
                error = $"{CareersFile}: {error}";
                return false;
            }
            parsed = parsed with { CareerSet = careers };
        }
        if (sources.Demand is not null)
        {
            // После лута: событие называет товары поимённо, и опечатку надо ловить при разборе.
            if (!DemandRules.TryParse(sources.Demand, parsed.Loots?.ItemMap, out var demand, out error))
            {
                error = $"{DemandFile}: {error}";
                return false;
            }
            parsed = parsed with { DemandSet = demand };
        }
        balance = parsed;
        return true;
    }
}
