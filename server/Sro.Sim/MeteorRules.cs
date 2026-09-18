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
/// Метеориты из shared/meteors.json: угроза, которая не зависит от пиратов. Летят по прямой через систему,
/// таранят корабли, а расстрелянные роняют минералы. Трасса никогда не проходит через укрытие у станции.
/// </summary>
/// <param name="SpawnIntervalSeconds">Средний интервал между появлениями.</param>
/// <param name="SpawnJitter">Разброс интервала, долей: 0.5 — от половины до полутора.</param>
/// <param name="MaxAlive">Больше этого метеоритов в системе одновременно не бывает; 0 — метеоритов нет.</param>
/// <param name="AimRadius">Трасса проходит через случайную точку этого круга вокруг станции — то есть через обитаемую часть.</param>
/// <param name="DespawnMargin">Вышедший за границу мира дальше этого исчезает.</param>
/// <param name="LifetimeSeconds">Предохранитель: столько метеорит живёт в любом случае.</param>
/// <param name="WarnSeconds">Клиент предупреждает о таране, если до сближения меньше этого.</param>
/// <param name="WarnMissFactor">Предупреждает, если разминуться выходит ближе (сумма радиусов × этот множитель).</param>
/// <param name="RamMinFactor">Нижний предел множителя урона тарана — когда метеорит догоняет или задевает вскользь.</param>
/// <param name="RamMaxFactor">Верхний предел — лоб в лоб: меньше расчётного, чтобы таран с полного здоровья не убивал.</param>
public sealed record MeteorRules(
    double SpawnIntervalSeconds = 7,
    double SpawnJitter = 0.5,
    int MaxAlive = 0,
    double AimRadius = 2600,
    double DespawnMargin = 600,
    double LifetimeSeconds = 90,
    double WarnSeconds = 4,
    double WarnMissFactor = 1.6,
    double RamMinFactor = 0.5,
    double RamMaxFactor = 1.6,
    IReadOnlyDictionary<string, MeteorSize>? Sizes = null)
{
    public const string File = "meteors.json";
    public const string Name = "Метеорит";
    /// <summary>Псевдо-пушка тарана в ShotDto: в weapons.json её нет, иначе она всплыла бы в dev-панели.</summary>
    public const string RamWeapon = "ram";
    /// <summary>Столько попыток найти трассу мимо укрытия, потом появление пропускается.</summary>
    public const int SpawnAttempts = 12;

    /// <summary>Без метеоритов: для тестов и когда файла нет.</summary>
    public static readonly MeteorRules None = new();

    [JsonIgnore] public int SpawnIntervalTicks => Math.Max(1, Combat.SecondsToTicks(SpawnIntervalSeconds));
    [JsonIgnore] public int LifetimeTicks => Math.Max(1, Combat.SecondsToTicks(LifetimeSeconds));
    [JsonIgnore] public IReadOnlyDictionary<string, MeteorSize> SizeMap => Sizes ?? new Dictionary<string, MeteorSize>();
    /// <summary>Метеориты вообще появляются.</summary>
    [JsonIgnore] public bool Enabled => MaxAlive > 0 && SizeMap.Values.Any(s => s.Weight > 0);

    /// <summary>Размер по весам. roll — из [0, 1).</summary>
    public string? PickSize(double roll)
    {
        var total = 0.0;
        foreach (var size in SizeMap.Values) total += size.Weight;
        if (!(total > 0)) return null;

        var at = roll * total;
        string? last = null;
        foreach (var (id, size) in SizeMap)
        {
            if (size.Weight <= 0) continue;
            last = id;
            if (at < size.Weight) return id;
            at -= size.Weight;
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
        if (!(SpawnJitter >= 0 && SpawnJitter < 1)) return "spawnJitter must be within 0..1 (1 excluded)";
        if (MaxAlive < 0) return "maxAlive must not be negative";
        if (!(AimRadius > stationSafeRadius) || AimRadius > Movement.WorldHalfSize)
            return $"aimRadius must be within {stationSafeRadius}..{Movement.WorldHalfSize} (beyond the station shelter, inside the world)";
        if (!(DespawnMargin >= 0)) return "despawnMargin must not be negative";
        if (!(LifetimeSeconds > 0)) return "lifetimeSeconds must be positive";
        if (!(WarnSeconds >= 0)) return "warnSeconds must not be negative";
        if (!(WarnMissFactor >= 1)) return "warnMissFactor must be at least 1";
        if (!(RamMinFactor > 0) || !(RamMaxFactor >= RamMinFactor)) return "ramMinFactor must be positive and not above ramMaxFactor";

        foreach (var (id, size) in SizeMap)
        {
            var problem = size is null ? "is null" : size.Validate(lootTables);
            if (problem is not null) return $"sizes.{id}: {problem}";
        }
        if (MaxAlive > 0 && SizeMap.Count > 0 && !SizeMap.Values.Any(s => s.Weight > 0))
            return "at least one size must have a positive weight";

        return CheckTunneling(hulls);
    }

    /// <summary>
    /// Столкновение проверяется раз в тик, без swept-теста. Поэтому сближение за тик не должно превышать
    /// наименьшую сумму радиусов — с запасом вдвое против лобовой границы, где касание ещё ловится.
    /// </summary>
    private string? CheckTunneling(IReadOnlyDictionary<string, HullParams> hulls)
    {
        if (SizeMap.Count == 0 || hulls.Count == 0) return null;
        var meteorSpeed = SizeMap.Values.Max(s => s.SpeedMax);
        var shipSpeed = hulls.Values.Max(h => h.MaxSpeed);
        var reach = SizeMap.Values.Min(s => s.Radius) + hulls.Values.Min(h => h.Size);
        var step = (meteorSpeed + shipSpeed) * SimConfig.Dt;
        if (step > reach)
            return $"speed is too high for the tick rate: relative step {step:0.#} per tick must not exceed {reach:0.#} " +
                   "(smallest meteor radius + smallest hull size), otherwise a collision can be skipped";
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
