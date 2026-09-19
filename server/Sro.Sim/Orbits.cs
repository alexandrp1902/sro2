namespace Sro.Sim;

/// <summary>
/// Круговая орбита вокруг звезды в центре системы. Положение — функция времени: сервер и клиент считают его
/// одной формулой от одних и тех же секунд, поэтому по сети орбиты не передаются, только параметры.
/// Зеркало: client/src/sim/orbits.ts.
/// </summary>
/// <param name="Radius">Расстояние до звезды; 0 — тело стоит в центре.</param>
/// <param name="PeriodMinutes">Один оборот за столько минут; отрицательное — в обратную сторону.</param>
/// <param name="Phase">Угол в градусах в момент 0 орбитального времени.</param>
public sealed record OrbitDef(double Radius, double PeriodMinutes = 60, double Phase = 0)
{
    /// <summary>Неподвижно в центре — станция системы без galaxy.json, как до орбит.</summary>
    public static readonly OrbitDef Center = new(0);

    /// <summary>Угол на орбите в радианах: 0 — вправо от звезды, растёт по часовой стрелке на экране (y вниз).</summary>
    public double Angle(double seconds)
    {
        var turns = PeriodMinutes == 0 ? 0 : seconds / (PeriodMinutes * 60);
        // Только дробная часть оборотов: секунды — это unix-время, и точность угла не должна от него зависеть.
        return Phase * Math.PI / 180 + (turns - Math.Floor(turns)) * 2 * Math.PI;
    }

    public (double X, double Y) At(double seconds)
    {
        if (Radius <= 0) return (0, 0);
        var angle = Angle(seconds);
        return (Radius * Math.Cos(angle), Radius * Math.Sin(angle));
    }

    /// <summary>
    /// Точка в осях станции → мир. Оси вращаются вместе со станцией: +y — прочь от звезды, +x — вдоль орбиты.
    /// Так дроны, точка появления и вылет из дока держатся одной стороны станции, где бы та ни была.
    /// У станции в центре оси мировые.
    /// </summary>
    public (double X, double Y) ToWorld(double seconds, double localX, double localY)
    {
        if (Radius <= 0) return (localX, localY);
        var (sx, sy) = At(seconds);
        var (sin, cos) = Math.SinCos(Angle(seconds));
        return (sx + localX * sin + localY * cos, sy - localX * cos + localY * sin);
    }

    /// <summary>Мир → оси станции, обратное к <see cref="ToWorld"/>.</summary>
    public (double X, double Y) ToLocal(double seconds, double x, double y)
    {
        if (Radius <= 0) return (x, y);
        var (sx, sy) = At(seconds);
        var (sin, cos) = Math.SinCos(Angle(seconds));
        var dx = x - sx;
        var dy = y - sy;
        return (dx * sin - dy * cos, dx * cos + dy * sin);
    }

    public string? Validate()
    {
        if (!(Radius >= 0) || Radius > Movement.WorldHalfSize) return $"radius must be within 0..{Movement.WorldHalfSize}";
        if (!double.IsFinite(PeriodMinutes) || PeriodMinutes == 0) return "periodMinutes must not be zero";
        if (!double.IsFinite(Phase)) return "phase must be a number";
        return null;
    }
}

/// <summary>
/// Звезда в центре системы (GDD §4). Ближе BurnRadius жжёт корабли: сначала щит, потом корпус; у самого диска —
/// BurnDps в секунду, к краю зоны — до нуля.
/// </summary>
/// <param name="Kind">Вид для клиента: yellow, orange, blue, red.</param>
/// <param name="Radius">Радиус диска.</param>
public sealed record SunDef(string Kind = "yellow", double Radius = 220, double BurnRadius = 600, double BurnDps = 150)
{
    /// <summary>Урон в секунду на расстоянии distance от центра; 0 — вне зоны жара.</summary>
    public double BurnAt(double distance)
    {
        if (BurnDps <= 0 || distance >= BurnRadius) return 0;
        if (distance <= Radius) return BurnDps;
        return BurnDps * (BurnRadius - distance) / (BurnRadius - Radius);
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Kind)) return "kind is empty";
        if (!(Radius > 0)) return "radius must be positive";
        if (!(BurnRadius >= Radius) || BurnRadius > Movement.WorldHalfSize) return $"burnRadius must be within radius..{Movement.WorldHalfSize}";
        if (!(BurnDps >= 0)) return "burnDps must not be negative";
        return null;
    }
}

/// <summary>Планета: ходит по орбите, выбирается прицелом, сквозь неё можно пролететь (посадки пока нет).</summary>
/// <param name="Kind">Вид для клиента: terran, desert, ice, gas.</param>
/// <param name="Size">Радиус планеты в мире.</param>
public sealed record PlanetDef(string Name, string Kind, double Size, OrbitDef Orbit)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (string.IsNullOrWhiteSpace(Kind)) return "kind is empty";
        if (!(Size > 0)) return "size must be positive";
        return Orbit is null ? "orbit is missing" : Orbit.Validate() is { } orbit ? $"orbit: {orbit}" : null;
    }
}
