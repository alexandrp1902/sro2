using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Гиперврата (GDD §5): стоят в системе и ведут в соседнюю.</summary>
/// <param name="To">Id системы, куда ведут врата.</param>
public sealed record GateDef(string To, double X, double Y);

/// <summary>Положение системы на карте галактики (GDD §55) — только для рисования, в симуляции не участвует.</summary>
public sealed record MapPoint(double X, double Y);

/// <summary>Маршрут между двумя системами (GDD §55). Прыжок стоит distance × fuelPerDistance топлива (§6).</summary>
public sealed record LinkDef(string A, string B, double Distance);

/// <summary>
/// Звёздная система (GDD §4, §33–34): своя карта с логовами, контейнерами, дронами и вратами.
/// В центре — звезда; станция, если есть, и планеты ходят вокруг неё по орбитам. Без станции дока и укрытия нет.
/// </summary>
/// <param name="Danger">Опасность 1–5 (§33) — для игрока; сила пиратов задаётся их уровнями в spawns.</param>
/// <param name="Pvp">PvP (§34): <see cref="GalaxyRules.PvpOff"/>, <see cref="GalaxyRules.PvpBorder"/> или <see cref="GalaxyRules.PvpFree"/>.</param>
/// <param name="Seed">Небо системы: звёзды и туманность клиента.</param>
/// <param name="Meteors">Множитель метеоритов к meteors.json: 0 — их нет, 2 — вдвое чаще и вдвое больше в полёте.</param>
/// <param name="Sun">Звезда в центре; null — звезды нет (одна система без galaxy.json, как до орбит).</param>
/// <param name="StationOrbit">Орбита станции; null — станция стоит в центре.</param>
/// <param name="Planets">Планеты на орбитах.</param>
/// <param name="Pirates">Налёты пиратов (<see cref="RaidRules"/>); null — только логова из spawns.</param>
/// <param name="Drones">Учебные дроны; x и y — в осях станции (<see cref="OrbitDef.ToWorld"/>): они летят вместе с ней.</param>
public sealed record SystemDef(
    string Name,
    int Danger = 1,
    string Pvp = GalaxyRules.PvpOff,
    bool Station = true,
    int Seed = 0,
    MapPoint? Map = null,
    double Meteors = 1,
    IReadOnlyList<DroneSpec>? Drones = null,
    IReadOnlyList<NpcSpawn>? Spawns = null,
    IReadOnlyList<LootContainer>? Containers = null,
    IReadOnlyList<GateDef>? Gates = null,
    SunDef? Sun = null,
    OrbitDef? StationOrbit = null,
    IReadOnlyList<PlanetDef>? Planets = null,
    RaidRules? Pirates = null)
{
    [JsonIgnore] public OrbitDef StationPath => StationOrbit ?? OrbitDef.Center;
    [JsonIgnore] public IReadOnlyList<PlanetDef> PlanetList => Planets ?? [];
    [JsonIgnore] public IReadOnlyList<DroneSpec> DroneList => Drones ?? [];
    [JsonIgnore] public IReadOnlyList<NpcSpawn> SpawnList => Spawns ?? [];
    [JsonIgnore] public IReadOnlyList<LootContainer> ContainerList => Containers ?? [];
    [JsonIgnore] public IReadOnlyList<GateDef> GateList => Gates ?? [];

    /// <summary>Врата в систему to; null — таких здесь нет.</summary>
    public GateDef? GateTo(string to) => GateList.FirstOrDefault(g => g.To == to);
}

