namespace Sro.Sim.Tests;

public class MovementTests
{
    private const double Dt = SimConfig.Dt;

    private static ShipState FlyingUp(double speed) => new() { Vy = -speed };

    private static double Speed(in ShipState s) => Math.Sqrt(s.Vx * s.Vx + s.Vy * s.Vy);

    private static double Forward(in ShipState s) => s.Vx * Math.Sin(s.Rot) - s.Vy * Math.Cos(s.Rot);

    [Fact]
    public void WrapAngle_KeepsAnglesWithinMinusPiToPi()
    {
        Assert.Equal(-Math.PI, Movement.WrapAngle(Math.PI), 12);
        Assert.Equal(0.5, Movement.WrapAngle(0.5 + 4 * Math.PI), 12);
        Assert.Equal(-0.5, Movement.WrapAngle(-0.5 - 2 * Math.PI), 12);
    }

    [Fact]
    public void MoveTowardsAngle_TakesTheShortWayAcrossPi()
    {
        var rot = Movement.MoveTowardsAngle(3.0, -3.0, 0.1);
        Assert.Equal(3.1, rot, 12);
        Assert.Equal(-3.0, Movement.MoveTowardsAngle(3.1, -3.0, 0.5), 12);
    }

    [Fact]
    public void MoveTowardsAngle_DoesNotOvershoot()
    {
        Assert.Equal(1.0, Movement.MoveTowardsAngle(0.9, 1.0, 0.5), 12);
    }

    [Fact]
    public void ExactReversal_TurnsTheSameWayEveryTime()
    {
        // Ровно 180°: wrap даёт −π, поворот идёт в отрицательную сторону — и на сервере, и на клиенте.
        var s = new ShipState();
        Movement.Step(ref s, new MoveInput(0, 1, 0), TestHulls.Medium, Dt);
        Assert.Equal(-100 * Math.PI / 180 * Dt, s.Rot, 12);
    }

    [Fact]
    public void Light_ReachesMaxSpeedInAboutMaxSpeedOverAcceleration()
    {
        var s = new ShipState();
        var steps = 0;
        while (Speed(s) < TestHulls.Light.MaxSpeed && steps < 1000)
        {
            Movement.Step(ref s, new MoveInput(0, -1, 1), TestHulls.Light, Dt);
            steps++;
        }
        Assert.InRange(steps * Dt, 330.0 / 180 - Dt, 330.0 / 180 + Dt);
        Assert.Equal(TestHulls.Light.MaxSpeed, Speed(s), 9);
    }

    [Fact]
    public void Light_StopsInAboutMaxSpeedOverBrake()
    {
        var s = FlyingUp(330);
        var steps = 0;
        while (Speed(s) > 0 && steps < 1000)
        {
            Movement.Step(ref s, new MoveInput(0, -1, 0), TestHulls.Light, Dt);
            steps++;
        }
        Assert.InRange(steps * Dt, 1.5 - Dt, 1.5 + Dt);
    }

    [Fact]
    public void PartialThrottle_CapsSpeedAtMaxSpeedTimesThrottle()
    {
        var s = new ShipState();
        for (var i = 0; i < 200; i++) Movement.Step(ref s, new MoveInput(0, -1, 0.3), TestHulls.Light, Dt);
        Assert.Equal(99, Speed(s), 9);

        for (var i = 0; i < 200; i++) Movement.Step(ref s, new MoveInput(0, -1, 0.1), TestHulls.Light, Dt);
        Assert.Equal(33, Speed(s), 9);
    }

    [Fact]
    public void Reversal_NeverExceedsMaxSpeedAndEndsFlyingNoseFirst()
    {
        var hull = TestHulls.Medium;
        var s = FlyingUp(hull.MaxSpeed);
        var reverse = new MoveInput(0, 1, 1);
        for (var i = 0; i < 200; i++)
        {
            Movement.Step(ref s, reverse, hull, Dt);
            Assert.True(Speed(s) <= hull.MaxSpeed + 1e-9, $"step {i}: speed {Speed(s)}");
        }
        Assert.Equal(Math.PI, Math.Abs(s.Rot), 9);
        Assert.Equal(hull.MaxSpeed, s.Vy, 6);
        Assert.Equal(0, s.Vx, 6);
    }

    [Theory]
    [InlineData(TestHullId.Light)]
    [InlineData(TestHullId.Medium)]
    [InlineData(TestHullId.Heavy)]
    public void Turning_NeverPushesSpeedAboveMaxSpeed(TestHullId id)
    {
        var hull = TestHulls.Get(id) with { LateralToForward = 1 };
        var s = FlyingUp(hull.MaxSpeed);
        for (var i = 0; i < 200; i++)
        {
            // Непрерывный вираж: желаемое направление всё время на 90° правее носа.
            Movement.Step(ref s, new MoveInput(Math.Cos(s.Rot), Math.Sin(s.Rot), 1), hull, Dt);
            Assert.True(Speed(s) <= hull.MaxSpeed + 1e-9, $"step {i}: speed {Speed(s)}");
        }
    }

