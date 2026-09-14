using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Учебный дрон (GDD §54): не стреляет, стоит на месте или кружит вокруг своей точки. Летает по той же модели,
/// что игроки, поэтому уклонение от скорости у кружащего дрона настоящее.
/// </summary>
public sealed class Drone(int id, DroneSpec spec) : ShipEntity(id, spec.Name, spec.Hull, SimConfig.DefaultWeapon)
{
    /// <summary>Насколько сильно дрон тянется обратно к кругу, если его снесло с орбиты.</summary>
    private const double OrbitPull = 1.5;

    public DroneSpec Spec { get; } = spec;
    public MoveInput LastInput { get; private set; } = new(0, -1, 0);

    /// <summary>Кружащий дрон появляется сразу на своей орбите.</summary>
    public (double X, double Y) SpawnPoint => (Spec.X + Spec.OrbitRadius, Spec.Y);

    public override double MaxHp(HullParams hull) => Spec.Hp ?? hull.Hp;

    public override double MaxShield(HullParams hull) => Spec.Shield ?? hull.Shield;

    /// <summary>Вход на этот тик: стоять носом вверх или лететь по касательной к кругу с поправкой к радиусу.</summary>
    public MoveInput NextInput()
    {
        if (Spec.OrbitRadius <= 0) return LastInput = new MoveInput(0, -1, 0);

        var rx = Ship.X - Spec.X;
        var ry = Ship.Y - Spec.Y;
        var distance = Math.Sqrt(rx * rx + ry * ry);
        if (distance < 1e-6) return LastInput = new MoveInput(1, 0, Spec.Throttle);

        var ux = rx / distance;
        var uy = ry / distance;
        // Касательная (−uy, ux) — по часовой стрелке на экране; снесло наружу — довернуть внутрь, и наоборот.
        var pull = Math.Clamp((distance - Spec.OrbitRadius) / Spec.OrbitRadius, -1, 1) * OrbitPull;
        MoveInput.TryCreate(-uy - ux * pull, ux - uy * pull, Spec.Throttle, out var input);
        return LastInput = input;
    }
}
