using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Тип NPC (GDD §31): корпус, пушки, живучесть и манера боя.</summary>
/// <param name="Weapon">Одна пушка; null — без неё. Если задан <paramref name="Weapons"/>, не смотрится.</param>
/// <param name="Hp">Своя прочность на 1-м уровне вместо корпусной; null — как у корпуса.</param>
/// <param name="Shield">Свой щит на 1-м уровне вместо корпусного; null — как у корпуса.</param>
/// <param name="Damage">Множитель урона пушки на 1-м уровне: пираты слабее игрока.</param>
/// <param name="HoldRange">Дистанция, которую NPC держит в бою, от центра до центра.</param>
/// <param name="RetreatHp">При такой доле корпуса NPC уходит в логово чиниться; 0 — бьётся до конца.</param>
/// <param name="Weapons">Пушки по слотам (GDD §12); пустой — NPC не стреляет (торговец).</param>
/// <param name="Table">Таблица лута из loot.json; null — таблица с id типа, как у пиратов.</param>
public sealed record NpcType(
    string Name,
    string Hull,
    string? Weapon = null,
    double? Hp = null,
    double? Shield = null,
    double Damage = 1,
    double HoldRange = 320,
    double RetreatHp = 0,
    IReadOnlyList<string>? Weapons = null,
    string? Table = null)
{
    [JsonIgnore] public IReadOnlyList<string> WeaponList => Weapons ?? (Weapon is null ? [] : [Weapon]);

    public string? Validate(IReadOnlyDictionary<string, HullParams> hulls, IReadOnlyDictionary<string, WeaponParams> weapons)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (Hull is null || !hulls.ContainsKey(Hull)) return $"unknown hull '{Hull}'";
        if (WeaponList.Count > Fitting.MaxWeaponSlots) return $"at most {Fitting.MaxWeaponSlots} weapons";
        foreach (var weapon in WeaponList)
        {
            if (weapon is null || !weapons.ContainsKey(weapon)) return $"unknown weapon '{weapon}'";
        }
        if (Hp is { } hp && !(hp > 0)) return "hp must be positive";
        if (Shield is { } shield && !(shield >= 0)) return "shield must not be negative";
        if (!(Damage > 0)) return "damage must be positive";
        if (!(HoldRange > 0)) return "holdRange must be positive";
        if (!(RetreatHp >= 0 && RetreatHp < 1)) return "retreatHp must be within 0..1";
        return null;
    }
}

/// <summary>Логово: столько NPC такого типа и уровня живут вокруг точки. Записи с одной точкой — одно логово.</summary>
public sealed record NpcSpawn(string Type, int Level, double X, double Y, int Count = 1)
{
    public const int MaxLevel = 99;
    public const int MaxCount = 20;
}

/// <summary>Прибавка за каждый уровень выше первого (GDD §32): доли для корпуса, щита и урона, пункты — для точности.</summary>
public sealed record NpcLevelScaling(double Hp = 0.2, double Shield = 0.2, double Damage = 0.1, double Accuracy = 2)
{
    public double HpFactor(int level) => 1 + Hp * (level - 1);
    public double ShieldFactor(int level) => 1 + Shield * (level - 1);
    public double DamageFactor(int level) => 1 + Damage * (level - 1);
    public double AccuracyBonus(int level) => Accuracy * (level - 1);

    public string? Validate() =>
        Hp >= 0 && Shield >= 0 && Damage >= 0 && Accuracy >= 0 ? null : "levelScaling values must not be negative";
}

