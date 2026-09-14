namespace Sro.Sim;

// Бой: шанс попадания, сектор стрельбы, щит и корпус (GDD §14–17, §45–47; боевой документ v0.2, §34–41).
// Зеркало — client/src/sim/combat.ts; совпадение проверяет shared/test-vectors/combat.json.

/// <summary>Параметры пушки (GDD §14). Хранятся в shared/weapons.json.</summary>
/// <param name="Accuracy">Точность, %.</param>
/// <param name="Cooldown">Перезарядка, секунды.</param>
/// <param name="OptimalRange">До этой дистанции штрафа за дальность нет (§16).</param>
/// <param name="MaxRange">Дальше выстрел невозможен.</param>
/// <param name="RangePenalty">Штраф к шансу на MaxRange, %; от OptimalRange растёт линейно.</param>
/// <param name="Arc">Сектор стрельбы от носа в каждую сторону, градусы (боевой документ §35).</param>
/// <param name="Kind">Вид трассера на клиенте: bolt — снаряд, beam — луч, orb — плазменный шар.</param>
/// <param name="Color">Цвет трассера, #rrggbb.</param>
public sealed record WeaponParams(
    string Name,
    double Damage,
    double Accuracy,
    double Cooldown,
    double OptimalRange,
    double MaxRange,
    double RangePenalty,
    double Arc = 60,
    string Kind = "bolt",
    string Color = "#ffd166")
{
    /// <returns>Описание ошибки или null, если параметры годятся.</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (!(Damage > 0) || !(Cooldown > 0)) return "damage and cooldown must be positive";
        if (!(Accuracy >= 0 && Accuracy <= 100)) return "accuracy must be within 0..100";
        if (!(OptimalRange >= 0) || !(MaxRange > 0) || !(MaxRange >= OptimalRange))
            return "ranges must satisfy 0 <= optimalRange <= maxRange, maxRange > 0";
        if (!(RangePenalty >= 0 && RangePenalty <= 100)) return "rangePenalty must be within 0..100";
        if (!(Arc > 0 && Arc <= 180)) return "arc must be within 0..180";
        return null;
    }
}

/// <summary>Сколько урона пришлось на щит и сколько на корпус.</summary>
public readonly record struct DamageResult(double Shield, double Hull);

public static class Combat
{
    /// <summary>Шанс попадания всегда в этих пределах, % (GDD §15).</summary>
    public const double MinHitChance = 5;
    public const double MaxHitChance = 95;

    private const double DegToRad = Math.PI / 180;
    /// <summary>Цель ровно на границе сектора — в секторе, несмотря на погрешность atan2.</summary>
    private const double ArcEpsilon = 1e-9;
    private const double SameSpotSq = 1e-9;

    /// <summary>Уклонение цели, % (§39–40): базовое плюс добавка, растущая со скоростью.</summary>
    public static double Evasion(HullParams hull, double speed) =>
        hull.Evasion + hull.MoveEvasion * Math.Clamp(speed / hull.MaxSpeed, 0, 1);

    /// <summary>Штраф за дистанцию, % (GDD §16): 0 до OptimalRange, дальше линейно до RangePenalty на MaxRange.</summary>
    public static double RangePenalty(WeaponParams weapon, double distance)
    {
        if (distance <= weapon.OptimalRange) return 0;
        var span = weapon.MaxRange - weapon.OptimalRange;
        if (span <= 0) return weapon.RangePenalty;
        return weapon.RangePenalty * Math.Min(1, (distance - weapon.OptimalRange) / span);
    }

    public static bool InRange(WeaponParams weapon, double distance) => distance <= weapon.MaxRange;

    /// <summary>Шанс попадания, % (GDD §46): точность − уклонение − штраф за дистанцию, в пределах 5…95.</summary>
    public static double HitChance(WeaponParams weapon, double distance, HullParams target, double targetSpeed) =>
        Math.Clamp(weapon.Accuracy - Evasion(target, targetSpeed) - RangePenalty(weapon, distance), MinHitChance, MaxHitChance);

    /// <param name="roll">Случайное число из [0, 1).</param>
    public static bool IsHit(double chance, double roll) => roll * 100 < chance;

    /// <summary>
    /// Цель в секторе стрельбы (§35): угол между носом и направлением на цель не больше arcDeg.
    /// (dx, dy) — от стрелка к цели; корабли в одной точке считаются в секторе.
    /// </summary>
    public static bool InArc(double rot, double dx, double dy, double arcDeg)
    {
        if (dx * dx + dy * dy < SameSpotSq) return true;
        var bearing = Math.Atan2(dx, -dy);
        return Math.Abs(Movement.WrapAngle(bearing - rot)) <= arcDeg * DegToRad + ArcEpsilon;
    }

    /// <summary>Урон снимает сначала щит, остаток — корпус (GDD §17). Корпус не уходит ниже нуля.</summary>
    public static DamageResult ApplyDamage(ref double hp, ref double shield, double damage)
    {
        var absorbed = Math.Min(shield, damage);
        shield -= absorbed;
        var hull = Math.Min(hp, damage - absorbed);
        hp -= hull;
        return new DamageResult(absorbed, hull);
    }

    /// <summary>Перезарядка в тиках: выстрел не чаще раза за столько тиков.</summary>
    public static int CooldownTicks(WeaponParams weapon) =>
        Math.Max(1, (int)Math.Ceiling(weapon.Cooldown * SimConfig.TickRate - 1e-9));

    public static int SecondsToTicks(double seconds) => (int)Math.Round(seconds * SimConfig.TickRate);
}
