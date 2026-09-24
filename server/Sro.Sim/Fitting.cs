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
/// Модуль корабля из shared/modules.json (GDD §13): двигатель, щит, радар, генератор — по одному на корабль —
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
/// <param name="Output">Генератор: сколько энергии он даёт всему остальному (GDD §18).</param>
/// <param name="Repair">Utility: чинит корпус, единиц в секунду, если давно не было урона (<see cref="CombatRules.RepairDelay"/>).</param>
/// <param name="Cooling">Utility: доля, на которую короче перезарядка всех пушек (0.1 — на 10 %).</param>
/// <param name="Cargo">Utility: прибавка к трюму корпуса.</param>
/// <param name="Evasion">Utility (M15.6): прибавка к уклонению корпуса, % — по кораблю просто хуже попадают.</param>
/// <param name="BlockKinetic">
/// Utility (M15.6): шанс отбить кинетическое попадание, %. Бросок делается только по выстрелу, который иначе
/// попал бы, и отбитый выстрел не наносит урона вовсе — ни по щиту, ни по корпусу.
/// </param>
/// <param name="BlockEnergy">Utility (M15.6): то же для энергетического попадания — завеса рассеивает луч.</param>
/// <param name="Intercept">
/// Utility (M15.6): противоракетный комплекс. Сбивает ракеты и торпеды, как зенитка, но оружейного слота
/// не занимает — поэтому перезарядка и урон у него свои, а не от пушки-хозяина.
/// </param>
/// <param name="Grab">Utility (M19): множитель радиуса захвата груза. С особенностью «Тягача» перемножается.</param>
/// <param name="Scan">
/// Utility (M19): с какого расстояния видно контейнеры и обломки, минуя радар. С особенностью «Циркуля»
/// берётся большее из двух, а не сумма: две дальнозоркости — это не вдвое дальше.
/// </param>
/// <param name="Stealth">
/// Utility (M19): насколько ближе пират замечает этот корабль, долей. 0.4 — на 40 % ближе.
/// По рейнджерам и по другим пилотам не работает: маскировка от разбойников, а не от закона.
/// </param>
/// <param name="HpMul">Utility (M19): множитель прочности корпуса. Бронеплиты — 1.15.</param>
/// <param name="SpeedMul">Utility (M19): множитель максимальной скорости. Бронеплиты — 0.95, это плата за прочность.</param>
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
    double Output = 0,
    double Repair = 0,
    double Cooling = 0,
    double Cargo = 0,
    double Evasion = 0,
    double BlockKinetic = 0,
    double BlockEnergy = 0,
    InterceptParams? Intercept = null,
    double Grab = 1,
    double Scan = 0,
    double Stealth = 0,
    double HpMul = 1,
    double SpeedMul = 1,
    int Tier = 1)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (!Fitting.ModuleSlots.Contains(Slot) && Slot != Fitting.UtilityKind)
            return $"slot must be one of {string.Join(", ", Fitting.ModuleSlots)}, {Fitting.UtilityKind}";
        if (!EquipClass.IsValid(Class)) return "class must be S, M or L";
        if (!(Power >= 0)) return "power must not be negative";
        // Защита и маневренность — только вспомогательные модули (M15.6): у двигателя и щита свои роли,
        // и смешивать их значило бы делать один слот вдвое важнее остальных.
        if (Slot != Fitting.UtilityKind && (Evasion > 0 || BlockKinetic > 0 || BlockEnergy > 0 || Intercept is not null))
            return "evasion, block and intercept belong to a utility module";
        // То же и для новинок M19: захват, скан, маскировка и бронеплиты — вспомогательные модули.
        if (Slot != Fitting.UtilityKind && (Grab != 1 || Scan > 0 || Stealth > 0 || HpMul != 1 || SpeedMul != 1))
            return "grab, scan, stealth, hpMul and speedMul belong to a utility module";
        if (!(Grab >= 1 && Grab <= 2)) return "grab must be within 1..2";
        if (!(Scan == 0 || Scan is >= 500 and <= 6000)) return "scan must be 0 or within 500..6000";
        if (!(Stealth >= 0 && Stealth <= Fitting.MaxStealth)) return $"stealth must be within 0..{Fitting.MaxStealth}";
        if (!(HpMul >= 1 && HpMul <= 1.3)) return "hpMul must be within 1..1.3";
        if (!(SpeedMul >= Fitting.MinSpeedFactor && SpeedMul <= 1)) return $"speedMul must be within {Fitting.MinSpeedFactor}..1";
        if (Intercept?.ValidateStandalone() is { } intercept) return $"intercept: {intercept}";
        return Slot switch
        {
            Fitting.EngineSlot when !(Speed > 0) || !(Accel > 0) => "speed and accel must be positive",
            Fitting.ShieldSlot when !(Shield >= 0) || !(ShieldRegen >= 0) => "shield and shieldRegen must not be negative",
            Fitting.RadarSlot when !(Radar > 0) => "radar must be positive",
            Fitting.GeneratorSlot when !(Output > 0) => "output must be positive",
            Fitting.UtilityKind when !(Repair >= 0) || !(Cargo >= 0) || !(Cooling >= 0 && Cooling <= Fitting.MaxCooling) =>
                $"repair and cargo must not be negative, cooling must be within 0..{Fitting.MaxCooling}",
            Fitting.UtilityKind when !(Evasion >= 0 && Evasion <= Fitting.MaxEvasionBonus) =>
                $"evasion must be within 0..{Fitting.MaxEvasionBonus}",
            Fitting.UtilityKind when !(BlockKinetic >= 0 && BlockKinetic <= Fitting.MaxBlock) || !(BlockEnergy >= 0 && BlockEnergy <= Fitting.MaxBlock) =>
                $"blockKinetic and blockEnergy must be within 0..{Fitting.MaxBlock}",
            Fitting.UtilityKind when !(Repair > 0 || Cooling > 0 || Cargo > 0 || Evasion > 0 || BlockKinetic > 0 || BlockEnergy > 0 ||
                Intercept is not null || Grab > 1 || Scan > 0 || Stealth > 0 || HpMul > 1) =>
                "a utility module must repair, cool, add cargo, evade, block, intercept, grab, scan, hide or armour",
            _ => null,
        };
    }
}

