namespace Sro.Sim.Tests;

/// <summary>
/// Запретная зона вокруг звезды (M15.7, упреждающая с M16a). До неё пираты летели к точке напрямик,
/// сквозь центр системы, и сгорали по дороге; теперь всякий курс проходит через <see cref="Heat.Avoid"/>,
/// всякая выбранная точка — через <see cref="Heat.SafePoint"/>, а дальний перелёт — через
/// <see cref="Heat.Detour"/>.
/// </summary>
public class HeatTests
{
    private const double Burn = 600;

    /// <summary>Единичный вектор направления: сравнивать удобнее углы, а не длины.</summary>
    private static (double X, double Y) Unit((double X, double Y) v)
    {
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return (v.X / length, v.Y / length);
    }

    [Fact]
    public void FarFromTheStar_TheCourseIsUntouched()
    {
        Assert.Equal((0, -1), Heat.Avoid(0, -3000, 0, -1, Burn));
        // Ровно на границе запаса поправки ещё нет.
        Assert.Equal((0, -1), Heat.Avoid(0, -(Burn + Heat.Margin), 0, -1, Burn));
    }

    [Fact]
    public void WithoutAStar_TheCourseIsUntouched()
    {
        Assert.Equal((1, 0), Heat.Avoid(0, 0, 1, 0, 0));
    }

    [Fact]
    public void InsideTheHeat_TheCourseTurnsAwayFromTheCentre()
    {
        // Корабль слева от звезды и летит прямо в неё.
        var (x, y) = Unit(Heat.Avoid(-300, 0, 1, 0, Burn));
        Assert.True(x < 0, $"должен разворачиваться прочь от центра, а не лететь в него: x={x}");
        Assert.True(Math.Abs(y) > 0.3, $"и уходить вбок, чтобы обогнуть звезду: y={y}");
    }

    [Fact]
    public void TheDeeperInside_TheStrongerThePush()
    {
        var shallow = Unit(Heat.Avoid(-(Burn + Heat.Margin - 50), 0, 1, 0, Burn));
        var deep = Unit(Heat.Avoid(-100, 0, 1, 0, Burn));
        Assert.True(deep.X < shallow.X, $"глубже — сильнее прочь: {deep.X} должно быть меньше {shallow.X}");
    }

    /// <summary>
    /// Главная правка M16a: корабль сворачивает **до** того, как войдёт в зону. Быстрый NPC, идущий
    /// точно в звезду, раньше успевал влететь в жар прежде, чем реактивная поправка успевала сработать.
    /// </summary>
    [Fact]
    public void HeadingStraightAtTheStar_TheCourseBendsBeforeTheHeat()
    {
        const double speed = 200;
        var far = -(Burn + Heat.Margin + 250); // ещё вне зоны, но идёт прямо в центр
        Assert.Equal((0.0, 1.0), Heat.Avoid(0, far, 0, 1, Burn)); // без скорости — как раньше, под ноги
        var (x, y) = Unit(Heat.Avoid(0, far, 0, 1, Burn, speed));
        Assert.True(Math.Abs(x) > 0.1, $"на скорости курс должен отворачивать заранее: x={x}");
        Assert.True(y > 0, $"но всё ещё вперёд, а не назад: y={y}");
    }

    [Fact]
    public void ACourseAlongsideTheStar_IsLeftAlone()
    {
        // Идёт мимо, в стороне от зоны: упреждение не должно шарахаться от каждой звезды в системе.
        var far = Burn + Heat.Margin + 400;
        Assert.Equal((0.0, 1.0), Heat.Avoid(far, -3000, 0, 1, Burn, 200));
    }

    [Fact]
    public void SafePoint_PushesAPointOutOfTheZone()
    {
        var safe = Heat.SafeRadius(Burn);
        // Точка внутри выносится наружу по тому же лучу — направление сохраняется.
        var (x, y) = Heat.SafePoint(300, 0, Burn);
        Assert.Equal(safe, x, 6);
        Assert.Equal(0, y, 6);
        Assert.False(Heat.Inside(x, y, Burn));

        // Точка снаружи не трогается вовсе.
        Assert.Equal((2000.0, 0.0), Heat.SafePoint(2000, 0, Burn));
        // И даже из самого центра получается годная точка, а не деление на ноль.
        Assert.False(Heat.Inside(Heat.SafePoint(0, 0, Burn).X, Heat.SafePoint(0, 0, Burn).Y, Burn));
    }

    [Fact]
    public void Detour_SendsTheShipAroundAStarInTheWay()
    {
        // Цель ровно по ту сторону звезды: лететь надо не в неё, а мимо.
        var (x, y) = Heat.Detour(-2000, 0, 2000, 0, Burn);
        Assert.False(Heat.Inside(x, y, Burn));
        Assert.True(Math.Abs(y) > Burn, $"обход должен уходить вбок от линии: y={y}");

        // Цель сбоку, звезда не на пути — летим прямо к ней.
        Assert.Equal((2000.0, 3000.0), Heat.Detour(-2000, 3000, 2000, 3000, Burn));
        // Звезды нет — обходить нечего.
        Assert.Equal((2000.0, 0.0), Heat.Detour(-2000, 0, 2000, 0, 0));
    }

    [Fact]
    public void TheSidewaysPush_FollowsWhereTheTargetIs()
    {
        // Слева от звезды, цель за ней сверху — обходить надо сверху, снизу — снизу.
        var up = Unit(Heat.Avoid(-300, 0, 1, -1, Burn));
        var down = Unit(Heat.Avoid(-300, 0, 1, 1, Burn));
        Assert.True(up.Y < 0, $"цель сверху — обход сверху: y={up.Y}");
        Assert.True(down.Y > 0, $"цель снизу — обход снизу: y={down.Y}");
    }
}
