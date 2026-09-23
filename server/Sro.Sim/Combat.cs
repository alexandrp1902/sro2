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
/// <param name="CloseRange">
/// Ближе этого пушке мешает собственная неповоротливость: штраф растёт к <paramref name="ClosePenalty"/> в упор.
/// 0 — штрафа за близость нет. Так дальнобойные орудия становятся снайперскими, а скорострельные — оружием свалки.
/// </param>
/// <param name="ClosePenalty">Штраф к шансу в упор, %; от CloseRange падает линейно до нуля.</param>
/// <param name="Class">Класс (GDD §20): встаёт в оружейный слот того же класса или старше.</param>
/// <param name="Power">Сколько энергии генератора забирает (GDD §18).</param>
/// <param name="Missile">Ракетница (боевой документ §37): пушка запускает самонаводящуюся ракету вместо броска на попадание.</param>
/// <param name="ShieldFactor">Множитель урона по щиту (ионный разрядник — 2).</param>
/// <param name="HullFactor">Множитель урона по корпусу (ионный разрядник — 0.3).</param>
/// <param name="Slow">Попадание замедляет цель: доля, на которую падают скорость и разгон (0.4 — на 40 %).</param>
/// <param name="SlowSeconds">Сколько длится замедление.</param>
/// <param name="Intercept">Зенитка (M11): сама бьёт ракеты и торпеды, летящие рядом, даже без цели и без огня.</param>
/// <param name="BlastRadius">
/// Урон по площади (M15.5): попадание рвётся в точке цели и задевает соседей в этом радиусе. 0 — осколков нет.
/// Взрыв именно в точке попадания, а не задевание по дороге: промах не взрывается вовсе.
/// </param>
/// <param name="BlastShare">Доля урона пушки, которую получает сосед в самом эпицентре.</param>
/// <param name="BlastFalloff">Показатель спада к краю: 1 — линейный, больше — круче.</param>
/// <param name="DamageType">
/// Чем бьёт (M15.6) — от этого зависит, какая защита цели может попадание отбить: <see cref="DamageTypes"/>.
/// У ракетницы не задаётся: её вид урона следует из самого наличия ракеты, и блокировать её нельзя — сбивают.
/// </param>
/// <param name="Tier">Тир Mk1–Mk3 (<see cref="Tiers"/>): в файле всегда 1, старшие тиры раскрываются при разборе.</param>
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
    string Color = "#ffd166",
    double CloseRange = 0,
    double ClosePenalty = 0,
    string Class = EquipClass.S,
    double Power = 0,
    MissileParams? Missile = null,
    double ShieldFactor = 1,
    double HullFactor = 1,
    double Slow = 0,
    double SlowSeconds = 0,
    InterceptParams? Intercept = null,
    double BlastRadius = 0,
    double BlastShare = 0,
    double BlastFalloff = 1.5,
    string DamageType = DamageTypes.Kinetic,
    int Pellets = 1,
    double Spread = 0,
    int Salvo = 1,
    int Tier = 1)
{
    [System.Text.Json.Serialization.JsonIgnore] public int SlowTicks => Combat.SecondsToTicks(SlowSeconds);

    /// <summary>
    /// Вид урона на самом деле: у ракетницы он следует из ракеты, поэтому в файле его не пишут.
    /// Защита смотрит именно сюда, а не в <see cref="DamageType"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Hits => Missile is not null ? DamageTypes.Missile : DamageType;

    public const string MissileKind = "missile";

    /// <summary>Больше дробин за нажатие не бывает: каждая — свой бросок, свой урон и своя строка в ленте.</summary>
    public const int MaxPellets = 8;

    /// <summary>Больше ракет в залпе не бывает: каждая летит и сбивается сама.</summary>
    public const int MaxSalvo = 6;

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
        if (!(CloseRange >= 0) || CloseRange > OptimalRange)
            return "closeRange must be within 0..optimalRange";
        if (!(ClosePenalty >= 0 && ClosePenalty <= 100)) return "closePenalty must be within 0..100";
        if (!EquipClass.IsValid(Class)) return "class must be S, M or L";
        if (!(Power >= 0)) return "power must not be negative";
        if ((Kind == MissileKind) != (Missile is not null)) return "kind 'missile' and the missile block go together";
        if (Missile?.Validate() is { } missile) return $"missile: {missile}";
        if (!(ShieldFactor >= 0) || !(HullFactor >= 0)) return "shieldFactor and hullFactor must not be negative";
        if (!(Slow >= 0 && Slow <= 0.9) || !(SlowSeconds >= 0)) return "slow must be within 0..0.9, slowSeconds must not be negative";
        if (Intercept?.Validate() is { } intercept) return $"intercept: {intercept}";
        if (Intercept is not null && Missile is not null) return "a missile launcher cannot intercept";
        if (!(BlastRadius >= 0)) return "blastRadius must not be negative";
        if (BlastRadius > MaxRange) return "blastRadius must not exceed maxRange";
        if (!(BlastShare >= 0 && BlastShare <= 1)) return "blastShare must be within 0..1";
        if (!(BlastFalloff >= 0.5 && BlastFalloff <= 4)) return "blastFalloff must be within 0.5..4";
        if (BlastRadius > 0 && !(BlastShare > 0)) return "blastRadius without blastShare does nothing";
        if (!DamageTypes.IsValid(DamageType)) return $"damageType must be one of {DamageTypes.All}";
        // Вид «ракета» в файле не пишут: одна правда — сам блок missile. Иначе можно было бы описать
        // ракетницу, которую отбивает броня, и такую же ракету, которую нет.
        if (DamageType == DamageTypes.Missile) return "damageType 'missile' is implied by the missile block, not written";
        // Дробовик и залп (M19): damage у них — за одну дробину и за одну ракету, иначе тиры Mk2/Mk3
        // (они умножают только damage) множили бы урон дважды.
        if (Pellets is < 1 or > MaxPellets) return $"pellets must be within 1..{MaxPellets}";
        if (!(Spread >= 0 && Spread <= 45)) return "spread must be within 0..45";
        if (Spread > 0 && Pellets < 2) return "spread without pellets does nothing";
        if (Pellets > 1 && Missile is not null) return "a missile launcher does not fire pellets";
        if (Salvo is < 1 or > MaxSalvo) return $"salvo must be within 1..{MaxSalvo}";
        if (Salvo > 1 && Missile is null) return "salvo needs a missile block";
        return null;
    }
}

