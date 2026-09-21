namespace Sro.Sim.Tests;

/// <summary>
/// Уклонение NPC от жара звезды (M15.7). До него пираты летели к точке напрямик, сквозь центр системы,
/// и сгорали по дороге; теперь всякий курс проходит через <see cref="Heat.Avoid"/>.
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
