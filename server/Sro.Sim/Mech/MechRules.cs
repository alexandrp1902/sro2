using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim.Mech;

/// <summary>Корпус или шасси меха (баланс мехов §3–4). У корпуса хода нет — он 0.</summary>
public sealed record MechFrame(string Name, int Hp, int Armor, int Move = 0);

/// <summary>
/// Оружие руки (§16–17). Дальность — в клетках по Чебышёву, как и ход: «4 клетки хода» и «дальность 4»
/// значат одно и то же расстояние.
/// </summary>
public sealed record MechWeapon(
    string Name, int Damage, int Accuracy, int MinRange, int OptimalMin, int OptimalMax, int MaxRange, int Hp, int Armor,
    string Sound = "bolt");

/// <summary>Щит руки (§37–38): Block — базовый шанс блока в процентах.</summary>
public sealed record MechShield(string Name, int Hp, int Armor, int Block);

/// <summary>Сборка меха: корпус, шасси и что стоит в каждой руке (оружие или щит; null — пусто).</summary>
public sealed record MechUnitDef(string Name, string Body, string Chassis, string? Left = null, string? Right = null);

/// <summary>
/// Числа боя (§17–27, §39–40). Всё в процентных пунктах. Таблицы частей — в порядке
/// [корпус, левая рука, правая рука, шасси]; правый бок — зеркало левого.
/// </summary>
public sealed record MechCombatDef(
    int RangeStep = 5,
    int RangeFloor = 25,
    int StillBonus = 10,
    int FarMovePenalty = 10,
    int FlankBonus = 10,
    int RearBonus = 15,
    int AimedPenalty = 25,
    int HitMin = 20,
    int HitMax = 95,
    double RollMin = 0.9,
    double RollMax = 1.1,
    int[]? PartsFront = null,
    int[]? PartsLeft = null,
    int[]? PartsRear = null,
    int ShieldSame = 15,
    int ShieldOpposite = -25,
    int BlockMax = 85)
{
    public static readonly int[] DefaultFront = [45, 20, 20, 15];
    public static readonly int[] DefaultLeft = [35, 35, 10, 20];
    public static readonly int[] DefaultRear = [55, 15, 15, 15];

    [JsonIgnore] public int[] Front => PartsFront ?? DefaultFront;
    [JsonIgnore] public int[] Left => PartsLeft ?? DefaultLeft;
    [JsonIgnore] public int[] Rear => PartsRear ?? DefaultRear;
}

/// <summary>Кто где стоит в начале боя. Dir — одно из 8 направлений, 0 — север, по часовой.</summary>
public sealed record MechSpawn(string Unit, int X, int Y, int Dir);

/// <summary>Реплики карточки диалога — как у сюжета.</summary>
public sealed record MechLines(string Who, string Role, string[] Lines);

/// <summary>
/// Наземная миссия. Карта — строки одинаковой длины (легенда — <see cref="MechField"/>), здание — квадрат 2×2
/// из «b». Награда платится один раз, за первую победу.
/// </summary>
public sealed record MechMission(
    string Name,
    string Goal,
    string[] Map,
    MechSpawn[] Player,
    MechSpawn[] Enemy,
    int Reward = 0,
    MechLines? Intro = null,
    MechLines? Win = null,
    MechLines? Lose = null);

