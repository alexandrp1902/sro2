using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Гиперврата (GDD §5): стоят в системе и ведут в соседнюю.</summary>
/// <param name="To">Id системы, куда ведут врата.</param>
public sealed record GateDef(string To, double X, double Y);

/// <summary>Положение системы на карте галактики (GDD §55) — только для рисования, в симуляции не участвует.</summary>
public sealed record MapPoint(double X, double Y);

/// <summary>Регион галактики (M11): Ядро, Пограничье, Дальний рубеж — ассортимент магазинов и подпись на карте.</summary>
/// <param name="Color">Цвет подложки на карте галактики, #rrggbb.</param>
public sealed record RegionDef(string Name, string Color = "#4f7cc4");

/// <summary>Маршрут между двумя системами (GDD §55). Distance — длина плеча: по ней считают тариф буксира (M15.6).</summary>
public sealed record LinkDef(string A, string B, double Distance);

/// <summary>
/// Звёздная система (GDD §4, §33–34): своя карта с логовами, контейнерами, дронами и вратами.
/// В центре — звезда; станция, если есть, и планеты ходят вокруг неё по орбитам. Без станции дока и укрытия нет.
/// </summary>
/// <param name="Danger">Опасность 1–6 (§33) — для игрока; сила пиратов задаётся их уровнями в spawns.</param>
/// <param name="Pvp">PvP (§34): <see cref="GalaxyRules.PvpOff"/>, <see cref="GalaxyRules.PvpBorder"/> или <see cref="GalaxyRules.PvpFree"/>.</param>
/// <param name="Seed">Небо системы: звёзды и туманность клиента.</param>
/// <param name="Meteors">Множитель метеоритов к meteors.json: 0 — их нет, 2 — вдвое чаще и вдвое больше в полёте.</param>
/// <param name="Sun">Звезда в центре; null — звезды нет (одна система без galaxy.json, как до орбит).</param>
/// <param name="StationOrbit">Орбита станции; null — станция стоит в центре.</param>
/// <param name="Planets">Планеты на орбитах.</param>
/// <param name="Pirates">Налёты пиратов (<see cref="RaidRules"/>); null — только логова из spawns.</param>
/// <param name="Drones">Учебные дроны; x и y — в осях станции (<see cref="OrbitDef.ToWorld"/>): они летят вместе с ней.</param>
/// <param name="Region">Регион (M11) из <see cref="GalaxyRules.Regions"/>: от него ассортимент магазина.</param>
/// <param name="StationSprite">Картинка станции на клиенте: ring, mining, fortress, habitat, outpost, trade, ranger…; null — по опасности.</param>
/// <param name="DockScene">Свой набор фонов дока: ranger…; null — общие сцены станции (M12-арт).</param>
/// <param name="MeteorSizes">Свои веса размеров метеоритов (small, medium, large); null — как в meteors.json.</param>
/// <param name="LootTables">Замена таблиц лута в системе: «тип NPC → таблица» (пираты Рубежа роняют Mk2/Mk3).</param>
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
    RaidRules? Pirates = null,
    TraderRules? Traders = null,
    string? Region = null,
    string? StationSprite = null,
    string? DockScene = null,
    IReadOnlyDictionary<string, double>? MeteorSizes = null,
    IReadOnlyDictionary<string, string>? LootTables = null)
{
    [JsonIgnore] public OrbitDef StationPath => StationOrbit ?? OrbitDef.Center;
    [JsonIgnore] public IReadOnlyList<PlanetDef> PlanetList => Planets ?? [];

    /// <summary>Планеты, на которые можно сесть (M15): те, у кого есть поселение.</summary>
    [JsonIgnore] public IEnumerable<PlanetDef> Settled => PlanetList.Where(p => p is { Settlement: not null, Id: not null });

    /// <summary>
    /// Места системы (M15): станция, если есть, и поселения планет — в этом порядке, чтобы «ближайшее место»
    /// при равном расстоянии предпочитало станцию, как было до планет.
    /// </summary>
    /// <param name="id">Id системы: им зовётся её станция.</param>
    /// <param name="range">Радиус подлёта к станции (<see cref="LootRules.StationRange"/>).</param>
    public IReadOnlyList<PlaceDef> Places(string id, double range)
    {
        var places = new List<PlaceDef>();
        if (Station)
            places.Add(new PlaceDef(PlaceKey.Station(id), PlaceKey.StationKind, id, id, Name, StationPath, range, DockScene));
        foreach (var planet in Settled)
        {
            // К планете подлетают снаружи: её радиус — часть дистанции, иначе садиться пришлось бы внутрь картинки.
            places.Add(new PlaceDef(
                PlaceKey.Planet(planet.Id!), PlaceKey.PlanetKind, planet.Id!, id, planet.PlaceName,
                planet.Orbit, range + planet.Size, planet.Settlement!.Scene, planet.Settlement.Shipyard));
        }
        return places;
    }
    [JsonIgnore] public IReadOnlyList<DroneSpec> DroneList => Drones ?? [];
    [JsonIgnore] public IReadOnlyList<NpcSpawn> SpawnList => Spawns ?? [];
    [JsonIgnore] public IReadOnlyList<LootContainer> ContainerList => Containers ?? [];
    [JsonIgnore] public IReadOnlyList<GateDef> GateList => Gates ?? [];

    /// <summary>Врата в систему to; null — таких здесь нет.</summary>
    public GateDef? GateTo(string to) => GateList.FirstOrDefault(g => g.To == to);
}

