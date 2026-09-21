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

/// <summary>
/// Уклонение от жара звезды (M15.7). Всякий NPC, который летит к точке, проводит своё направление через
/// <see cref="Avoid"/>: внутри запретного круга его тянет наружу и вбок — в ту сторону, куда цель, — так что
/// корабль звезду **огибает**, а не упирается в неё лбом и не сгорает по дороге на другую сторону системы.
/// Игрока это не касается: он рулит сам, ему только пишут в ленту, что горячо.
/// </summary>
public static class Heat
{
    /// <summary>От края зоны жара NPC держится на столько дальше; внутри этого запаса уже сворачивает.</summary>
    public const double Margin = 350;

    /// <summary>
    /// Поправить направление полёта так, чтобы обойти жар звезды в центре системы.
    /// </summary>
    /// <param name="x">Где корабль сейчас.</param>
    /// <param name="y">Где корабль сейчас.</param>
    /// <param name="ux">Куда он хочет лететь, единичный вектор.</param>
    /// <param name="uy">Куда он хочет лететь, единичный вектор.</param>
    /// <param name="burnRadius">Радиус зоны жара; 0 — звезды нет, направление не меняется.</param>
    /// <returns>Направление с поправкой; нормировать его не нужно — <see cref="MoveInput"/> сделает это сам.</returns>
    public static (double X, double Y) Avoid(double x, double y, double ux, double uy, double burnRadius)
    {
        if (burnRadius <= 0) return (ux, uy);
        var r = Math.Sqrt(x * x + y * y);
        var safe = burnRadius + Margin;
        if (r >= safe || r <= 1e-6) return (ux, uy);
        var ox = x / r;
        var oy = y / r;
        // Чем глубже в запретном круге, тем сильнее тянет прочь от центра.
        var push = (safe - r) / Margin * 2;
        // Вбок — в ту сторону, куда цель: иначе корабль вставал бы носом в звезду и полз вдоль её края.
        var side = ox * uy - oy * ux >= 0 ? 1 : -1;
        return (ux + (ox - side * oy) * push, uy + (oy + side * ox) * push);
    }
}

/// <summary>
/// Поселение на планете (M15): то же место, что станция, — док, магазин, рынок, доска заданий и своя репутация.
/// Планета без него остаётся декорацией: сесть нельзя, кнопки посадки нет.
/// </summary>
/// <param name="Name">Как зовётся поселение; null — по имени планеты.</param>
/// <param name="Scene">Набор фонов дока: desert, ice, jungle, lava, barren, orbital-platform; null — земной.</param>
/// <param name="Shipyard">Здесь продают и меняют корпуса. Верфь есть не везде — в этом и разница со станцией.</param>
public sealed record SettlementDef(string? Name = null, string? Scene = null, bool Shipyard = false)
{
    public string? Validate()
    {
        if (Name is not null && string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (Scene is not null && string.IsNullOrWhiteSpace(Scene)) return "scene is empty";
        return null;
    }
}

/// <summary>Планета: ходит по орбите, выбирается прицелом, сквозь неё можно пролететь. С поселением — ещё и место посадки (M15).</summary>
/// <param name="Kind">Вид для клиента: terran, desert, ice, gas.</param>
/// <param name="Size">Радиус планеты в мире.</param>
/// <param name="Id">Id планеты, уникальный на всю галактику; обязателен там, где есть поселение.</param>
/// <param name="Settlement">Поселение (M15); null — планета необитаема, сесть нельзя.</param>
public sealed record PlanetDef(
    string Name,
    string Kind,
    double Size,
    OrbitDef Orbit,
    string? Id = null,
    SettlementDef? Settlement = null)
{
    /// <summary>Как звать место посадки: имя поселения, если задано, иначе имя планеты.</summary>
    public string PlaceName => Settlement?.Name ?? Name;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (string.IsNullOrWhiteSpace(Kind)) return "kind is empty";
        if (!(Size > 0)) return "size must be positive";
        if (Id is not null && string.IsNullOrWhiteSpace(Id)) return "id is empty";
        // Без id поселение не к чему привязать: по нему ключуются магазин, рынок, задания и репутация места.
        if (Settlement is not null && Id is null) return "a settlement needs an id";
        if (Settlement?.Validate() is { } settlement) return $"settlement: {settlement}";
        return Orbit is null ? "orbit is missing" : Orbit.Validate() is { } orbit ? $"orbit: {orbit}" : null;
    }
}