/// <summary>
/// Мехи из shared/mechs.json (M21): детали, сборки, числа боя и наземные миссии. Здесь только данные и проверки,
/// бой считает <see cref="MechBattle"/>, отыгрывает — сессия сервера. Клиент читает тот же файл для прогноза.
/// </summary>
public sealed record MechRules(
    IReadOnlyDictionary<string, MechFrame>? Bodies = null,
    IReadOnlyDictionary<string, MechFrame>? Chassis = null,
    IReadOnlyDictionary<string, MechWeapon>? Weapons = null,
    IReadOnlyDictionary<string, MechShield>? Shields = null,
    IReadOnlyDictionary<string, MechUnitDef>? Units = null,
    MechCombatDef? Combat = null,
    IReadOnlyDictionary<string, MechMission>? Missions = null)
{
    public const string File = "mechs.json";

    /// <summary>Файла нет: мехов в игре нет, ретранслятор ведёт в никуда.</summary>
    public static readonly MechRules None = new();

    /// <summary>Единственная миссия M21. В M22 она станет пятнадцатой миссией кампании.</summary>
    public const string FirstSortie = "firstSortie";

    /// <summary>Поле не больше этого по стороне: состояние целиком уходит в каждом сообщении.</summary>
    public const int MaxSide = 24;

    private static readonly Dictionary<string, MechFrame> NoFrames = new();

    [JsonIgnore] public IReadOnlyDictionary<string, MechFrame> BodyMap => Bodies ?? NoFrames;
    [JsonIgnore] public IReadOnlyDictionary<string, MechFrame> ChassisMap => Chassis ?? NoFrames;
    [JsonIgnore] public IReadOnlyDictionary<string, MechWeapon> WeaponMap => Weapons ?? new Dictionary<string, MechWeapon>();
    [JsonIgnore] public IReadOnlyDictionary<string, MechShield> ShieldMap => Shields ?? new Dictionary<string, MechShield>();
    [JsonIgnore] public IReadOnlyDictionary<string, MechUnitDef> UnitMap => Units ?? new Dictionary<string, MechUnitDef>();
    [JsonIgnore] public MechCombatDef CombatDef => Combat ?? new MechCombatDef();
    [JsonIgnore] public IReadOnlyDictionary<string, MechMission> MissionMap => Missions ?? new Dictionary<string, MechMission>();

    public MechMission? Mission(string? id) => id is not null && MissionMap.TryGetValue(id, out var m) ? m : null;

    public MechWeapon? Weapon(string? id) => id is not null && WeaponMap.TryGetValue(id, out var w) ? w : null;

    public MechShield? Shield(string? id) => id is not null && ShieldMap.TryGetValue(id, out var s) ? s : null;

    /// <summary>Прочность и броня руки: оружие или щит в ней. Пустая рука — ноль, в неё не попадают.</summary>
    public (int Hp, int Armor) Arm(string? id) =>
        Weapon(id) is { } w ? (w.Hp, w.Armor) : Shield(id) is { } s ? (s.Hp, s.Armor) : (0, 0);

    public string? Validate()
    {
        foreach (var (id, f) in BodyMap)
            if (Frame(f) is { } p) return $"bodies.{id}: {p}";
        foreach (var (id, f) in ChassisMap)
        {
            if (Frame(f) is { } p) return $"chassis.{id}: {p}";
            if (f.Move < 1) return $"chassis.{id}: move must be at least 1";
        }
        foreach (var (id, w) in WeaponMap)
        {
            if (w is null) return $"weapons.{id}: is null";
            if (string.IsNullOrWhiteSpace(w.Name)) return $"weapons.{id}: name is empty";
            if (w.Damage < 1 || w.Hp < 1 || w.Armor < 0) return $"weapons.{id}: damage and hp must be positive, armor not negative";
            if (w.Accuracy is < 1 or > 100) return $"weapons.{id}: accuracy must be within 1..100";
            if (!(1 <= w.MinRange && w.MinRange <= w.OptimalMin && w.OptimalMin <= w.OptimalMax && w.OptimalMax <= w.MaxRange))
                return $"weapons.{id}: ranges must go 1 <= min <= optimalMin <= optimalMax <= max";
            if (ShieldMap.ContainsKey(id)) return $"weapons.{id}: a shield has the same id, arms would be ambiguous";
        }
        foreach (var (id, s) in ShieldMap)
        {
            if (s is null) return $"shields.{id}: is null";
            if (string.IsNullOrWhiteSpace(s.Name)) return $"shields.{id}: name is empty";
            if (s.Hp < 1 || s.Armor < 0) return $"shields.{id}: hp must be positive, armor not negative";
            if (s.Block is < 0 or > 100) return $"shields.{id}: block must be within 0..100";
        }
        foreach (var (id, u) in UnitMap)
        {
            if (u is null) return $"units.{id}: is null";
            if (string.IsNullOrWhiteSpace(u.Name)) return $"units.{id}: name is empty";
            if (!BodyMap.ContainsKey(u.Body)) return $"units.{id}: unknown body '{u.Body}'";
            if (!ChassisMap.ContainsKey(u.Chassis)) return $"units.{id}: unknown chassis '{u.Chassis}'";
            if (u.Left is { } l && Arm(l).Hp == 0) return $"units.{id}: unknown left arm '{l}'";
            if (u.Right is { } r && Arm(r).Hp == 0) return $"units.{id}: unknown right arm '{r}'";
            // Щит в двух руках сразу — это две таблицы блока, а правило одно (§39): не бывает.
            if (Shield(u.Left) is not null && Shield(u.Right) is not null) return $"units.{id}: two shields";
        }
        if (Combat is { } c && CheckCombat(c) is { } cp) return $"combat: {cp}";
        foreach (var (id, m) in MissionMap)
            if (CheckMission(m) is { } p) return $"missions.{id}: {p}";
        return null;
    }

    private static string? Frame(MechFrame? f)
    {
        if (f is null) return "is null";
        if (string.IsNullOrWhiteSpace(f.Name)) return "name is empty";
        if (f.Hp < 1 || f.Armor < 0) return "hp must be positive, armor not negative";
        return null;
    }

    private static string? CheckCombat(MechCombatDef c)
    {
        if (c.HitMin < 0 || c.HitMax > 100 || c.HitMin > c.HitMax) return "hitMin..hitMax must lie within 0..100";
        if (c.BlockMax is < 0 or > 100) return "blockMax must be within 0..100";
        if (c.RollMin <= 0 || c.RollMin > c.RollMax) return "rollMin must be positive and not above rollMax";
        foreach (var (name, table) in new[] { ("partsFront", c.PartsFront), ("partsLeft", c.PartsLeft), ("partsRear", c.PartsRear) })
        {
            if (table is null) continue;
            if (table.Length != 4 || table.Any(w => w < 0) || table.Sum() != 100)
                return $"{name} must be four non-negative weights summing to 100";
        }
        return null;
    }

    private string? CheckMission(MechMission? m)
    {
        if (m is null) return "is null";
        if (string.IsNullOrWhiteSpace(m.Name)) return "name is empty";
        if (m.Reward < 0) return "reward must not be negative";
        if (m.Map is null || m.Map.Length == 0) return "map is empty";
        var width = m.Map[0].Length;
        if (width == 0 || width > MaxSide || m.Map.Length > MaxSide) return $"map must be 1..{MaxSide} cells on each side";
        if (m.Map.Any(row => row.Length != width)) return "map rows must have the same length";
        if (m.Map.SelectMany(row => row).FirstOrDefault(ch => !MechField.Legend.Contains(ch)) is var bad and not '\0')
            return $"unknown map cell '{bad}': must be one of '{MechField.Legend}'";
        var field = new MechField(m.Map);
        if (field.Buildings() is null) return "every building must be a whole 2x2 block of 'b'";
        if (m.Player is not { Length: > 0 } || m.Enemy is not { Length: > 0 }) return "both sides need at least one mech";
        var taken = new HashSet<int>();
        foreach (var s in m.Player.Concat(m.Enemy))
        {
            if (s is null) return "spawn is null";
            if (!UnitMap.ContainsKey(s.Unit)) return $"unknown unit '{s.Unit}'";
            if (!field.Inside(s.X, s.Y)) return $"spawn ({s.X},{s.Y}) is off the map";
            if (!field.Passable(s.X, s.Y)) return $"spawn ({s.X},{s.Y}) stands on a blocked cell";
            if (s.Dir is < 0 or > 7) return $"spawn ({s.X},{s.Y}): dir must be within 0..7";
            if (!taken.Add(field.Index(s.X, s.Y))) return $"two mechs spawn at ({s.X},{s.Y})";
        }
        return null;
    }

    public static bool TryParse(string json, out MechRules rules, out string? error)
    {
        rules = None;
        MechRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MechRules>(json, JsonCatalog.Options);
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
        error = parsed.Validate();
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
