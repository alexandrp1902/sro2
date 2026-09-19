using System.Text.Json.Serialization;

namespace Sro.Sim;

// Оснащение корабля (GDD §11–13, §18–20): оружейные слоты корпуса, модули и энергия генератора.
// Зеркало — client/src/sim/fitting.ts; совпадение проверяет shared/test-vectors/fitting.json.

/// <summary>Классы оборудования (GDD §20): чем старше, тем мощнее и тем больше корпус.</summary>
public static class EquipClass
{
    public const string S = "S";
    public const string M = "M";
    public const string L = "L";

    public static bool IsValid(string? c) => c is S or M or L;

    /// <summary>S — 1, M — 2, L — 3; иное — 0.</summary>
    public static int Rank(string? c) => c switch
    {
        S => 1,
        M => 2,
        L => 3,
        _ => 0,
    };

    /// <summary>Предмет класса item встаёт в место класса slot.</summary>
    public static bool Fits(string item, string slot) => Rank(item) <= Rank(slot);
}

/// <summary>
/// Модуль корабля из shared/modules.json (GDD §13): двигатель, щит, радар, бак, генератор — по одному на корабль —
/// или вспомогательный (utility, M11): ремонт, охлаждение, грузовой расширитель — сколько utility-слотов у корпуса.
/// </summary>
/// <param name="Slot">Куда ставится: <see cref="Fitting.EngineSlot"/> и соседние.</param>
/// <param name="Class">Класс (GDD §20): не старше класса корпуса.</param>
/// <param name="Power">Сколько энергии генератора забирает (GDD §18).</param>
/// <param name="Speed">Двигатель: множитель максимальной скорости корпуса.</param>
/// <param name="Accel">Двигатель: множитель разгона и торможения корпуса. Поворот двигатель не меняет.</param>
/// <param name="Shield">Щит: ёмкость (GDD §17).</param>
/// <param name="ShieldRegen">Щит: восстановление в секунду после паузы без урона.</param>
/// <param name="Radar">Радар: дальность обзора (GDD §10).</param>
/// <param name="Fuel">Бак: ёмкость (GDD §6).</param>
/// <param name="Output">Генератор: сколько энергии он даёт всему остальному (GDD §18).</param>
/// <param name="Repair">Utility: чинит корпус, единиц в секунду, если давно не было урона (<see cref="CombatRules.RepairDelay"/>).</param>
/// <param name="Cooling">Utility: доля, на которую короче перезарядка всех пушек (0.1 — на 10 %).</param>
/// <param name="Cargo">Utility: прибавка к трюму корпуса.</param>
/// <param name="Tier">Тир Mk1–Mk3 (<see cref="Tiers"/>): в файле всегда 1, старшие тиры раскрываются при разборе.</param>
public sealed record ModuleParams(
    string Name,
    string Slot,
    string Class = EquipClass.S,
    double Power = 0,
    double Speed = 1,
    double Accel = 1,
    double Shield = 0,
    double ShieldRegen = 0,
    double Radar = 0,
    double Fuel = 0,
    double Output = 0,
    double Repair = 0,
    double Cooling = 0,
    double Cargo = 0,
    int Tier = 1)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (!Fitting.ModuleSlots.Contains(Slot) && Slot != Fitting.UtilityKind)
            return $"slot must be one of {string.Join(", ", Fitting.ModuleSlots)}, {Fitting.UtilityKind}";
        if (!EquipClass.IsValid(Class)) return "class must be S, M or L";
        if (!(Power >= 0)) return "power must not be negative";
        return Slot switch
        {
            Fitting.EngineSlot when !(Speed > 0) || !(Accel > 0) => "speed and accel must be positive",
            Fitting.ShieldSlot when !(Shield >= 0) || !(ShieldRegen >= 0) => "shield and shieldRegen must not be negative",
            Fitting.RadarSlot when !(Radar > 0) => "radar must be positive",
            Fitting.TankSlot when !(Fuel >= 0) => "fuel must not be negative",
            Fitting.GeneratorSlot when !(Output > 0) => "output must be positive",
            Fitting.UtilityKind when !(Repair >= 0) || !(Cargo >= 0) || !(Cooling >= 0 && Cooling <= Fitting.MaxCooling) =>
                $"repair and cargo must not be negative, cooling must be within 0..{Fitting.MaxCooling}",
            Fitting.UtilityKind when !(Repair > 0 || Cooling > 0 || Cargo > 0) => "a utility module must repair, cool or add cargo",
            _ => null,
        };
    }
}

