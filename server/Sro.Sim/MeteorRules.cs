using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Размер метеорита: сколько держит, как быстро летит, как бьёт тараном и что роняет, если его расстрелять.</summary>
/// <param name="Radius">Радиус столкновения, он же размер на экране.</param>
/// <param name="RamDamage">Урон тарана при сближении со скоростью самого метеорита; дальше масштабируется скоростью сближения.</param>
/// <param name="Weight">Относительная частота появления этого размера.</param>
/// <param name="Table">Таблица дропа из loot.json; null — расстрелянный ничего не роняет.</param>
public sealed record MeteorSize(
    string Name,
    double Radius,
    double Hp,
    double SpeedMin,
    double SpeedMax,
    double RamDamage,
    double Weight = 1,
    string? Table = null)
{
    public string? Validate(IReadOnlyCollection<string> lootTables)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (!(Radius > 0)) return "radius must be positive";
        if (!(Hp > 0)) return "hp must be positive";
        if (!(SpeedMin > 0)) return "speedMin must be positive";
        if (!(SpeedMax >= SpeedMin)) return "speedMax must not be less than speedMin";
        if (!(RamDamage >= 0)) return "ramDamage must not be negative";
        if (!(Weight >= 0)) return "weight must not be negative";
        if (Table is not null && !lootTables.Contains(Table)) return $"unknown loot table '{Table}'";
        return null;
    }
}

/// <summary>
/// Тип траектории: насколько близко к центру системы камень целится и как быстро идёт. Медленный и близкий
/// заметно загибается тяготением, быстрый и дальний идёт почти прямо — отсюда разные дуги в одном небе.
/// </summary>
/// <param name="AimFactor">Доля AimRadius, в которую целится камень: меньше — ближе к центру и круче дуга.</param>
/// <param name="SpeedFactor">Множитель к скорости размера.</param>
/// <param name="Weight">Относительная частота этой траектории.</param>
public sealed record MeteorTrack(string Name, double AimFactor = 1, double SpeedFactor = 1, double Weight = 1)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (!(AimFactor > 0 && AimFactor <= 1)) return "aimFactor must be within 0..1";
        if (!(SpeedFactor > 0)) return "speedFactor must be positive";
        if (!(Weight >= 0)) return "weight must not be negative";
        return null;
    }
}