/// <summary>
/// Галактика из shared/galaxy.json (GDD §4–6, §33–34, §55): системы, их раскладка, врата и маршруты.
/// Типы пиратов, таблицы лута и радиусы остаются в npcs.json, loot.json и combat.json — здесь только где что стоит.
/// </summary>
/// <param name="GateRange">Ближе этого к вратам можно начать прыжок.</param>
/// <param name="JumpSeconds">Подготовка прыжка (§5 — 3 секунды).</param>
/// <param name="ArrivalOffset">Корабль после прыжка появляется на столько ближе к центру, чем врата.</param>
/// <param name="StartSystem">Здесь появляются новые пилоты и гости; в ней обязана быть станция.</param>
/// <param name="Regions">Регионы (M11); null — регионов нет, магазин везде один.</param>
public sealed record GalaxyRules(
    double GateRange = 250,
    double JumpSeconds = 3,
    double ArrivalOffset = 250,
    string StartSystem = GalaxyRules.DefaultSystem,
    IReadOnlyDictionary<string, SystemDef>? Systems = null,
    IReadOnlyList<LinkDef>? Links = null,
    IReadOnlyDictionary<string, RegionDef>? Regions = null)
{
    public const string File = "galaxy.json";

    public const string PvpOff = "off";
    public const string PvpBorder = "border";
    public const string PvpFree = "free";

    /// <summary>Система тестов и баланса без galaxy.json: одна, со станцией, без врат.</summary>
    public const string DefaultSystem = "sol";

    public const int MaxDanger = 6;
    public const double MaxDistance = 1000;

    /// <summary>Логова, контейнеры и врата — не ближе этого к краю жара звезды.</summary>
    public const double HeatMargin = 200;

    /// <summary>Орбита станции — не ближе этого к краю жара: у дока и в точке появления не жжёт.</summary>
    public const double StationClearance = 400;

    /// <summary>Газовый гигант: сесть на него нельзя, поселение над ним — орбитальная платформа (M15).</summary>
    public const string GasKind = "gas";

    /// <summary>Набор сцен платформы в облаках — единственный, который разрешён газовому гиганту.</summary>
    public const string PlatformScene = "orbital-platform";

    /// <summary>
    /// Одна система со станцией и PvP, как до M7: раскладка тогда берётся из npcs.json, loot.json и combat.json.
    /// </summary>
    public static readonly GalaxyRules Single = new(Systems: new Dictionary<string, SystemDef> { [DefaultSystem] = Lone(DefaultSystem) });

    /// <summary>Система, которой нет в galaxy.json, — ведёт себя как до M7.</summary>
    public static SystemDef Lone(string name) => new(name, Pvp: PvpFree);

    [JsonIgnore] public IReadOnlyDictionary<string, SystemDef> SystemMap => Systems ?? new Dictionary<string, SystemDef>();
    [JsonIgnore] public IReadOnlyList<LinkDef> LinkList => Links ?? [];
    [JsonIgnore] public IReadOnlyDictionary<string, RegionDef> RegionMap => Regions ?? new Dictionary<string, RegionDef>();
    [JsonIgnore] public int JumpTicks => Math.Max(1, Combat.SecondsToTicks(JumpSeconds));

    public SystemDef? System(string? id) => id is not null ? SystemMap.GetValueOrDefault(id) : null;

    /// <summary>Все ключи мест галактики (M15): станции систем и поселения планет.</summary>
    [JsonIgnore]
    public IEnumerable<string> PlaceKeys =>
        SystemMap.Where(kv => kv.Value.Station).Select(kv => PlaceKey.Station(kv.Key))
            .Concat(SystemMap.Values.SelectMany(s => s.Settled).Select(p => PlaceKey.Planet(p.Id!)));

    /// <summary>Есть ли в галактике такое место. По нему ключуются магазин, рынок и задания (M15).</summary>
    public bool HasPlace(string? key)
    {
        if (key is null) return false;
        var (kind, id) = PlaceKey.Split(key);
        return kind switch
        {
            PlaceKey.StationKind => System(id) is { Station: true },
            PlaceKey.PlanetKind => SystemMap.Values.Any(s => s.Settled.Any(p => p.Id == id)),
            _ => false,
        };
    }

    /// <summary>Система, в которой стоит место; null — такого места нет.</summary>
    public string? SystemOfPlace(string? key)
    {
        if (key is null) return null;
        var (kind, id) = PlaceKey.Split(key);
        if (kind == PlaceKey.StationKind) return System(id) is { Station: true } ? id : null;
        if (kind != PlaceKey.PlanetKind) return null;
        foreach (var (systemId, system) in SystemMap)
            if (system.Settled.Any(p => p.Id == id)) return systemId;
        return null;
    }

    /// <summary>Маршрут между a и b в любую сторону; null — прямого нет.</summary>
    public LinkDef? Link(string a, string b) =>
        LinkList.FirstOrDefault(l => (l.A == a && l.B == b) || (l.A == b && l.B == a));

    /// <summary>
    /// Сколько прыжков от from до каждой достижимой системы; сама from — 0. Недостижимых в ответе нет.
    /// Зеркало hops() в client/src/sim/galaxy.ts: по этому числу считается тариф буксира (M15.6),
    /// и цена в доке обязана совпасть с тем, что спишет сервер.
    /// </summary>
    public IReadOnlyDictionary<string, int> Hops(string from)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal) { [from] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var next = result[id] + 1;
            foreach (var link in LinkList)
            {
                var other = link.A == id ? link.B : link.B == id ? link.A : null;
                if (other is null || result.ContainsKey(other)) continue;
                result[other] = next;
                queue.Enqueue(other);
            }
        }
        return result;
    }

    /// <summary>Прыжков между системами; null — пути по вратам нет.</summary>
    public int? Jumps(string a, string b) => Hops(a).TryGetValue(b, out var n) ? n : null;

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
        if (!(GateRange > 0)) return "gateRange must be positive";
        if (!(JumpSeconds >= 0)) return "jumpSeconds must not be negative";
        if (!(ArrivalOffset >= 0)) return "arrivalOffset must not be negative";
        if (SystemMap.Count == 0) return "no systems";
        if (System(StartSystem) is not { } start) return $"unknown startSystem '{StartSystem}'";
        if (!start.Station) return "startSystem must have a station";
        foreach (var (id, region) in RegionMap)
        {
            if (region is null || string.IsNullOrWhiteSpace(region.Name)) return $"regions.{id}: name is empty";
        }

        foreach (var (id, system) in SystemMap)
        {
            var problem = system is null ? "is null" : CheckSystem(id, system) ?? validateSystem(id, system);
            if (problem is not null) return $"systems.{id}: {problem}";
        }

        // Id планет — ключи мест на всю галактику: двух «terra» быть не может, иначе магазин и репутация
        // одного поселения достанутся другому.
        var planetIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, system) in SystemMap)
        {
            foreach (var planet in system.PlanetList)
            {
                if (planet?.Id is not { } planetId) continue;
                if (planetIds.TryGetValue(planetId, out var owner)) return $"systems.{id}: planet id '{planetId}' is already used in '{owner}'";
                planetIds[planetId] = id;
            }
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
        if (Regions is not null && (system.Region is null || !Regions.ContainsKey(system.Region)))
            return $"region must be one of {string.Join(", ", Regions.Keys)}";
        if (system.MeteorSizes is { } weights && weights.Values.Any(w => !(w >= 0))) return "meteorSizes: weights must not be negative";
        if (system.Sun is { } sun && sun.Validate() is { } sunProblem) return $"sun: {sunProblem}";
        if (system.StationOrbit is { } orbit && orbit.Validate() is { } orbitProblem) return $"stationOrbit: {orbitProblem}";
        var burn = system.Sun?.BurnRadius ?? 0;
        if (system.Station && system.StationOrbit is not null && system.StationPath.Radius < burn + StationClearance)
            return $"stationOrbit: radius must be at least {burn + StationClearance} (sun burnRadius + {StationClearance}): docking must not burn";
        for (var i = 0; i < system.PlanetList.Count; i++)
        {
            var planet = system.PlanetList[i];
            var problem = planet is null ? "is null" : planet.Validate();
            if (problem is null && planet!.Settlement is not null)
            {
                // Садиться в жаре звезды нельзя — та же мерка, что у орбиты станции, плюс радиус самой планеты.
                if (planet.Orbit.Radius - planet.Size < burn + StationClearance)
                    problem = $"settlement: orbit radius minus size must be at least {burn + StationClearance} (sun burnRadius + {StationClearance}): landing must not burn";
                // На газовый гигант не садятся: там поселение — орбитальная платформа, и рисуется она своим набором сцен.
                else if (planet.Kind == GasKind && planet.Settlement.Scene != PlatformScene)
                    problem = $"settlement: a {GasKind} giant can only hold an orbital platform: scene must be '{PlatformScene}'";
            }
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

/// <summary>
/// Торговцы системы (GDD §31): летают между станцией и вратами, пираты на них охотятся, пилоты могут грабить.
/// Долетел — ушёл в док или в прыжок; уничтожен — оставил груз. Вместо ушедшего через срок появляется новый.
/// </summary>
/// <param name="Count">Столько торговцев в системе одновременно.</param>
/// <param name="RespawnSeconds">Через столько после ухода или гибели появляется новый.</param>
/// <param name="Type">Тип из npcs.json: корпус, прочность и таблица лута.</param>
/// <param name="Throttle">Тяга в пути, 0…1: гружёный торговец не гонит.</param>
/// <param name="SosReward">Столько кредитов торговец платит каждому пилоту, который отбивал его от нападавших и спас.</param>
/// <param name="SosQuietSeconds">Столько секунд без выстрелов по торговцу — и SOS снят: он спасён.</param>
public sealed record TraderRules(
    int Count = 1,
    double RespawnSeconds = 40,
    string Type = "trader",
    double Throttle = 0.7,
    int SosReward = 150,
    double SosQuietSeconds = 8)
{
    public const int MaxCount = 10;

    [JsonIgnore] public int RespawnTicks => Math.Max(1, Combat.SecondsToTicks(RespawnSeconds));
    [JsonIgnore] public int SosQuietTicks => Math.Max(1, Combat.SecondsToTicks(SosQuietSeconds));

    public string? Validate(IReadOnlyDictionary<string, NpcType> types)
    {
        if (Count is < 0 or > MaxCount) return $"count must be within 0..{MaxCount}";
        if (!(RespawnSeconds >= 0)) return "respawnSeconds must not be negative";
        if (Type is null || !types.TryGetValue(Type, out var type)) return $"unknown type '{Type}'";
        if (type.Faction != NpcType.TraderFaction) return $"type '{Type}' must have faction '{NpcType.TraderFaction}'";
        if (!(Throttle > 0 && Throttle <= 1)) return "throttle must be within 0..1";
        if (SosReward < 0) return "sosReward must not be negative";
        if (!(SosQuietSeconds > 0)) return "sosQuietSeconds must be positive";
        return null;
    }
}
