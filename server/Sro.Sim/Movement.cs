namespace Sro.Sim;

// Модель полёта (боевой документ v0.2, §1–18, §55). Построчное зеркало client/src/sim/movement.ts:
// порядок операций совпадает, чтобы предсказание клиента сходилось с сервером. Сверка — shared/test-vectors.

/// <summary>Состояние движения (§48). Rot = 0 — нос вверх; экранные координаты, y вниз.</summary>
public struct ShipState
{
    public double X;
    public double Y;
    public double Rot;
    public double Vx;
    public double Vy;
}

/// <summary>Управление (§49): желаемое направление на экране (единичный вектор или ноль) и тяга 0…1.</summary>
public readonly record struct MoveInput(double Dx, double Dy, double Throttle)
{
    /// <summary>
    /// Проверка входа от клиента (§51): только конечные числа, тяга в [0, 1], направление единичное.
    /// Уже единичный вектор не трогаем, чтобы сервер шагал с теми же числами, что и предсказание клиента.
    /// </summary>
    public static bool TryCreate(double dx, double dy, double throttle, out MoveInput input)
    {
        input = default;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(throttle)) return false;

        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 1e-6))
        {
            dx = 0;
            dy = 0;
        }
        else if (Math.Abs(length - 1) > 1e-9)
        {
            dx /= length;
            dy /= length;
        }
        input = new MoveInput(dx, dy, Math.Clamp(throttle, 0, 1));
        return true;
    }
}

/// <summary>Параметры корпуса (§46–47). Хранятся в shared/hulls.json.</summary>
/// <param name="TurnRate">Градусы в секунду.</param>
/// <param name="LateralDampTime">За это время боковая скорость гаснет примерно до 5%.</param>
/// <param name="LateralToForward">Доля погашенной боковой скорости, переходящая в продольную (0 — выключено).</param>
/// <param name="Hp">Прочность корпуса.</param>
/// <param name="Shield">Ёмкость щита: урон сначала снимает щит (GDD §17).</param>
/// <param name="ShieldRegen">Восстановление щита в секунду после паузы без урона.</param>
/// <param name="Evasion">Базовое уклонение, % (боевой документ §39).</param>
/// <param name="MoveEvasion">Добавка к уклонению на полной скорости, % (§40).</param>
/// <param name="Cargo">Ёмкость трюма в единицах объёма (§46): сколько добычи влезает в корпус.</param>
/// <param name="Shield">
/// Щит, <paramref name="ShieldRegen"/>, <paramref name="Fuel"/> и <paramref name="Radar"/> — у NPC; у пилота их задают модули
/// (<see cref="Fitting.Effective"/>), а без modules.json — корпус, как до M9.
/// </param>
/// <param name="Class">Старший класс оборудования, которое встаёт на корпус (GDD §20): S, M или L.</param>
/// <param name="WeaponSlots">Оружейные слоты и их классы (GDD §12, §20); null — один слот класса корпуса.</param>
public sealed record HullParams(
    string Name,
    double MaxSpeed,
    double Acceleration,
    double BrakeAcceleration,
    double TurnRate,
    double LateralDampTime,
    double LateralToForward,
    double Size,
    double Hp = 400,
    double Shield = 150,
    double ShieldRegen = 20,
    double Evasion = 25,
    double MoveEvasion = 8,
    double Cargo = 20,
    double Fuel = 100,
    double Radar = 2000,
    string Class = EquipClass.L,
    IReadOnlyList<string>? WeaponSlots = null)
{
    /// <summary>Классы оружейных слотов по порядку.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Slots => WeaponSlots ?? [Class];

    /// <returns>Описание ошибки или null, если параметры годятся.</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (!(MaxSpeed > 0) || !(Acceleration > 0) || !(BrakeAcceleration > 0) || !(TurnRate > 0))
            return "maxSpeed, acceleration, brakeAcceleration and turnRate must be positive";
        if (!(LateralDampTime > 0)) return "lateralDampTime must be positive";
        if (!(LateralToForward >= 0 && LateralToForward <= 1)) return "lateralToForward must be within 0..1";
        if (!(Size > 0)) return "size must be positive";
        if (!(Hp > 0)) return "hp must be positive";
        if (!(Shield >= 0) || !(ShieldRegen >= 0)) return "shield and shieldRegen must not be negative";
        if (!(Evasion >= 0 && Evasion <= 100) || !(MoveEvasion >= 0 && MoveEvasion <= 100))
            return "evasion and moveEvasion must be within 0..100";
        if (!(Cargo >= 0)) return "cargo must not be negative";
        if (!(Fuel >= 0)) return "fuel must not be negative";
        if (!(Radar > 0)) return "radar must be positive";
        if (!EquipClass.IsValid(Class)) return "class must be S, M or L";
        if (Slots.Count is < 1 or > Fitting.MaxWeaponSlots) return $"weaponSlots must have 1..{Fitting.MaxWeaponSlots} slots";
        foreach (var slot in Slots)
        {
            if (!EquipClass.IsValid(slot)) return "weaponSlots must be S, M or L";
            if (EquipClass.Rank(slot) > EquipClass.Rank(Class)) return "a weapon slot must not be above the hull class";
        }
        return null;
    }
}