/// <summary>NPC системы из shared/npcs.json: пираты (GDD §31–32) — типы, уровни, логова и поведение ИИ.</summary>
/// <param name="RespawnSeconds">Через столько уничтоженный NPC появляется снова в своём логове.</param>
/// <param name="AggroRange">Пират замечает игрока ближе этого.</param>
/// <param name="DropRange">Цель дальше этого потеряна.</param>
/// <param name="AssistRange">Пират вступает в бой собрата, если тот ближе этого.</param>
/// <param name="LeashRange">Дальше этого от логова пират не преследует, а возвращается.</param>
/// <param name="StationSafeRadius">Укрытие вокруг станции: пираты сюда не залетают и бросают цель, которая здесь.</param>
/// <param name="PatrolRadius">Пират патрулирует в этом радиусе вокруг логова.</param>
/// <param name="PatrolThrottle">Тяга на патруле, 0…1.</param>
public sealed record NpcRules(
    double RespawnSeconds = 20,
    double AggroRange = 700,
    double DropRange = 1000,
    double AssistRange = 900,
    double LeashRange = 1800,
    double StationSafeRadius = 900,
    double PatrolRadius = 250,
    double PatrolThrottle = 0.35,
    NpcLevelScaling? LevelScaling = null,
    IReadOnlyDictionary<string, NpcType>? Types = null,
    IReadOnlyList<NpcSpawn>? Spawns = null)
{
    public const string File = "npcs.json";

    /// <summary>Логова, точки патруля и бегство держатся на столько внутри границы мира.</summary>
    public const double WorldLimit = Movement.WorldHalfSize - 100;

    /// <summary>Без NPC: для тестов и когда файла нет.</summary>
    public static readonly NpcRules None = new();

    [JsonIgnore] public int RespawnTicks => Math.Max(1, Combat.SecondsToTicks(RespawnSeconds));
    [JsonIgnore] public NpcLevelScaling Scaling => LevelScaling ?? new NpcLevelScaling();
    [JsonIgnore] public IReadOnlyDictionary<string, NpcType> TypeMap => Types ?? new Dictionary<string, NpcType>();
    [JsonIgnore] public IReadOnlyList<NpcSpawn> SpawnList => Spawns ?? [];
    [JsonIgnore] public int Count => SpawnList.Sum(s => s.Count);

    /// <summary>«Пират Ур.2».</summary>
    public static string Name(NpcType type, int level) => $"{type.Name} Ур.{level}";

    public double MaxHp(NpcType type, int level, HullParams hull) => (type.Hp ?? hull.Hp) * Scaling.HpFactor(level);

    public double MaxShield(NpcType type, int level, HullParams hull) => (type.Shield ?? hull.Shield) * Scaling.ShieldFactor(level);

    /// <summary>Пушка NPC: урон — с множителем типа и уровня, точность — с прибавкой уровня (не выше 100).</summary>
    public WeaponParams ScaledWeapon(NpcType type, int level, WeaponParams weapon) => weapon with
    {
        Damage = weapon.Damage * type.Damage * Scaling.DamageFactor(level),
        Accuracy = Math.Min(100, weapon.Accuracy + Scaling.AccuracyBonus(level)),
    };

    /// <param name="stationOrbit">Радиус орбиты станции: укрытие ходит по этому кругу вокруг звезды; 0 — станция в центре.</param>
    public string? Validate(IReadOnlyDictionary<string, HullParams> hulls, IReadOnlyDictionary<string, WeaponParams> weapons, double stationOrbit = 0)
    {
        if (!(RespawnSeconds >= 0)) return "respawnSeconds must not be negative";
        if (!(AggroRange > 0) || !(DropRange >= AggroRange)) return "ranges must satisfy 0 < aggroRange <= dropRange";
        if (!(AssistRange >= 0) || !(LeashRange > 0) || !(StationSafeRadius >= 0) || !(PatrolRadius >= 0))
            return "assistRange, stationSafeRadius and patrolRadius must not be negative, leashRange must be positive";
        if (!(PatrolThrottle > 0 && PatrolThrottle <= 1)) return "patrolThrottle must be within 0..1";
        if (Scaling.Validate() is { } scaling) return scaling;

        foreach (var (id, type) in TypeMap)
        {
            var problem = type is null ? "is null" : type.Validate(hulls, weapons);
            if (problem is not null) return $"types.{id}: {problem}";
        }

        // Логово нельзя достать из укрытия: иначе игрок бьёт пирата дома, тот агрится, видит цель в укрытии и уходит — по кругу.
        // Станция ходит по орбите — считаем от ближайшей точки её круга, то есть от любого положения станции.
        var reach = weapons.Values.Select(w => w.MaxRange).DefaultIfEmpty(0).Max();
        var minHomeDistance = StationSafeRadius + PatrolRadius + reach;
        for (var i = 0; i < SpawnList.Count; i++)
        {
            var spawn = SpawnList[i];
            var problem = spawn switch
            {
                null => "is null",
                _ when spawn.Type is null || !TypeMap.ContainsKey(spawn.Type) => $"unknown type '{spawn.Type}'",
                _ when spawn.Level is < 1 or > NpcSpawn.MaxLevel => $"level must be within 1..{NpcSpawn.MaxLevel}",
                _ when spawn.Count is < 1 or > NpcSpawn.MaxCount => $"count must be within 1..{NpcSpawn.MaxCount}",
                _ when !(Math.Abs(spawn.X) <= WorldLimit) || !(Math.Abs(spawn.Y) <= WorldLimit) => $"x and y must be within ±{WorldLimit}",
                _ when Math.Abs(Math.Sqrt(Sq(spawn.X) + Sq(spawn.Y)) - stationOrbit) < minHomeDistance =>
                    $"too close to the station orbit: must be at least {minHomeDistance} away (safe radius + patrol radius + weapon range)",
                _ => null,
            };
            if (problem is not null) return $"spawns[{i}]: {problem}";
        }
        return null;
    }

    public static bool TryParse(
        string json,
        IReadOnlyDictionary<string, HullParams> hulls,
        IReadOnlyDictionary<string, WeaponParams> weapons,
        out NpcRules rules,
        out string? error)
    {
        rules = None;
        NpcRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<NpcRules>(json, JsonCatalog.Options);
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
        error = parsed.Validate(hulls, weapons);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }

    private static double Sq(double v) => v * v;
}