/// <summary>
/// Виды урона (M15.6): от вида зависит, чем от него защищаются. Кинетику держит динамическая защита,
/// энергию рассеивает аэрозольная завеса, а ракету не блокируют вовсе — её сбивают на подлёте
/// (<see cref="InterceptParams"/>).
/// </summary>
public static class DamageTypes
{
    public const string Kinetic = "kinetic";
    public const string Energy = "energy";
    public const string Missile = "missile";

    public const string All = "kinetic, energy";

    public static bool IsValid(string? type) => type is Kinetic or Energy or Missile;
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

    /// <summary>
    /// Псевдо-пушка осколков в <c>ShotDto.W</c> (M15.5): трассера у такой записи нет, рисуется только
    /// попадание на соседе. Тот же приём, что у тарана метеорита (<see cref="MeteorRules.RamWeapon"/>),
    /// и он позволяет не ломать семиполевой ShotDto и его бинарный кодек.
    /// </summary>
    public const string SplashWeapon = "splash";

    /// <summary>
    /// Псевдо-пушка противоракетного комплекса (M15.6): он не занимает оружейного слота, и его выстрел
    /// нечем назвать. Третий такой после <see cref="SplashWeapon"/> и <see cref="MeteorRules.RamWeapon"/>.
    /// </summary>
    public const string GuardWeapon = "guard";

    /// <summary>Уклонение цели, % (§39–40): базовое плюс добавка, растущая со скоростью.</summary>
    public static double Evasion(HullParams hull, double speed) =>
        hull.Evasion + hull.MoveEvasion * Math.Clamp(speed / hull.MaxSpeed, 0, 1);

    /// <summary>
    /// Штраф за дистанцию, % (GDD §16). Два склона: от OptimalRange растёт до RangePenalty на MaxRange,
    /// и — если у пушки задан CloseRange — от него растёт до ClosePenalty в упор. Между ними штрафа нет.
    /// </summary>
    public static double RangePenalty(WeaponParams weapon, double distance)
    {
        if (distance > weapon.OptimalRange)
        {
            var span = weapon.MaxRange - weapon.OptimalRange;
            if (span <= 0) return weapon.RangePenalty;
            return weapon.RangePenalty * Math.Min(1, (distance - weapon.OptimalRange) / span);
        }
        if (weapon.CloseRange > 0 && distance < weapon.CloseRange)
            return weapon.ClosePenalty * (1 - distance / weapon.CloseRange);
        return 0;
    }