public static class Movement
{
    /// <summary>Мир — квадрат ±WorldHalfSize.</summary>
    public const double WorldHalfSize = 4000;

    private const double Tau = 2 * Math.PI;
    private const double DegToRad = Math.PI / 180;
    private const double DirectionEpsilon = 1e-6;

    /// <summary>Остаток бокового скольжения ниже этого гасится в ноль, иначе корабль вечно «ползёт».</summary>
    private const double LateralStopSpeed = 0.5;

    /// <summary>Угол в диапазон [−π, π).</summary>
    public static double WrapAngle(double a) => a - Tau * Math.Floor((a + Math.PI) / Tau);

    public static double MoveTowardsAngle(double current, double target, double maxDelta)
    {
        var diff = WrapAngle(target - current);
        if (Math.Abs(diff) <= maxDelta) return WrapAngle(target);
        return WrapAngle(current + (diff > 0 ? maxDelta : -maxDelta));
    }

    /// <summary>Один шаг симуляции.</summary>
    public static void Step(ref ShipState s, in MoveInput input, HullParams hull, double dt)
    {
        // 1. Разворот носом к желаемому направлению с ограничением TurnRate.
        if (input.Dx * input.Dx + input.Dy * input.Dy > DirectionEpsilon)
        {
            var desired = Math.Atan2(input.Dx, -input.Dy);
            s.Rot = MoveTowardsAngle(s.Rot, desired, hull.TurnRate * DegToRad * dt);
        }

        // 2. Скорость в осях корабля: forward = (fx, fy), right = (−fy, fx).
        var fx = Math.Sin(s.Rot);
        var fy = -Math.Cos(s.Rot);
        var vf = s.Vx * fx + s.Vy * fy;
        var vl = s.Vx * -fy + s.Vy * fx;

        // 3. Продольная скорость тянется к MaxSpeed·Throttle; через ноль не перескакивает.
        //    Тяга не разгоняет суммарную скорость (вместе с заносом) выше MaxSpeed·Throttle, но и не отнимает набранную.
        var throttle = Math.Min(1, Math.Max(0, input.Throttle));
        var target = hull.MaxSpeed * throttle;
        var lateral = Math.Abs(vl);
        if (vf < 0) vf = Math.Min(0, vf + hull.BrakeAcceleration * dt);
        else if (vf > target) vf = Math.Max(target, vf - hull.BrakeAcceleration * dt);
        else
        {
            var room = Math.Sqrt(Math.Max(0, target * target - vl * vl));
            if (vf < room) vf = Math.Min(room, vf + hull.Acceleration * dt);
        }

        // 4. Стабилизация бокового скольжения; без тяги тормозит сильнее.
        var damped = lateral * Math.Exp((-3 * dt) / hull.LateralDampTime);
        if (throttle == 0) damped = Math.Min(damped, lateral - hull.BrakeAcceleration * dt);
        if (damped < LateralStopSpeed) damped = 0;
        if (hull.LateralToForward > 0 && vf >= 0)
        {
            var room = Math.Sqrt(Math.Max(0, target * target - damped * damped));
            if (vf < room) vf = Math.Min(room, vf + (lateral - damped) * hull.LateralToForward);
        }
        var newVl = vl < 0 ? -damped : damped;

        s.Vx = fx * vf - fy * newVl;
        s.Vy = fy * vf + fx * newVl;
        s.X += s.Vx * dt;
        s.Y += s.Vy * dt;

        // 5. Граница мира: упираемся, наружная скорость гасится.
        if (s.X > WorldHalfSize)
        {
            s.X = WorldHalfSize;
            if (s.Vx > 0) s.Vx = 0;
        }
        else if (s.X < -WorldHalfSize)
        {
            s.X = -WorldHalfSize;
            if (s.Vx < 0) s.Vx = 0;
        }
        if (s.Y > WorldHalfSize)
        {
            s.Y = WorldHalfSize;
            if (s.Vy > 0) s.Vy = 0;
        }
        else if (s.Y < -WorldHalfSize)
        {
            s.Y = -WorldHalfSize;
            if (s.Vy < 0) s.Vy = 0;
        }
    }
}