/// <summary>
/// Что стоит на корабле пилота (GDD §62): пушка в каждом оружейном слоте (null — пусто), по модулю каждого вида
/// и вспомогательные модули в utility-слотах (M11; null в старых профилях — пусто).
/// Неизменяемый: любая перестановка — новый объект, поэтому по ссылке видно, что оснащение сменилось.
/// </summary>
public sealed record ShipFit(
    IReadOnlyList<string?> Weapons,
    string? Engine = null,
    string? Shield = null,
    string? Radar = null,
    string? Tank = null,
    string? Generator = null,
    IReadOnlyList<string?>? Utility = null)
{
    [JsonIgnore] public IReadOnlyList<string?> UtilityList => Utility ?? [];

    /// <summary>Что стоит в слоте: w0…w5, u0…u2 или вид модуля; null — пусто или такого слота нет.</summary>
    public string? Get(string slot)
    {
        if (Fitting.WeaponIndex(slot) is { } i) return i < Weapons.Count ? Weapons[i] : null;
        if (Fitting.UtilityIndex(slot) is { } u) return u < UtilityList.Count ? UtilityList[u] : null;
        return slot switch
        {
            Fitting.EngineSlot => Engine,
            Fitting.ShieldSlot => Shield,
            Fitting.RadarSlot => Radar,
            Fitting.TankSlot => Tank,
            Fitting.GeneratorSlot => Generator,
            _ => null,
        };
    }

    /// <summary>То же оснащение, но в слоте slot — id (null — снять).</summary>
    public ShipFit With(string slot, string? id)
    {
        if (Fitting.WeaponIndex(slot) is { } i)
        {
            var weapons = Weapons.ToList();
            while (weapons.Count <= i) weapons.Add(null);
            weapons[i] = id;
            return this with { Weapons = weapons };
        }
        if (Fitting.UtilityIndex(slot) is { } u)
        {
            var utility = UtilityList.ToList();
            while (utility.Count <= u) utility.Add(null);
            utility[u] = id;
            return this with { Utility = utility };
        }
        return slot switch
        {
            Fitting.EngineSlot => this with { Engine = id },
            Fitting.ShieldSlot => this with { Shield = id },
            Fitting.RadarSlot => this with { Radar = id },
            Fitting.TankSlot => this with { Tank = id },
            Fitting.GeneratorSlot => this with { Generator = id },
            _ => this,
        };
    }

    /// <summary>Всё, что стоит: (слот, id).</summary>
    public IEnumerable<(string Slot, string Id)> Items()
    {
        for (var i = 0; i < Weapons.Count; i++) if (Weapons[i] is { } w) yield return (Fitting.WeaponSlot(i), w);
        foreach (var slot in Fitting.ModuleSlots) if (Get(slot) is { } m) yield return (slot, m);
        for (var i = 0; i < UtilityList.Count; i++) if (UtilityList[i] is { } u) yield return (Fitting.UtilitySlot(i), u);
    }

    public bool Equals(ShipFit? other) =>
        other is not null && Weapons.SequenceEqual(other.Weapons) && Engine == other.Engine && Shield == other.Shield &&
        Radar == other.Radar && Tank == other.Tank && Generator == other.Generator &&
        Trim(UtilityList).SequenceEqual(Trim(other.UtilityList));

    public override int GetHashCode() => HashCode.Combine(Weapons.Count, Engine, Shield, Radar, Tank, Generator, Trim(UtilityList).Count());

    /// <summary>Пустые utility-слоты в конце оснащения не отличают: [] и [null] — одно и то же.</summary>
    private static IEnumerable<string?> Trim(IReadOnlyList<string?> list)
    {
        var n = list.Count;
        while (n > 0 && list[n - 1] is null) n--;
        return list.Take(n);
    }
}

