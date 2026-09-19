using Sro.Sim;

namespace Sro.Server.Game;

public enum PirateState
{
    /// <summary>Кружит по точкам вокруг логова и высматривает цель.</summary>
    Patrol,
    /// <summary>Держит цель на дистанции и стреляет.</summary>
    Attack,
    /// <summary>Летит в логово, ни на что не реагируя; там чинится. Налётчик так летит от врат к точке патруля.</summary>
    Return,
    /// <summary>Налётчик уходит: летит к вратам (или на базу), ни на что не реагируя, и исчезает из системы.</summary>
    Leave,
}

/// <summary>
/// Пират (GDD §31–32): NPC со своим логовом, уровнем и ИИ. Летает по той же модели, что игроки, и стреляет через тот же
/// Battle — ИИ (<see cref="PirateBrain"/>) лишь выставляет вход, огонь и цель, как это делает клиент.
/// </summary>
public sealed class Pirate : ShipEntity
{
    /// <summary>Члены логова появляются на круге такого радиуса вокруг точки логова.</summary>
    private const double SlotRadius = 60;
    /// <summary>Каждый следующий член логова держит дистанцию на столько больше — пираты не слипаются в одну точку.</summary>
    private const double SlotHoldStep = 60;
    /// <summary>Золотой угол: места на круге не совпадают при любом числе членов.</summary>
    private const double SlotAngle = 2.39996;

    private NpcRules _rules;
    private WeaponParams? _weapon;
    private Balance? _weaponFor;

    /// <param name="slot">Номер в логове: от него место появления, дистанция боя и сторона захода.</param>
    public Pirate(int id, NpcSpawn spawn, int slot, NpcType type, NpcRules rules)
        : base(id, NpcRules.Name(type, spawn.Level), type.Hull, type.Weapon)
    {
        Spawn = spawn;
        Slot = slot;
        Type = type;
        _rules = rules;
        Side = slot % 2 == 0 ? 1 : -1;
    }

    public NpcSpawn Spawn { get; }
    public int Slot { get; }
    public NpcType Type { get; private set; }
    public int Level => Spawn.Level;
    public double HomeX => Spawn.X;
    public double HomeY => Spawn.Y;

    /// <summary>Номер налёта (<see cref="RaidRules"/>); 0 — пират из логова, живёт в системе постоянно.</summary>
    public int RaidId;
    /// <summary>Откуда налётчик прилетел и куда уйдёт: врата или пиратская база.</summary>
    public double ExitX;
    public double ExitY;
    /// <summary>Выход — врата: там пират готовит прыжок, как игрок; иначе — база, в неё он просто садится.</summary>
    public bool ExitIsGate;
    /// <summary>Сколько налётчик патрулирует, долетев до точки.</summary>
    public long PatrolTicks;
    /// <summary>Когда налётчику уходить; 0 — ещё не долетел до точки патруля.</summary>
    public long PatrolUntilTick;
    /// <summary>Готовит прыжок у врат и уйдёт в этот тик; 0 — нет.</summary>
    public long LeaveAtTick;
    /// <summary>Ушёл из системы: комната уберёт его после шага ИИ.</summary>
    public bool Gone;

    public bool IsRaider => RaidId != 0;

    public PirateState State = PirateState.Patrol;
    public MoveInput LastInput = new(0, -1, 0);
    /// <summary>Сторона захода на цель издалека: +1 — справа, −1 — слева.</summary>
    public int Side;
    public bool HasWaypoint;
    public double WaypointX;
    public double WaypointY;
    public long WaypointUntilTick;

    public double HoldRange => Type.HoldRange + SlotHoldStep * Slot;

    public (double X, double Y) SpawnPoint
    {
        get
        {
            var angle = Slot * SlotAngle;
            return (HomeX + SlotRadius * Math.Cos(angle), HomeY + SlotRadius * Math.Sin(angle));
        }
    }

    public override double MaxHp(HullParams hull) => _rules.MaxHp(Type, Level, hull);

    public override double MaxShield(HullParams hull) => _rules.MaxShield(Type, Level, hull);

    /// <summary>Пушка типа с уроном и точностью уровня; пересчитывается только при смене баланса.</summary>
    public override WeaponParams? Weapon(Balance balance)
    {
        if (!ReferenceEquals(_weaponFor, balance))
        {
            _weaponFor = balance;
            _weapon = balance.Weapons.TryGetValue(WeaponId, out var weapon) ? _rules.ScaledWeapon(Type, Level, weapon) : null;
        }
        return _weapon;
    }

    public override int RespawnTicks(Balance balance) => balance.Npc.RespawnTicks;

    /// <summary>Появление в логове: ИИ начинает с патруля.</summary>
    public void ResetAi()
    {
        State = PirateState.Patrol;
        TargetId = 0;
        FireHeld = false;
        HasWaypoint = false;
        LastInput = new MoveInput(0, -1, 0);
    }

    /// <summary>
    /// Баланс изменился, логова те же: новый тип, корпус и множители при тех же долях корпуса и щита.
    /// Доли считаются по старым правилам до замены — иначе смена параметров лечила бы или калечила.
    /// </summary>
    public void Rebind(NpcType type, NpcRules rules, IReadOnlyDictionary<string, HullParams> oldHulls, IReadOnlyDictionary<string, HullParams> newHulls)
    {
        var oldHull = Hull(oldHulls);
        var hpShare = Hp / MaxHp(oldHull);
        var oldShield = MaxShield(oldHull);
        var shieldShare = oldShield > 0 ? Shield / oldShield : 1;

        Type = type;
        _rules = rules;
        _weaponFor = null;
        Name = NpcRules.Name(type, Level);
        HullId = newHulls.ContainsKey(type.Hull) ? type.Hull : SimConfig.DefaultHull;
        WeaponId = type.Weapon;

        var newHull = newHulls[HullId];
        Hp = hpShare * MaxHp(newHull);
        Shield = shieldShare * MaxShield(newHull);
    }

    /// <summary>В логове: корпус и щит полностью.</summary>
    public void Repair(HullParams hull)
    {
        Hp = MaxHp(hull);
        Shield = MaxShield(hull);
    }

    /// <summary>Для лога: у членов логова одинаковые имена.</summary>
    public override string ToString() => $"{Name} #{Id}";
}