/// <summary>
/// Метеориты из shared/meteors.json: угроза, которая не зависит от пиратов. Идут по дуге — их ведёт
/// тяготение центра системы, — таранят корабли, а расстрелянные роняют минералы.
/// Трасса, уже с учётом искривления, никогда не проходит через укрытие у станции.
/// </summary>
/// <param name="SpawnIntervalSeconds">
/// Средний интервал между появлениями. Это именно среднее: каждый тик бросается монета, поэтому камни идут
/// неровно — то пусто, то два подряд, — а не по расписанию.
/// </param>
/// <param name="MaxAlive">Больше этого метеоритов в системе одновременно не бывает; 0 — метеоритов нет.</param>
/// <param name="AimRadius">Трасса проходит через случайную точку этого круга вокруг станции — то есть через обитаемую часть.</param>
/// <param name="DespawnMargin">Вышедший за границу мира дальше этого исчезает.</param>
/// <param name="LifetimeSeconds">Предохранитель: столько метеорит живёт в любом случае.</param>
/// <param name="RamMinFactor">Нижний предел множителя урона тарана — когда метеорит догоняет или задевает вскользь.</param>
/// <param name="RamMaxFactor">Верхний предел — лоб в лоб: меньше расчётного, чтобы таран с полного здоровья не убивал.</param>
/// <param name="Tracks">Типы траекторий с весами; пусто — все камни летят одинаково далеко от центра.</param>
/// <param name="Gravity">
/// Параметр тяготения центра системы, GM: ускорение камня — Gravity / r². Ноль — камни летят по прямой.
/// Корабли тяготение не чувствуют: это про камни, а не новая механика полёта.
/// </param>
/// <param name="GravityMinRadius">
/// Ближе этого тяготение перестаёт расти (сглаживание): иначе у самого центра ускорение уходит в бесконечность.
/// </param>
public sealed record MeteorRules(
    double SpawnIntervalSeconds = 7,
    int MaxAlive = 0,
    double AimRadius = 2600,
    double DespawnMargin = 600,
    double LifetimeSeconds = 90,
    double RamMinFactor = 0.5,
    double RamMaxFactor = 1.6,
    double Gravity = 15_000_000,
    double GravityMinRadius = 600,
    IReadOnlyDictionary<string, MeteorSize>? Sizes = null,
    IReadOnlyDictionary<string, MeteorTrack>? Tracks = null)
{
    public const string File = "meteors.json";
    public const string Name = "Метеорит";
    /// <summary>Псевдо-пушка тарана в ShotDto: в weapons.json её нет, иначе она всплыла бы в dev-панели.</summary>
    public const string RamWeapon = "ram";
    /// <summary>Столько попыток найти трассу мимо укрытия, потом появление пропускается.</summary>
    public const int SpawnAttempts = 12;

    /// <summary>
    /// Тяготение центра системы в точке (x, y): ускорение камня. Ниже GravityMinRadius сила растёт линейно,
    /// а не как 1/r² — центр системы не бесконечно тяжёлая точка.
    /// </summary>
    public (double Ax, double Ay) Pull(double x, double y)
    {
        if (Gravity <= 0) return (0, 0);
        var dx = x - SimConfig.StationX;
        var dy = y - SimConfig.StationY;
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r < 1e-9) return (0, 0);
        var soft = Math.Max(r, GravityMinRadius);
        var scale = -Gravity / (soft * soft * soft);
        return (dx * scale, dy * scale);
    }

    /// <summary>
    /// Шаг полёта камня: сначала тяготение меняет скорость, потом скорость — положение (полуявный Эйлер).
    /// Одна и та же схема на сервере и на клиенте, иначе камень на экране разойдётся с сервером.
    /// Зеркало: advance() в client/src/sim/meteors.ts, эталоны в shared/test-vectors/meteors.json.
    /// </summary>
    public void Step(ref double x, ref double y, ref double vx, ref double vy, double dt)
    {
        var (ax, ay) = Pull(x, y);
        vx += ax * dt;
        vy += ay * dt;
        x += vx * dt;
        y += vy * dt;
    }

    /// <summary>Без метеоритов: для тестов и когда файла нет.</summary>
    public static readonly MeteorRules None = new();

    /// <summary>Вероятность, что камень появится в этом тике: пуассоновский поток со средним SpawnIntervalSeconds.</summary>
    [JsonIgnore] public double SpawnChancePerTick =>
        SpawnIntervalSeconds > 0 ? 1 - Math.Exp(-SimConfig.Dt / SpawnIntervalSeconds) : 0;
    [JsonIgnore] public int LifetimeTicks => Math.Max(1, Combat.SecondsToTicks(LifetimeSeconds));
    [JsonIgnore] public IReadOnlyDictionary<string, MeteorSize> SizeMap => Sizes ?? new Dictionary<string, MeteorSize>();
    [JsonIgnore] public IReadOnlyDictionary<string, MeteorTrack> TrackMap => Tracks ?? new Dictionary<string, MeteorTrack>();
    /// <summary>Если траектории не заданы — одна обычная: целимся во весь круг, скорость как у размера.</summary>
    [JsonIgnore] public MeteorTrack DefaultTrack { get; } = new("Обычная");
    /// <summary>Метеориты вообще появляются.</summary>
    [JsonIgnore] public bool Enabled => MaxAlive > 0 && SizeMap.Values.Any(s => s.Weight > 0);

    /// <summary>Размер по весам. roll — из [0, 1).</summary>
    public string? PickSize(double roll) => PickWeighted(SizeMap, s => s.Weight, roll);

    /// <summary>Тип траектории по весам; null — траектории не заданы, и камень летит по обычной.</summary>
    public string? PickTrack(double roll) => PickWeighted(TrackMap, t => t.Weight, roll);

    /// <summary>Траектория по ключу; неизвестная или не заданная — обычная.</summary>
    public MeteorTrack Track(string? id) =>
        id is not null && TrackMap.TryGetValue(id, out var track) ? track : DefaultTrack;

    private static string? PickWeighted<T>(IReadOnlyDictionary<string, T> map, Func<T, double> weight, double roll)
    {
        var total = 0.0;
        foreach (var value in map.Values) total += weight(value);
        if (!(total > 0)) return null;

        var at = roll * total;
        string? last = null;
        foreach (var (id, value) in map)
        {
            if (weight(value) <= 0) continue;
            last = id;
            if (at < weight(value)) return id;
            at -= weight(value);
        }
        return last; // погрешность суммы: roll у самой единицы
    }

    /// <summary>
    /// Множитель урона тарана: скорость сближения вдоль линии центров, отнесённая к скорости метеорита,
    /// в пределах RamMinFactor…RamMaxFactor. Лоб в лоб больнее, догоняющий — слабее.
    /// </summary>
    public double RamFactor(double closing, double meteorSpeed) =>
        Math.Clamp(meteorSpeed > 0 ? closing / meteorSpeed : RamMaxFactor, RamMinFactor, RamMaxFactor);

    /// <param name="hulls">Для проверки туннелирования: быстрый корпус и мелкий метеорит не должны проскочить друг сквозь друга за тик.</param>
    /// <param name="stationSafeRadius">Укрытие из npcs.json: трассы мимо него должны существовать.</param>
    /// <param name="lootTables">Имена таблиц из loot.json.</param>
    public string? Validate(IReadOnlyDictionary<string, HullParams> hulls, double stationSafeRadius, IReadOnlyCollection<string> lootTables)
    {
        if (!(SpawnIntervalSeconds > 0)) return "spawnIntervalSeconds must be positive";
        if (MaxAlive < 0) return "maxAlive must not be negative";
        if (!(AimRadius > stationSafeRadius) || AimRadius > Movement.WorldHalfSize)
            return $"aimRadius must be within {stationSafeRadius}..{Movement.WorldHalfSize} (beyond the station shelter, inside the world)";
        if (!(DespawnMargin >= 0)) return "despawnMargin must not be negative";
        if (!(LifetimeSeconds > 0)) return "lifetimeSeconds must be positive";
        if (!(RamMinFactor > 0) || !(RamMaxFactor >= RamMinFactor)) return "ramMinFactor must be positive and not above ramMaxFactor";
        if (!(Gravity >= 0)) return "gravity must not be negative";
        if (!(GravityMinRadius > 0)) return "gravityMinRadius must be positive";

        foreach (var (id, size) in SizeMap)
        {
            var problem = size is null ? "is null" : size.Validate(lootTables);
            if (problem is not null) return $"sizes.{id}: {problem}";
        }
        if (MaxAlive > 0 && SizeMap.Count > 0 && !SizeMap.Values.Any(s => s.Weight > 0))
            return "at least one size must have a positive weight";

        foreach (var (id, track) in TrackMap)
        {
            var problem = track is null ? "is null" : track.Validate();
            if (problem is not null) return $"tracks.{id}: {problem}";
        }
        if (TrackMap.Count > 0 && !TrackMap.Values.Any(t => t.Weight > 0))
            return "at least one track must have a positive weight";

        return CheckTunneling(hulls, stationSafeRadius);
    }

    /// <summary>
    /// Наибольшая скорость, которую камень наберёт, падая к центру: тяготение разгоняет его от точки появления
    /// до самой близкой точки трассы. Ближе укрытия трассы не проходят, поэтому оно и есть предел разгона.
    /// </summary>
    public double TopSpeed(double stationSafeRadius)
    {
        if (SizeMap.Count == 0) return 0;
        // Самый быстрый камень: наибольшая скорость размера на наибольшем множителе траектории.
        var launch = SizeMap.Values.Max(s => s.SpeedMax) * (TrackMap.Count > 0 ? TrackMap.Values.Max(t => t.SpeedFactor) : 1);
        if (Gravity <= 0) return launch;
        var from = Movement.WorldHalfSize + DespawnMargin;
        var to = Math.Max(GravityMinRadius, stationSafeRadius);
        // v² = v₀² + 2·GM·(1/r − 1/r₀): разгон на пути от края мира до ближайшей точки.
        return Math.Sqrt(launch * launch + 2 * Gravity * Math.Max(0, 1 / to - 1 / from));
    }

    /// <summary>
    /// Столкновение проверяется раз в тик, без swept-теста. Поэтому сближение за тик не должно превышать
    /// наименьшую сумму радиусов — с запасом вдвое против лобовой границы, где касание ещё ловится.
    /// Тяготение входит сюда через разгон: слишком сильная гравитация отклоняется вместе с файлом.
    /// </summary>
    private string? CheckTunneling(IReadOnlyDictionary<string, HullParams> hulls, double stationSafeRadius)
    {
        if (SizeMap.Count == 0 || hulls.Count == 0) return null;
        var meteorSpeed = TopSpeed(stationSafeRadius);
        var shipSpeed = hulls.Values.Max(h => h.MaxSpeed);
        var reach = SizeMap.Values.Min(s => s.Radius) + hulls.Values.Min(h => h.Size);
        var step = (meteorSpeed + shipSpeed) * SimConfig.Dt;
        if (step > reach)
            return $"speed is too high for the tick rate: relative step {step:0.#} per tick must not exceed {reach:0.#} " +
                   $"(smallest meteor radius + smallest hull size), otherwise a collision can be skipped. " +
                   $"Top meteor speed with gravity is {meteorSpeed:0}";
        return null;
    }

    public static bool TryParse(
        string json,
        IReadOnlyDictionary<string, HullParams> hulls,
        double stationSafeRadius,
        IReadOnlyCollection<string> lootTables,
        out MeteorRules rules,
        out string? error)
    {
        rules = None;
        MeteorRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MeteorRules>(json, JsonCatalog.Options);
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
        error = parsed.Validate(hulls, stationSafeRadius, lootTables);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