/// <summary>Почему предмет не встаёт в слот (<see cref="Fitting.CanInstall"/>). Коды уходят клиенту.</summary>
public static class FitProblem
{
    /// <summary>Такого слота у корпуса нет или предмет не того вида.</summary>
    public const string Slot = "slot";
    /// <summary>Класс предмета старше слота или корпуса.</summary>
    public const string Class = "class";
    /// <summary>Не хватает энергии генератора.</summary>
    public const string Power = "power";
    /// <summary>Двигатель, радар и генератор снять нельзя — только заменить.</summary>
    public const string Required = "required";
}

public static class Fitting
{
    /// <summary>Больше оружейных слотов у корпуса не бывает (линкор GDD §12 — 6).</summary>
    public const int MaxWeaponSlots = 6;

    /// <summary>Больше utility-слотов у корпуса не бывает (M11).</summary>
    public const int MaxUtilitySlots = 3;

    /// <summary>Охлаждение не укорачивает перезарядку больше чем наполовину, сколько модулей ни ставь.</summary>
    public const double MaxCooling = 0.5;

    /// <summary>Вид вспомогательного модуля: встаёт в любой utility-слот u0…u2.</summary>
    public const string UtilityKind = "utility";

    public const string EngineSlot = "engine";
    public const string ShieldSlot = "shield";
    public const string RadarSlot = "radar";
    public const string TankSlot = "tank";
    public const string GeneratorSlot = "generator";

    public static readonly string[] ModuleSlots = [EngineSlot, ShieldSlot, RadarSlot, TankSlot, GeneratorSlot];

    /// <summary>Без этих модулей корабль не летает: их можно заменить, но не снять.</summary>
    public static readonly string[] RequiredSlots = [EngineSlot, RadarSlot, GeneratorSlot];

    /// <summary>Стартовый комплект модулей (GDD §54): есть у каждого нового пилота. Зеркало STARTER в client/src/sim/fitting.ts.</summary>
    public const string StarterEngine = "engineS";
    public const string StarterShield = "shieldS";
    public const string StarterRadar = "radarS";
    public const string StarterTank = "tankS";
    public const string StarterGenerator = "generatorS";

    /// <summary>Стартовое оснащение: стартовая пушка в первом слоте и стартовые модули.</summary>
    public static readonly ShipFit Starter = new(
        [SimConfig.DefaultWeapon], StarterEngine, StarterShield, StarterRadar, StarterTank, StarterGenerator);

    /// <summary>Модуль, который ставится взамен, если обязательный слот опустел (модуль убрали из баланса или он не влез в корпус).</summary>
    public static string? StarterFor(string slot) => slot switch
    {
        EngineSlot => StarterEngine,
        ShieldSlot => StarterShield,
        RadarSlot => StarterRadar,
        TankSlot => StarterTank,
        GeneratorSlot => StarterGenerator,
        _ => null,
    };

    public static string WeaponSlot(int index) => $"w{index}";

    public static string UtilitySlot(int index) => $"u{index}";

    /// <returns>Номер оружейного слота из «w0»…«w5»; null — это не оружейный слот.</returns>
    public static int? WeaponIndex(string? slot) =>
        slot is ['w', var d] && d is >= '0' and <= '9' && d - '0' < MaxWeaponSlots ? d - '0' : null;

    /// <returns>Номер utility-слота из «u0»…«u2»; null — это не utility-слот.</returns>
    public static int? UtilityIndex(string? slot) =>
        slot is ['u', var d] && d is >= '0' and <= '9' && d - '0' < MaxUtilitySlots ? d - '0' : null;

