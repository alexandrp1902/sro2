using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Торговец (GDD §31): везёт груз между станцией и вратами системы. Пираты на него охотятся, пилот может его
/// ограбить — с уничтоженного выпадает груз по таблице лута. Сам ни на кого не нападает, но обидчику отвечает
/// огнём — слабее пирата (множитель урона типа). Долетел — ушёл в док или в прыжок, из системы он исчезает,
/// а через срок появляется новый (<see cref="TraderRules"/>).
/// </summary>
public sealed class Trader(int id, string typeId, NpcType type, NpcRules rules) : ShipEntity(id, type.Name, type.Hull, type.WeaponList)
{
    private readonly WeaponParams?[] _weapons = new WeaponParams?[Fitting.MaxWeaponSlots];
    private Balance? _weaponsFor;

    /// <summary>Id типа в npcs.json — по нему и таблица лута, если у типа нет своей.</summary>
    public string TypeId { get; } = typeId;
    public NpcType Type { get; } = type;
    public string LootTable => Type.Table ?? TypeId;

    public MoveInput LastInput = new(0, -1, 0);

    /// <summary>Куда летит: к станции (она ходит по орбите — точка берётся каждый тик) или к вратам.</summary>
    public bool ToStation;
    public double DestX;
    public double DestY;

    /// <summary>Готовит прыжок у врат и уйдёт в этот тик; 0 — ещё летит.</summary>
    public long LeaveAtTick;
    /// <summary>Под огнём: дальше до цели — на полной тяге.</summary>
    public bool Fleeing;
    /// <summary>Кто напал последним — ему торговец отвечает огнём; 0 — никто.</summary>
    public int Attacker;
    /// <summary>Ушёл из системы: комната уберёт его после шага.</summary>
    public bool Gone;

    public override double MaxHp(HullParams hull) => Type.Hp ?? hull.Hp;

    public override double MaxShield(HullParams hull) => Type.Shield ?? hull.Shield;

    /// <summary>Погибший торговец уходит из системы на следующий тик: вместо него появится новый (<see cref="TraderRules"/>).</summary>
    public override int RespawnTicks(Balance balance) => 1;

    /// <summary>Пушки типа с его множителем урона (1-й уровень); пересчитываются только при смене баланса.</summary>
    public override WeaponParams? WeaponAt(Balance balance, int slot)
    {
        if (!ReferenceEquals(_weaponsFor, balance))
        {
            _weaponsFor = balance;
            for (var i = 0; i < _weapons.Length; i++)
            {
                _weapons[i] = i < WeaponIds.Count && WeaponIds[i] is { } id && balance.Weapons.TryGetValue(id, out var weapon)
                    ? rules.ScaledWeapon(Type, 1, weapon)
                    : null;
            }
        }
        return slot < _weapons.Length ? _weapons[slot] : null;
    }

    public override string ToString() => $"{Name} #{Id}";
}

/// <summary>Путь торговца: лететь к цели, огибая жар звезды; у врат — подготовка прыжка, у станции — сразу в док.</summary>
internal static class TraderBrain
{
    /// <summary>Врата достигнуты — ближе этого.</summary>
    private const double GateRadius = 120;
    /// <summary>От жара звезды держится на столько дальше его края; ближе — сворачивает прочь.</summary>
    private const double HeatMargin = 350;

    /// <param name="station">Где сейчас станция.</param>
    /// <param name="stationRange">Ближе этого к станции — пристыковался.</param>
    /// <param name="heat">Радиус жара звезды в центре; 0 — звезды нет.</param>
    /// <param name="ships">Корабли системы: обидчик, которому торговец отвечает огнём.</param>
    /// <param name="fireRange">Дальше этого торговец по обидчику не стреляет и забывает его.</param>
    public static void Think(
        Trader trader,
        TraderRules rules,
        long tick,
        int jumpTicks,
        (double X, double Y) station,
        double stationRange,
        double heat,
        IReadOnlyDictionary<int, ShipEntity> ships,
        double fireRange)
    {
        if (trader.LastAttackerId != 0)
        {
            trader.Fleeing = true;
            trader.Attacker = trader.LastAttackerId;
            trader.LastAttackerId = 0;
        }
        ReturnFire(trader, ships, fireRange);
        if (trader.LeaveAtTick > 0)
        {
            Set(trader, trader.LastInput.Dx, trader.LastInput.Dy, 0);
            if (tick >= trader.LeaveAtTick) trader.Gone = true;
            return;
        }

        var (x, y) = trader.ToStation ? station : (trader.DestX, trader.DestY);
        var dx = x - trader.Ship.X;
        var dy = y - trader.Ship.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= (trader.ToStation ? stationRange : GateRadius))
        {
            if (trader.ToStation) trader.Gone = true;
            else
            {
                trader.LeaveAtTick = tick + jumpTicks;
                Set(trader, dx, dy, 0);
            }
            return;
        }

        var ux = dx / distance;
        var uy = dy / distance;
        // Жар звезды: чем глубже в запретном круге, тем сильнее тянет наружу и вбок — торговец огибает звезду.
        if (heat > 0)
        {
            var r = Math.Sqrt(trader.Ship.X * trader.Ship.X + trader.Ship.Y * trader.Ship.Y);
            var safe = heat + HeatMargin;
            if (r < safe && r > 1e-6)
            {
                var ox = trader.Ship.X / r;
                var oy = trader.Ship.Y / r;
                var push = (safe - r) / HeatMargin * 2;
                // Вбок — в ту сторону, куда цель, чтобы не упираться в звезду лоб в лоб.
                var side = ox * uy - oy * ux >= 0 ? 1 : -1;
                ux += (ox - side * oy) * push;
                uy += (oy + side * ox) * push;
            }
        }
        var throttle = trader.Fleeing ? 1 : rules.Throttle;
        Set(trader, ux, uy, throttle);
    }

    /// <summary>Огонь по обидчику, пока тот цел и рядом; ушёл или погиб — торговец его забывает. Курс это не меняет.</summary>
    private static void ReturnFire(Trader trader, IReadOnlyDictionary<int, ShipEntity> ships, double range)
    {
        if (trader.Attacker != 0 && ships.TryGetValue(trader.Attacker, out var foe) && !foe.IsDead)
        {
            var dx = foe.Ship.X - trader.Ship.X;
            var dy = foe.Ship.Y - trader.Ship.Y;
            if (dx * dx + dy * dy <= range * range)
            {
                trader.TargetId = foe.Id;
                trader.FireHeld = trader.WeaponIds.Count > 0;
                return;
            }
        }
        trader.Attacker = 0;
        trader.TargetId = 0;
        trader.FireHeld = false;
    }

    private static void Set(Trader trader, double dx, double dy, double throttle)
    {
        MoveInput.TryCreate(dx, dy, throttle, out var input);
        trader.LastInput = input;
    }
}