/// <summary>
/// Галактика из shared/galaxy.json (GDD §4–6, §33–34, §55): системы, их раскладка, врата, маршруты и топливо.
/// Типы пиратов, таблицы лута и радиусы остаются в npcs.json, loot.json и combat.json — здесь только где что стоит.
/// </summary>
/// <param name="FuelPerDistance">Топлива за единицу расстояния маршрута — «коэффициент двигателя» из §6.</param>
/// <param name="GateRange">Ближе этого к вратам можно начать прыжок.</param>
/// <param name="JumpSeconds">Подготовка прыжка (§5 — 3 секунды).</param>
/// <param name="ArrivalOffset">Корабль после прыжка появляется на столько ближе к центру, чем врата.</param>
/// <param name="StartSystem">Здесь появляются новые пилоты и гости; в ней обязана быть станция.</param>
public sealed record GalaxyRules(
    double FuelPerDistance = 1,
    double GateRange = 250,
    double JumpSeconds = 3,
    double ArrivalOffset = 250,
    string StartSystem = GalaxyRules.DefaultSystem,
    IReadOnlyDictionary<string, SystemDef>? Systems = null,
    IReadOnlyList<LinkDef>? Links = null)
{
    public const string File = "galaxy.json";

    public const string PvpOff = "off";
    public const string PvpBorder = "border";
    public const string PvpFree = "free";

    /// <summary>Система тестов и баланса без galaxy.json: одна, со станцией, без врат.</summary>
    public const string DefaultSystem = "sol";

    public const int MaxDanger = 5;
    public const double MaxDistance = 1000;

    /// <summary>Логова, контейнеры и врата — не ближе этого к краю жара звезды.</summary>
    public const double HeatMargin = 200;

    /// <summary>Орбита станции — не ближе этого к краю жара: у дока и в точке появления не жжёт.</summary>
    public const double StationClearance = 400;

    /// <summary>
    /// Одна система со станцией и PvP, как до M7: раскладка тогда берётся из npcs.json, loot.json и combat.json.
    /// </summary>
    public static readonly GalaxyRules Single = new(Systems: new Dictionary<string, SystemDef> { [DefaultSystem] = Lone(DefaultSystem) });

    /// <summary>Система, которой нет в galaxy.json, — ведёт себя как до M7.</summary>
    public static SystemDef Lone(string name) => new(name, Pvp: PvpFree);

    [JsonIgnore] public IReadOnlyDictionary<string, SystemDef> SystemMap => Systems ?? new Dictionary<string, SystemDef>();
    [JsonIgnore] public IReadOnlyList<LinkDef> LinkList => Links ?? [];
    [JsonIgnore] public int JumpTicks => Math.Max(1, Combat.SecondsToTicks(JumpSeconds));

    public SystemDef? System(string? id) => id is not null ? SystemMap.GetValueOrDefault(id) : null;

    /// <summary>Маршрут между a и b в любую сторону; null — прямого нет.</summary>
    public LinkDef? Link(string a, string b) =>
        LinkList.FirstOrDefault(l => (l.A == a && l.B == b) || (l.A == b && l.B == a));

    /// <summary>Сколько топлива стоит прыжок a → b; null — прямого маршрута нет. Округляется вверх.</summary>
    public int? JumpCost(string a, string b) => Link(a, b) is { } link ? Cost(link) : null;

    public int Cost(LinkDef link) => (int)Math.Ceiling(link.Distance * FuelPerDistance - 1e-9);

    /// <summary>Куда корабль попадает после прыжка from → to: у ответных врат, на ArrivalOffset ближе к центру.</summary>
    public (double X, double Y)? Arrival(string from, string to)
    {
        if (System(to)?.GateTo(from) is not { } gate) return null;
        var length = Math.Sqrt(gate.X * gate.X + gate.Y * gate.Y);
        if (length < 1e-9) return (gate.X, gate.Y);
        var shift = Math.Min(ArrivalOffset, length) / length;
        return (gate.X - gate.X * shift, gate.Y - gate.Y * shift);
    }

    /// <param name="validateSystem">Проверка раскладки системы по правилам NPC, лута и боя; null — всё хорошо.</param>
    public string? Validate(Func<string, SystemDef, string?> validateSystem)
    {
        if (!(FuelPerDistance >= 0)) return "fuelPerDistance must not be negative";
        if (!(GateRange > 0)) return "gateRange must be positive";
        if (!(JumpSeconds >= 0)) return "jumpSeconds must not be negative";
        if (!(ArrivalOffset >= 0)) return "arrivalOffset must not be negative";
        if (SystemMap.Count == 0) return "no systems";
        if (System(StartSystem) is not { } start) return $"unknown startSystem '{StartSystem}'";
        if (!start.Station) return "startSystem must have a station";

        foreach (var (id, system) in SystemMap)
        {
            var problem = system is null ? "is null" : CheckSystem(id, system) ?? validateSystem(id, system);
            if (problem is not null) return $"systems.{id}: {problem}";
        }

        for (var i = 0; i < LinkList.Count; i++)
        {
            var link = LinkList[i];
            var problem = link switch
            {
                null => "is null",
                _ when !SystemMap.ContainsKey(link.A ?? "") => $"unknown system '{link.A}'",
                _ when !SystemMap.ContainsKey(link.B ?? "") => $"unknown system '{link.B}'",
                _ when link.A == link.B => "a and b must differ",
                _ when !(link.Distance > 0 && link.Distance <= MaxDistance) => $"distance must be within 0..{MaxDistance}",
                _ when LinkList.Take(i).Any(l => l is not null && ((l.A == link.A && l.B == link.B) || (l.A == link.B && l.B == link.A))) => "duplicate link",
                _ when SystemMap[link.A].GateTo(link.B) is null => $"no gate from {link.A} to {link.B}",
                _ when SystemMap[link.B].GateTo(link.A) is null => $"no gate from {link.B} to {link.A}",
                _ => null,
            };
            if (problem is not null) return $"links[{i}]: {problem}";
        }

        // Врата без маршрута вели бы в никуда: цену прыжка не из чего посчитать.
        foreach (var (id, system) in SystemMap)
        {
            foreach (var gate in system.GateList)
            {
                if (Link(id, gate.To) is null) return $"systems.{id}: gate to '{gate.To}' has no link";
            }
        }
        return null;
    }

    private string? CheckSystem(string id, SystemDef system)
    {
        if (string.IsNullOrWhiteSpace(system.Name)) return "name is empty";
        if (system.Danger is < 1 or > MaxDanger) return $"danger must be within 1..{MaxDanger}";
        if (system.Pvp is not (PvpOff or PvpBorder or PvpFree)) return $"pvp must be one of {PvpOff}, {PvpBorder}, {PvpFree}";
        if (!(system.Meteors >= 0)) return "meteors must not be negative";
        if (system.Sun is { } sun && sun.Validate() is { } sunProblem) return $"sun: {sunProblem}";
        if (system.StationOrbit is { } orbit && orbit.Validate() is { } orbitProblem) return $"stationOrbit: {orbitProblem}";
        var burn = system.Sun?.BurnRadius ?? 0;
        if (system.Station && system.StationOrbit is not null && system.StationPath.Radius < burn + StationClearance)
            return $"stationOrbit: radius must be at least {burn + StationClearance} (sun burnRadius + {StationClearance}): docking must not burn";
        for (var i = 0; i < system.PlanetList.Count; i++)
        {
            var problem = system.PlanetList[i] is null ? "is null" : system.PlanetList[i].Validate();
            if (problem is not null) return $"planets[{i}]: {problem}";
        }
        // Звезда жжёт — ничего, к чему надо подлетать, в её жаре стоять не должно.
        for (var i = 0; i < system.SpawnList.Count; i++)
        {
            if (system.SpawnList[i] is { } spawn && Hypot(spawn.X, spawn.Y) < burn + HeatMargin)
                return $"spawns[{i}]: too close to the sun: must be at least {burn + HeatMargin} from the centre";
        }
        if (system.Pirates?.Base is { } pirateBase && Hypot(pirateBase.X, pirateBase.Y) < burn + HeatMargin)
            return $"pirates: base: too close to the sun: must be at least {burn + HeatMargin} from the centre";
        for (var i = 0; i < system.ContainerList.Count; i++)
        {
            if (system.ContainerList[i] is { } container && Hypot(container.X, container.Y) < burn + HeatMargin)
                return $"containers[{i}]: too close to the sun: must be at least {burn + HeatMargin} from the centre";
        }
        for (var i = 0; i < system.GateList.Count; i++)
        {
            var gate = system.GateList[i];
            var problem = gate switch
            {
                null => "is null",
                _ when gate.To is null || !SystemMap.ContainsKey(gate.To) => $"unknown system '{gate.To}'",
                _ when gate.To == id => "a gate must lead to another system",
                _ when system.GateList.Take(i).Any(g => g?.To == gate.To) => $"second gate to '{gate.To}'",
                _ when !(Math.Abs(gate.X) <= NpcRules.WorldLimit) || !(Math.Abs(gate.Y) <= NpcRules.WorldLimit) =>
                    $"x and y must be within ±{NpcRules.WorldLimit}",
                _ when Hypot(gate.X, gate.Y) < burn + HeatMargin => $"too close to the sun: must be at least {burn + HeatMargin} from the centre",
                _ => null,
            };
            if (problem is not null) return $"gates[{i}]: {problem}";
        }
        return null;
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    public static bool TryParse(string json, Func<string, SystemDef, string?> validateSystem, out GalaxyRules rules, out string? error)
    {
        rules = Single;
        GalaxyRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GalaxyRules>(json, JsonCatalog.Options);
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
        error = parsed.Validate(validateSystem);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