    public static bool IsSlot(string? slot) =>
        WeaponIndex(slot) is not null || UtilityIndex(slot) is not null || ModuleSlots.Contains(slot);

    /// <summary>
    /// Корпус с учётом модулей — по нему пилот летает, держит щит, видит радаром и заправляется. Двигатель умножает
    /// скорость, разгон и торможение; щит, радар и бак берутся из модулей, грузовые расширители прибавляют трюм.
    /// Без каталога модулей — корпус как есть (до M9).
    /// Зеркало effectiveHull в client/src/sim/fitting.ts: предсказание движения обязано совпасть с сервером.
    /// </summary>
    public static HullParams Effective(HullParams hull, ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        if (modules is null) return hull;
        var engine = Module(modules, fit.Engine, EngineSlot);
        var shield = Module(modules, fit.Shield, ShieldSlot);
        var radar = Module(modules, fit.Radar, RadarSlot);
        var tank = Module(modules, fit.Tank, TankSlot);
        var speed = engine?.Speed ?? 1;
        var accel = engine?.Accel ?? 1;
        return hull with
        {
            MaxSpeed = hull.MaxSpeed * speed,
            Acceleration = hull.Acceleration * accel,
            BrakeAcceleration = hull.BrakeAcceleration * accel,
            Shield = shield?.Shield ?? 0,
            ShieldRegen = shield?.ShieldRegen ?? 0,
            Radar = radar?.Radar ?? hull.Radar,
            Fuel = tank?.Fuel ?? 0,
            Cargo = hull.Cargo + Utilities(fit, modules).Sum(m => m.Cargo),
        };
    }

    /// <summary>Стоящие utility-модули.</summary>
    public static IEnumerable<ModuleParams> Utilities(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        if (modules is null) yield break;
        foreach (var id in fit.UtilityList) if (Module(modules, id, UtilityKind) is { } m) yield return m;
    }

    /// <summary>Ремонт корпуса в секунду от ремонтных блоков (M11).</summary>
    public static double Repair(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules) =>
        Utilities(fit, modules).Sum(m => m.Repair);

    /// <summary>Множитель перезарядки от охлаждения: 1 — без него, не меньше 1 − <see cref="MaxCooling"/>.</summary>
    public static double CooldownScale(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules) =>
        1 - Math.Min(MaxCooling, Utilities(fit, modules).Sum(m => m.Cooling));

    /// <summary>Сколько энергии забирает всё, что стоит (GDD §18).</summary>
    public static double Power(ShipFit fit, IReadOnlyDictionary<string, WeaponParams> weapons, IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        var total = 0.0;
        foreach (var id in fit.Weapons) if (id is not null && weapons.TryGetValue(id, out var w)) total += w.Power;
        if (modules is null) return total;
        foreach (var slot in ModuleSlots) if (Module(modules, fit.Get(slot), slot) is { } m) total += m.Power;
        foreach (var m in Utilities(fit, modules)) total += m.Power;
        return total;
    }

    /// <summary>Сколько энергии даёт генератор; без каталога модулей энергии не считают — бесконечность.</summary>
    public static double Output(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules) =>
        modules is null ? double.PositiveInfinity : Module(modules, fit.Generator, GeneratorSlot)?.Output ?? 0;

