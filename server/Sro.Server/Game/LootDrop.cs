namespace Sro.Server.Game;

/// <summary>
/// Предмет, лежащий в космосе (GDD §21). Не <see cref="ShipEntity"/>: у него нет корпуса, щита, пушки и респауна,
/// а попав в список кораблей, он стал бы мишенью для <see cref="Battle"/> и попал бы в список игроков.
/// Состояние — поля: дрейф меняется каждый тик.
/// </summary>
/// <param name="expiresAtTick">Тик, когда предмет исчезнет; long.MaxValue — не протухает (контейнер).</param>
/// <param name="fromContainer">Предмет из контейнера, а не обломки: рисуется ящиком и ждёт на месте.</param>
/// <param name="owner">
/// Чей это груз (M20a): id пилота, которому его положил сюжет; 0 — общий, как всё остальное в космосе.
/// Видят такую стопку все — снапшот в системе один, — но взять её может только хозяин. Иначе двое,
/// идущие по одной кампании, растаскивали бы ящики друг у друга, и цепочка вставала бы намертво.
/// </param>
public sealed class LootDrop(
    int id,
    string item,
    int count,
    double x,
    double y,
    double vx,
    double vy,
    long expiresAtTick,
    bool fromContainer = false,
    int owner = 0)
{
    public bool FromContainer { get; } = fromContainer;

    /// <summary>Чей груз; 0 — ничей, берёт кто успел.</summary>
    public int Owner { get; } = owner;

    /// <summary>Id из общего счётчика комнаты: предметы и корабли не путаются между собой.</summary>
    public int Id { get; } = id;
    public string Item { get; } = item;
    public int Count { get; } = count;
    public long ExpiresAtTick { get; } = expiresAtTick;

    public double X = x;
    public double Y = y;
    public double Vx = vx;
    public double Vy = vy;

    /// <summary>
    /// Дрейф по инерции убитого: обломки разлетаются и за DriftDampTime почти встают.
    /// Затухание — то же, что у бокового скольжения в <see cref="Sro.Sim.Movement"/>: за dampTime примерно до 5%.
    /// </summary>
    public void Step(double dampTime, double dt)
    {
        var damp = Math.Exp(-3 * dt / dampTime);
        Vx *= damp;
        Vy *= damp;
        X += Vx * dt;
        Y += Vy * dt;
    }
}
