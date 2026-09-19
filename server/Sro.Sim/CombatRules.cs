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
    IReadOnlyList<DroneSpec>? Drones = null)
{
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
public sealed record BalanceSources(
    string Hulls, string Weapons, string Rules, string Npcs, string Loot, string Meteors, string Shop,
    string? Galaxy = null, string? Missions = null, string? Modules = null);

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
    IReadOnlyDictionary<string, ModuleParams>? Modules = null)
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

    /// <summary>Все файлы баланса в порядке разбора.</summary>
    public static readonly string[] Files =
        [HullsFile, WeaponsFile, RulesFile, NpcsFile, LootFile, MeteorsFile, ShopFile, GalaxyFile, MissionsFile, ModulesFile];

    public NpcRules Npc => Npcs ?? NpcRules.None;

    public LootRules Loot => Loots ?? LootRules.None;

    public MeteorRules Meteors => MeteorSet ?? MeteorRules.None;

    public ShopRules Shop => ShopSet ?? ShopRules.None;

    public GalaxyRules Galaxy => GalaxySet ?? GalaxyRules.Single;

    public MissionRules Missions => MissionSet ?? MissionRules.None;

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
        return View(this, system) with { SystemId = id };
    }

    private static Balance View(Balance b, SystemDef system)
    {
        var npc = b.Npc;
        var meteors = b.MeteorSet;
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
        return b with
        {
            Rules = b.Rules with { Drones = system.DroneList },
            Npcs = npc with { Spawns = system.SpawnList, StationSafeRadius = system.Station ? npc.StationSafeRadius : 0 },
            Loots = b.Loot with { Containers = system.ContainerList },
            MeteorSet = meteors,
            CoreRadius = npc.StationSafeRadius,
        };
    }

    /// <summary>Раскладка системы по правилам NPC, лута и боя: логова и контейнеры — не в укрытии станции, дроны — в мире.</summary>
    private static string? ValidateLayout(Balance b, SystemDef system)
    {
        var view = View(b, system);
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
        if (!LootRules.TryParse(sources.Loot, out var loot, out error, npcs.StationSafeRadius))
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
            if (!GalaxyRules.TryParse(sources.Galaxy, (_, system) => ValidateLayout(parsed, system), out var galaxy, out error))
            {
                error = $"{GalaxyFile}: {error}";
                return false;
            }
            parsed = parsed with { GalaxySet = galaxy };
        }
        if (sources.Missions is not null)
        {
            if (!MissionRules.TryParse(sources.Missions, npcs.TypeMap, loot.ItemMap, out var missions, out error))
            {
                error = $"{MissionsFile}: {error}";
                return false;
            }
            parsed = parsed with { MissionSet = missions };
        }
        balance = parsed;
        return true;
    }
}