    /// <summary>
    /// Можно ли поставить id в slot (null — снять то, что там стоит) на этот корпус. Проверяет вид слота, класс
    /// и энергию генератора с учётом того, что старый предмет из слота уходит.
    /// </summary>
    /// <returns>null — можно; иначе код <see cref="FitProblem"/>.</returns>
    public static string? CanInstall(
        HullParams hull,
        ShipFit fit,
        string slot,
        string? id,
        IReadOnlyDictionary<string, WeaponParams> weapons,
        IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        if (WeaponIndex(slot) is { } index)
        {
            if (index >= hull.Slots.Count) return FitProblem.Slot;
            if (id is not null)
            {
                if (!weapons.TryGetValue(id, out var weapon)) return FitProblem.Slot;
                if (!EquipClass.Fits(weapon.Class, hull.Slots[index])) return FitProblem.Class;
            }
        }
        else if (UtilityIndex(slot) is { } utility)
        {
            if (modules is null || utility >= hull.UtilitySlots) return FitProblem.Slot;
            if (id is not null)
            {
                if (!modules.TryGetValue(id, out var module) || module.Slot != UtilityKind) return FitProblem.Slot;
                if (!EquipClass.Fits(module.Class, hull.Class)) return FitProblem.Class;
            }
        }
        else if (ModuleSlots.Contains(slot))
        {
            if (modules is null) return FitProblem.Slot;
            if (id is null)
            {
                if (RequiredSlots.Contains(slot)) return FitProblem.Required;
            }
            else
            {
                if (!modules.TryGetValue(id, out var module) || module.Slot != slot) return FitProblem.Slot;
                if (!EquipClass.Fits(module.Class, hull.Class)) return FitProblem.Class;
            }
        }
        else
        {
            return FitProblem.Slot;
        }
        var next = fit.With(slot, id);
        return Power(next, weapons, modules) <= Output(next, modules) + 1e-9 ? null : FitProblem.Power;
    }

    /// <summary>
    /// Оснащение, приведённое к корпусу и балансу: неизвестное и не влезающее по слоту или классу снимается,
    /// опустевший обязательный слот получает стартовый модуль, а если энергии не хватает — снимаются пушки
    /// с последнего слота. Снятое (кроме неизвестного балансу) попадает в removed — оно уходит на склад.
    /// </summary>
    public static ShipFit Refit(
        HullParams hull,
        ShipFit fit,
        IReadOnlyDictionary<string, WeaponParams> weapons,
        IReadOnlyDictionary<string, ModuleParams>? modules,
        List<string>? removed = null)
    {
        var slots = hull.Slots;
        var guns = new string?[slots.Count];
        for (var i = 0; i < fit.Weapons.Count; i++)
        {
            if (fit.Weapons[i] is not { } id || !weapons.TryGetValue(id, out var weapon)) continue;
            if (i < slots.Count && EquipClass.Fits(weapon.Class, slots[i])) guns[i] = id;
            else removed?.Add(id);
        }
        var result = new ShipFit(guns);
        if (modules is not null)
        {
            foreach (var slot in ModuleSlots)
            {
                var id = fit.Get(slot);
                if (id is not null && modules.TryGetValue(id, out var module))
                {
                    if (module.Slot == slot && EquipClass.Fits(module.Class, hull.Class))
                    {
                        result = result.With(slot, id);
                        continue;
                    }
                    removed?.Add(id);
                }
                if (RequiredSlots.Contains(slot) && StarterFor(slot) is { } starter && modules.ContainsKey(starter))
                    result = result.With(slot, starter);
            }
            for (var i = 0; i < fit.UtilityList.Count; i++)
            {
                if (fit.UtilityList[i] is not { } id || !modules.TryGetValue(id, out var module)) continue;
                if (i < hull.UtilitySlots && module.Slot == UtilityKind && EquipClass.Fits(module.Class, hull.Class))
                    result = result.With(UtilitySlot(i), id);
                else removed?.Add(id);
            }
        }
        for (var i = guns.Length - 1; i >= 0 && Power(result, weapons, modules) > Output(result, modules) + 1e-9; i--)
        {
            if (result.Weapons[i] is not { } id) continue;
            removed?.Add(id);
            result = result.With(WeaponSlot(i), null);
        }
        for (var i = result.UtilityList.Count - 1; i >= 0 && Power(result, weapons, modules) > Output(result, modules) + 1e-9; i--)
        {
            if (result.UtilityList[i] is not { } id) continue;
            removed?.Add(id);
            result = result.With(UtilitySlot(i), null);
        }
        return result;
    }