/// <summary>
/// Что стоит на корабле пилота (GDD §62): пушка в каждом оружейном слоте (null — пусто), по модулю каждого вида
/// и вспомогательные модули в utility-слотах (M11; null в старых профилях — пусто).
/// Неизменяемый: любая перестановка — новый объект, поэтому по ссылке видно, что оснащение сменилось.
/// </summary>
/// <param name="Tank">
/// Бак из профиля старше M15.6. Слота больше нет (топливо отменено), и поле живёт только затем, чтобы вход
/// увидел оплаченный модуль и вернул за него кредиты: уберёшь свойство — <c>System.Text.Json</c> молча
/// выбросит ключ «tank», и возвращать станет нечего. <c>Save</c> его не пишет, наружу оно не уходит.
/// </param>
public sealed record ShipFit(
    IReadOnlyList<string?> Weapons,
    string? Engine = null,
    string? Shield = null,
    string? Radar = null,
    string? Generator = null,
    IReadOnlyList<string?>? Utility = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tank = null)
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
        Radar == other.Radar && Generator == other.Generator &&
        Trim(UtilityList).SequenceEqual(Trim(other.UtilityList));

    public override int GetHashCode() => HashCode.Combine(Weapons.Count, Engine, Shield, Radar, Generator, Trim(UtilityList).Count());

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

    /// <summary>
    /// Больше этого модули к уклонению не добавляют, % (M15.6). Потолок нужен из-за тиров: три Mk3-дюзы
    /// в трёх utility-слотах дали бы +27 сверх корпуса, и перехватчик стало бы не во что попасть.
    /// </summary>
    public const double MaxEvasionBonus = 12;

    /// <summary>
    /// Больше этого один вид урона не блокируется, % (M15.6). По той же причине: неуязвимость — не цель,
    /// а защита должна менять исход боя, не отменяя его.
    /// </summary>
    public const double MaxBlock = 40;

    /// <summary>
    /// Больше этого бронеплиты к корпусу не добавляют, долей (M19). Причина та же, что у уклонения:
    /// три Mk3-плиты в трёх слотах дали бы «Галеону» +45 % прочности, и крепкий грузовик стал бы неубиваемым.
    /// </summary>
    public const double MaxHullBonus = 0.3;

    /// <summary>Ниже этого скорость плитами не роняют (M19): корабль должен оставаться кораблём, а не мишенью.</summary>
    public const double MinSpeedFactor = 0.85;

    /// <summary>Больше этого радиус захвата не растёт (M19): «Тягач» с двумя захватами уже собирает поле, не сходя с места.</summary>
    public const double MaxGrab = 4;

    /// <summary>Больше этого маскировка не прячет, долей (M19): пират должен уметь найти цель, если подлетел вплотную.</summary>
    public const double MaxStealth = 0.6;

    /// <summary>Вид вспомогательного модуля: встаёт в любой utility-слот u0…u2.</summary>
    public const string UtilityKind = "utility";

    public const string EngineSlot = "engine";
    public const string ShieldSlot = "shield";
    public const string RadarSlot = "radar";
    public const string GeneratorSlot = "generator";

    public static readonly string[] ModuleSlots = [EngineSlot, ShieldSlot, RadarSlot, GeneratorSlot];

    /// <summary>Без этих модулей корабль не летает: их можно заменить, но не снять.</summary>
    public static readonly string[] RequiredSlots = [EngineSlot, RadarSlot, GeneratorSlot];

    /// <summary>Стартовый комплект модулей (GDD §54): есть у каждого нового пилота. Зеркало STARTER в client/src/sim/fitting.ts.</summary>
    public const string StarterEngine = "engineS";
    public const string StarterShield = "shieldS";
    public const string StarterRadar = "radarS";
    public const string StarterGenerator = "generatorS";

    /// <summary>Стартовое оснащение: стартовая пушка в первом слоте и стартовые модули.</summary>
    public static readonly ShipFit Starter = new(
        [SimConfig.DefaultWeapon], StarterEngine, StarterShield, StarterRadar, StarterGenerator);

    /// <summary>
    /// Голый корпус (M20): на нём нет ничего. Через <see cref="Refit"/> он получает стартовые двигатель,
    /// радар и генератор — самое дешёвое из обязательного, — и с этого начинается только что купленный корабль.
    /// </summary>
    public static readonly ShipFit Empty = new([]);

    /// <summary>Модуль, который ставится взамен, если обязательный слот опустел (модуль убрали из баланса или он не влез в корпус).</summary>
    public static string? StarterFor(string slot) => slot switch
    {
        EngineSlot => StarterEngine,
        ShieldSlot => StarterShield,
        RadarSlot => StarterRadar,
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
    /// Корпус с учётом модулей — по нему пилот летает, держит щит и видит радаром. Двигатель умножает
    /// скорость, разгон и торможение; щит и радар берутся из модулей, грузовые расширители прибавляют трюм.
    /// Без каталога модулей — корпус как есть (до M9).
    /// Зеркало effectiveHull в client/src/sim/fitting.ts: предсказание движения обязано совпасть с сервером.
    /// </summary>
    public static HullParams Effective(HullParams hull, ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        if (modules is null) return hull;
        var engine = Module(modules, fit.Engine, EngineSlot);
        var shield = Module(modules, fit.Shield, ShieldSlot);
        var radar = Module(modules, fit.Radar, RadarSlot);
        var speed = engine?.Speed ?? 1;
        var accel = engine?.Accel ?? 1;
        return hull with
        {
            // Бронеплиты (M19) платят скоростью за прочность: разгон и торможение они не трогают —
            // корабль остаётся отзывчивым, просто не разгоняется так, как раньше.
            MaxSpeed = hull.MaxSpeed * speed * SpeedFactor(fit, modules),
            Acceleration = hull.Acceleration * accel,
            BrakeAcceleration = hull.BrakeAcceleration * accel,
            Hp = hull.Hp * HullFactor(fit, modules),
            Shield = shield?.Shield ?? 0,
            ShieldRegen = shield?.ShieldRegen ?? 0,
            Radar = radar?.Radar ?? hull.Radar,
            // Уклонение от дюз — прямо в корпус: дальше оно само течёт в Combat.Evasion и HitChance,
            // одинаково на сервере и на клиенте, и попадает в карточку цели без правок UI.
            Evasion = hull.Evasion + EvasionBonus(fit, modules),
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

    /// <summary>Множитель прочности от бронеплит (M19); не выше 1 + <see cref="MaxHullBonus"/>.</summary>
    public static double HullFactor(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        var factor = 1.0;
        foreach (var m in Utilities(fit, modules)) factor *= m.HpMul;
        return Math.Min(factor, 1 + MaxHullBonus);
    }

    /// <summary>Множитель скорости от бронеплит (M19); не ниже <see cref="MinSpeedFactor"/>.</summary>
    public static double SpeedFactor(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        var factor = 1.0;
        foreach (var m in Utilities(fit, modules)) factor *= m.SpeedMul;
        return Math.Max(factor, MinSpeedFactor);
    }

    /// <summary>
    /// Множитель радиуса захвата от грузовых захватов и особенности корпуса (M19); не выше <see cref="MaxGrab"/>.
    /// Модули и корпус перемножаются: «Тягач» с захватом собирает поле вдвое шире, чем «Тягач» без него.
    /// </summary>
    public static double Grab(HullParams hull, ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        var factor = hull.Perk?.Grab ?? 1;
        foreach (var m in Utilities(fit, modules)) factor *= m.Grab;
        return Math.Min(factor, MaxGrab);
    }

    /// <summary>
    /// С какого расстояния видно контейнеры и обломки (M19); 0 — только радаром. Корпус и модуль не
    /// складываются, берётся больший: две дальнозоркости — это не вдвое дальше.
    /// </summary>
    public static double Scan(HullParams hull, ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        var range = hull.Perk?.Scan ?? 0;
        foreach (var m in Utilities(fit, modules)) range = Math.Max(range, m.Scan);
        return range;
    }

    /// <summary>
    /// Во сколько раз ближе пират замечает этот корабль (M19): 1 — как всех, 0.6 — на 40 % ближе.
    /// Маскировки не складываются — берётся лучшая: второй такой же модуль пользы не даёт.
    /// </summary>
    public static double Stealth(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules)
    {
        var best = 0.0;
        foreach (var m in Utilities(fit, modules)) best = Math.Max(best, m.Stealth);
        return 1 - Math.Min(best, MaxStealth);
    }

    /// <summary>Прибавка к уклонению от маневровых дюз, % (M15.6); не выше <see cref="MaxEvasionBonus"/>.</summary>
    public static double EvasionBonus(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules) =>
        Math.Min(MaxEvasionBonus, Utilities(fit, modules).Sum(m => m.Evasion));

    /// <summary>
    /// Шанс отбить попадание этого вида урона, % (M15.6); не выше <see cref="MaxBlock"/>.
    /// У ракеты — всегда 0: её не блокируют, её сбивают (<see cref="Guard"/>).
    /// </summary>
    public static double Block(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules, string damageType) => damageType switch
    {
        DamageTypes.Kinetic => Math.Min(MaxBlock, Utilities(fit, modules).Sum(m => m.BlockKinetic)),
        DamageTypes.Energy => Math.Min(MaxBlock, Utilities(fit, modules).Sum(m => m.BlockEnergy)),
        _ => 0,
    };

    /// <summary>
    /// Противоракетный комплекс корабля (M15.6): лучший по шансу из стоящих; null — его нет.
    /// Работает только один, сколько бы их ни стояло, — это и есть потолок для этой защиты.
    /// </summary>
    public static InterceptParams? Guard(ShipFit fit, IReadOnlyDictionary<string, ModuleParams>? modules) =>
        Utilities(fit, modules).Select(m => m.Intercept).Where(i => i is not null).MaxBy(i => i!.Chance);

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