    [Fact]
    public void BackwardSpeed_IsBrakedToZeroWithoutOvershooting()
    {
        // Нос вверх, а корабль ещё летит вниз (после разворота): гасится тормозом, но не дальше нуля за шаг.
        var s = new ShipState { Vy = 3 };
        Movement.Step(ref s, new MoveInput(0, -1, 1), TestHulls.Medium, Dt);
        Assert.Equal(0, Forward(s));

        s = new ShipState { Vy = 100 };
        Movement.Step(ref s, new MoveInput(0, -1, 1), TestHulls.Medium, Dt);
        Assert.Equal(-100 + 145 * Dt, Forward(s), 9);
    }

    [Fact]
    public void LateralSlip_FadesToFivePercentWithinDampTime()
    {
        var hull = TestHulls.Light;
        var s = new ShipState { Vx = 200 }; // нос вверх, скольжение вбок
        var steps = (int)Math.Round(hull.LateralDampTime / Dt);
        for (var i = 0; i < steps; i++) Movement.Step(ref s, new MoveInput(0, -1, 1), hull, Dt);
        Assert.True(Math.Abs(s.Vx) <= 200 * 0.05 + 1e-9, $"lateral {s.Vx}");
    }

    [Fact]
    public void Stopping_SettlesToExactlyZero()
    {
        var s = new ShipState { Vx = 150, Vy = -250 };
        for (var i = 0; i < 100; i++) Movement.Step(ref s, new MoveInput(0, -1, 0), TestHulls.Light, Dt);
        Assert.Equal(0, s.Vx);
        Assert.Equal(0, s.Vy);
    }

    [Fact]
    public void ZeroDirection_DoesNotTurn()
    {
        var s = new ShipState { Rot = 1.0 };
        Movement.Step(ref s, new MoveInput(0, 0, 0.5), TestHulls.Light, Dt);
        Assert.Equal(1.0, s.Rot);
    }

    [Fact]
    public void WorldBorder_StopsTheShipAtTheEdge()
    {
        var s = new ShipState { X = Movement.WorldHalfSize - 5, Rot = Math.PI / 2, Vx = 330 };
        for (var i = 0; i < 10; i++) Movement.Step(ref s, new MoveInput(1, 0, 1), TestHulls.Light, Dt);
        Assert.Equal(Movement.WorldHalfSize, s.X);
        Assert.True(s.Vx <= 1e-9);
    }

    [Fact]
    public void LateralToForward_KeepsMoreSpeedInASustainedTurn()
    {
        var plain = FlyingUp(330);
        var assisted = FlyingUp(330);
        var carving = TestHulls.Light with { LateralToForward = 0.5 };
        // Непрерывный вираж на полном TurnRate: желаемое направление всё время на 90° правее носа.
        for (var i = 0; i < 60; i++)
        {
            Movement.Step(ref plain, new MoveInput(Math.Cos(plain.Rot), Math.Sin(plain.Rot), 1), TestHulls.Light, Dt);
            Movement.Step(ref assisted, new MoveInput(Math.Cos(assisted.Rot), Math.Sin(assisted.Rot), 1), carving, Dt);
        }
        Assert.True(Speed(assisted) > Speed(plain) + 10, $"assisted {Speed(assisted)}, plain {Speed(plain)}");
    }

    [Theory]
    [InlineData(double.NaN, 0, 1)]
    [InlineData(0, double.PositiveInfinity, 1)]
    [InlineData(0, 1, double.NaN)]
    public void TryCreate_RejectsNonFiniteNumbers(double dx, double dy, double th)
    {
        Assert.False(MoveInput.TryCreate(dx, dy, th, out _));
    }

    [Fact]
    public void TryCreate_ClampsThrottleAndNormalizesDirection()
    {
        Assert.True(MoveInput.TryCreate(3, 4, 5, out var input));
        Assert.Equal(new MoveInput(0.6, 0.8, 1), input);

        Assert.True(MoveInput.TryCreate(0, 0, -2, out input));
        Assert.Equal(new MoveInput(0, 0, 0), input);
    }

    [Fact]
    public void TryCreate_LeavesUnitVectorsBitForBit()
    {
        var dx = 1 / Math.Sqrt(2);
        Assert.True(MoveInput.TryCreate(dx, -dx, 0.5, out var input));
        Assert.Equal(dx, input.Dx);
        Assert.Equal(-dx, input.Dy);
    }

    [Fact]
    public void SharedHullsJson_IsValid()
    {
        var json = File.ReadAllText(Path.Combine(TestHulls.RepoRoot(), "shared", "hulls.json"));
        Assert.True(HullCatalog.TryParse(json, out var hulls, out var error), error);
        Assert.Contains(SimConfig.DefaultHull, hulls.Keys);
    }

    [Fact]
    public void HullCatalog_RejectsBrokenValues()
    {
        Assert.False(HullCatalog.TryParse("""{ "light": { "name": "x", "maxSpeed": 0 } }""", out _, out var error));
        Assert.Contains("light", error);
        Assert.False(HullCatalog.TryParse("not json", out _, out _));
    }
}