    /// <summary>Пушки по слотам (null — пусто или такой пушки больше нет).</summary>
    public static WeaponParams? WeaponAt(ShipFit fit, int slot, IReadOnlyDictionary<string, WeaponParams> weapons) =>
        slot < fit.Weapons.Count && fit.Weapons[slot] is { } id ? weapons.GetValueOrDefault(id) : null;

    private static ModuleParams? Module(IReadOnlyDictionary<string, ModuleParams> modules, string? id, string slot) =>
        id is not null && modules.TryGetValue(id, out var m) && m.Slot == slot ? m : null;
}

/// <summary>Разбор shared/modules.json: словарь «id модуля → параметры». Стартовый комплект обязан быть.</summary>
public static class ModuleCatalog
{
    public const string File = "modules.json";

    public static bool TryParse(string json, out IReadOnlyDictionary<string, ModuleParams> modules, out string? error)
    {
        if (!JsonCatalog.TryParse<ModuleParams>(json, "module", Fitting.StarterEngine, m => m.Validate(), out modules, out error)) return false;
        foreach (var slot in Fitting.ModuleSlots)
        {
            var starter = Fitting.StarterFor(slot)!;
            if (!modules.TryGetValue(starter, out var module) || module.Slot != slot)
            {
                error = $"starter module '{starter}' must exist and be a {slot}";
                return false;
            }
        }
        return true;
    }
}

/// <summary>Самонаводящаяся ракета (боевой документ §37).</summary>
/// <param name="Speed">Скорость полёта, постоянная.</param>
/// <param name="TurnRate">Доворот к цели, градусы в секунду: от ракеты уходят резким манёвром.</param>
/// <param name="Lifetime">Столько секунд ракета летит, потом гаснет.</param>
/// <param name="HitRadius">Ракета попадает, если ближе этого к борту цели (к кругу радиусом size корпуса).</param>
/// <param name="Hp">Прочность: столько урона зенитки (M11) ракета выдерживает. 1 — любое попадание сбивает.</param>
/// <param name="Sprite">Как рисовать на клиенте: null — ракета, «torpedo» — торпеда.</param>
public sealed record MissileParams(
    double Speed = 330, double TurnRate = 120, double Lifetime = 5, double HitRadius = 10, double Hp = 1, string? Sprite = null)
{
    [JsonIgnore] public int LifetimeTicks => Math.Max(1, Combat.SecondsToTicks(Lifetime));

    public string? Validate()
    {
        if (!(Speed > 0) || !(TurnRate > 0) || !(Lifetime > 0)) return "speed, turnRate and lifetime must be positive";
        if (!(HitRadius >= 0)) return "hitRadius must not be negative";
        if (!(Hp > 0)) return "hp must be positive";
        return null;
    }
}

/// <summary>Положение ракеты: Rot — как у корабля, 0 — нос вверх.</summary>
public struct MissileState
{
    public double X;
    public double Y;
    public double Rot;
}

public static class Missiles
{
    private const double DegToRad = Math.PI / 180;

    /// <summary>Шаг ракеты: доворот носа к цели не быстрее turnRate, потом полёт носом вперёд.</summary>
    public static void Step(ref MissileState m, MissileParams p, double targetX, double targetY, double dt)
    {
        var dx = targetX - m.X;
        var dy = targetY - m.Y;
        if (dx * dx + dy * dy > 1e-9) m.Rot = Movement.MoveTowardsAngle(m.Rot, Math.Atan2(dx, -dy), p.TurnRate * DegToRad * dt);
        m.X += Math.Sin(m.Rot) * p.Speed * dt;
        m.Y -= Math.Cos(m.Rot) * p.Speed * dt;
    }

    /// <summary>Ракета достала цель: ближе hitRadius к кругу корпуса радиусом size (hulls.json size).</summary>
    public static bool Hits(in MissileState m, MissileParams p, double targetX, double targetY, double size)
    {
        var reach = p.HitRadius + size;
        var dx = targetX - m.X;
        var dy = targetY - m.Y;
        return dx * dx + dy * dy <= reach * reach;
    }
}