    public static bool InRange(WeaponParams weapon, double distance) => distance <= weapon.MaxRange;

    /// <summary>Шанс попадания, % (GDD §46): точность − уклонение − штраф за дистанцию, в пределах 5…95.</summary>
    public static double HitChance(WeaponParams weapon, double distance, HullParams target, double targetSpeed) =>
        HitChance(weapon, distance, Evasion(target, targetSpeed));

    /// <summary>Шанс попадания по цели с готовым уклонением, % — для целей без корпуса (метеорит уклоняться не умеет).</summary>
    public static double HitChance(WeaponParams weapon, double distance, double evasion) =>
        Math.Clamp(weapon.Accuracy - evasion - RangePenalty(weapon, distance), MinHitChance, MaxHitChance);

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

    /// <summary>
    /// Урон снимает сначала щит, остаток — корпус (GDD §17). Корпус не уходит ниже нуля.
    /// Множители (ионный разрядник): по щиту урон × shieldFactor; что щит не принял — в исходных единицах × hullFactor
    /// по корпусу.
    /// </summary>
    public static DamageResult ApplyDamage(ref double hp, ref double shield, double damage, double shieldFactor = 1, double hullFactor = 1)
    {
        var absorbed = shieldFactor > 0 ? Math.Min(shield, damage * shieldFactor) : 0;
        shield -= absorbed;
        var rest = shieldFactor > 0 ? damage - absorbed / shieldFactor : damage;
        var hull = Math.Min(hp, Math.Max(0, rest) * hullFactor);
        hp -= hull;
        return new DamageResult(absorbed, hull);
    }

    /// <summary>Урон пушки с её множителями по щиту и корпусу.</summary>
    public static DamageResult ApplyDamage(ref double hp, ref double shield, WeaponParams weapon) =>
        ApplyDamage(ref hp, ref shield, weapon.Damage, weapon.ShieldFactor, weapon.HullFactor);

    /// <summary>
    /// Осколочный урон соседу (M15.5): доля урона пушки, спадающая от эпицентра к краю радиуса.
    /// </summary>
    /// <param name="distance">От эпицентра до брони соседа, а не до его центра: крупный корпус ловит осколки бортом.</param>
    public static double Splash(WeaponParams weapon, double distance)
    {
        var radius = weapon.BlastRadius;
        if (!(radius > 0) || !(weapon.BlastShare > 0)) return 0;
        if (!(distance < radius)) return 0;
        var reach = 1 - Math.Max(0, distance) / radius;
        return weapon.Damage * weapon.BlastShare * Math.Pow(reach, weapon.BlastFalloff);
    }

    /// <summary>Перезарядка в тиках: выстрел не чаще раза за столько тиков.</summary>
    /// <param name="scale">Множитель от охлаждения (<see cref="Fitting.CooldownScale"/>); 1 — без него.</param>
    public static int CooldownTicks(WeaponParams weapon, double scale = 1) =>
        Math.Max(1, (int)Math.Ceiling(weapon.Cooldown * scale * SimConfig.TickRate - 1e-9));

    public static int SecondsToTicks(double seconds) => (int)Math.Round(seconds * SimConfig.TickRate);
}

/// <summary>Зенитка (M11) и противоракетный комплекс (M15.6): сбивают ракеты и торпеды в радиусе.</summary>
/// <param name="Range">Ракета ближе этого к кораблю — под огнём.</param>
/// <param name="Chance">Шанс попасть по ракете, %.</param>
/// <param name="Cooldown">
/// Своя перезарядка, секунды. 0 — брать у пушки-хозяина: так работает зенитка, которая стоит в оружейном
/// слоте. Модулю занять её не у кого, поэтому у него это поле обязательно.
/// </param>
/// <param name="Damage">Урон по ракете. 0 — брать у пушки-хозяина, по той же причине.</param>
public sealed record InterceptParams(double Range = 350, double Chance = 60, double Cooldown = 0, double Damage = 0)
{
    public string? Validate()
    {
        if (!(Range > 0)) return "range must be positive";
        if (!(Chance > 0 && Chance <= 100)) return "chance must be within 0..100";
        if (!(Cooldown >= 0) || !(Damage >= 0)) return "cooldown and damage must not be negative";
        return null;
    }

    /// <summary>Комплекс сам по себе, без пушки-хозяина: перезарядка и урон должны быть свои.</summary>
    public string? ValidateStandalone() =>
        Validate() ?? (!(Cooldown > 0) || !(Damage > 0) ? "a module needs its own cooldown and damage" : null);
}
