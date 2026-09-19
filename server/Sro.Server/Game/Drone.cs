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

    /// <summary>
    /// Точка дрона в мире: x, y из файла — в осях станции, а станция ходит по орбите. Комната двигает её каждый тик
    /// и сносит дрона вместе с ней (<see cref="Carry"/>).
    /// </summary>
    public (double X, double Y) Anchor { get; private set; } = (spec.X, spec.Y);

    /// <summary>Кружащий дрон появляется сразу на своей орбите.</summary>
    public (double X, double Y) SpawnPoint => (Anchor.X + Spec.OrbitRadius, Anchor.Y);

    /// <summary>Новая точка дрона: корабль сдвигается вместе с ней, как пришвартованный к станции.</summary>
    public void Carry((double X, double Y) anchor)
    {
        if (!IsDead)
        {
            Ship.X += anchor.X - Anchor.X;
            Ship.Y += anchor.Y - Anchor.Y;
        }
        Anchor = anchor;
    }

    public override double MaxHp(HullParams hull) => Spec.Hp ?? hull.Hp;

    public override double MaxShield(HullParams hull) => Spec.Shield ?? hull.Shield;

    /// <summary>Вход на этот тик: стоять носом вверх или лететь по касательной к кругу с поправкой к радиусу.</summary>
    public MoveInput NextInput()
    {
        if (Spec.OrbitRadius <= 0) return LastInput = new MoveInput(0, -1, 0);

        var rx = Ship.X - Anchor.X;
        var ry = Ship.Y - Anchor.Y;
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
