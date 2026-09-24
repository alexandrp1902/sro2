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
public sealed class Pirate : ShipEntity, IScavenger
{
    /// <summary>Члены логова появляются на круге такого радиуса вокруг точки логова.</summary>
    private const double SlotRadius = 60;
    /// <summary>Каждый следующий член логова держит дистанцию на столько больше — пираты не слипаются в одну точку.</summary>
    private const double SlotHoldStep = 60;
    /// <summary>Золотой угол: места на круге не совпадают при любом числе членов.</summary>
    private const double SlotAngle = 2.39996;

    private NpcRules _rules;
    private readonly WeaponParams?[] _weapons = new WeaponParams?[Fitting.MaxWeaponSlots];
    private Balance? _weaponsFor;

    /// <param name="slot">Номер в логове: от него место появления, дистанция боя и сторона захода.</param>
    public Pirate(int id, NpcSpawn spawn, int slot, NpcType type, NpcRules rules)
        : base(id, NpcRules.Name(type, spawn.Level), NpcRules.HullOf(type, spawn.Level), type.WeaponList)
    {
        Spawn = spawn;
        Slot = slot;
        Type = type;
        _rules = rules;
        Side = slot % 2 == 0 ? 1 : -1;
        HomeX = spawn.X;
        HomeY = spawn.Y;
    }

    public NpcSpawn Spawn { get; }
    public int Slot { get; }
    public NpcType Type { get; private set; }
    public int Level => Spawn.Level;
    /// <summary>
    /// Что пират считает домом: обычно точка логова или налёта. У звена задания дом переезжает с точки
    /// на точку маршрута — его переставляет комната (M14).
    /// </summary>
    public double HomeX { get; set; }
    public double HomeY { get; set; }

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

    /// <summary>Номер вторжения (GDD §38); 0 — не из вторжения. Пират вторжения — налётчик, который не уходит сам.</summary>
    public int InvasionId;

    public bool IsInvader => InvasionId != 0;

    /// <summary>Номер прогона задания (M14): звено рейнджеров патруля или засада на конвой; 0 — не из задания.</summary>
    public int MissionId;

    /// <summary>
    /// Корабль вызван сюжетом (M20a). За такого не платят награду за голову, он не идёт в счёт заданий
    /// с доски и не двигает отношение ни в какую сторону: кампания — личная история пилота, а не источник
    /// дохода и не способ отмыть репутацию. Имя у него своё, и горячая правка баланса его не переименовывает.
    /// </summary>
    public bool Story;

    /// <summary>
    /// Кого этот корабль вызвали встретить (M20b); 0 — любого, кто попадётся. Охрана корпорации ждёт
    /// одного пилота и не должна ловить на мушку посторонних: кампания личная, а комната общая.
    /// </summary>
    public int OwnerId;

    /// <summary>
    /// Стоит на посту (M20b): не считает зону станции запретной и не уходит по поводку от дома. Без этого
    /// охрана, вызванная у шлюза, разворачивалась бы и улетала, не сделав ни выстрела.
    /// </summary>
    public bool HoldsGround;

    /// <summary>Что этот корабль уронит, погибнув (M20b); null — обычная таблица своего типа.</summary>
    public string? StoryDrop;

    /// <summary>
    /// Не отступает и не считает перевес: пираты вторжения и корабли задания дерутся до конца. Звену это нужно
    /// не для злости, а чтобы его в принципе можно было выбить, — иначе подбитый рейнджер просто уйдёт из системы.
    /// </summary>
    public bool NeverRetreats => IsInvader || MissionId != 0;

    /// <summary>
    /// Чинится, добравшись домой. Звено задания — нет: дом у него на каждой точке маршрута, и оно лечилось бы
    /// по дороге до полного, а «звено уничтожено» никогда бы не наступило (M14). Сюжетный корабль — тоже нет,
    /// и ровно по той же причине.
    /// </summary>
    public bool HealsAtHome => !IsInvader && MissionId == 0 && !Story;

    /// <summary>При такой доле корпуса уходит; те, кто бьётся до конца, не уходят вовсе.</summary>
    public double RetreatHp => NeverRetreats ? 0 : Type.RetreatHp;

    public PirateState State = PirateState.Patrol;
    public MoveInput LastInput = new(0, -1, 0);
    /// <summary>Сторона захода на цель издалека: +1 — справа, −1 — слева.</summary>
    public int Side;
    public bool HasWaypoint;
    public double WaypointX;
    public double WaypointY;
    public long WaypointUntilTick;
    /// <summary>Кто стрелял по нему в пути: по нему пират огрызается на ходу; 0 — никто.</summary>
    public int Avenge;
    /// <summary>Что пират подобрал в космосе: погибнет — высыплет вместе со своей добычей.</summary>
    public Cargo Hold { get; } = new();
    /// <summary>Груз, к которому пират летит на патруле; 0 — ни к какому.</summary>
    public int LootId { get; set; }
    public double LootX { get; set; }
    public double LootY { get; set; }

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

    /// <summary>Пушки типа с уроном и точностью уровня; пересчитываются только при смене баланса.</summary>
    public override WeaponParams? WeaponAt(Balance balance, int slot)
    {
        if (!ReferenceEquals(_weaponsFor, balance))
        {
            _weaponsFor = balance;
            for (var i = 0; i < _weapons.Length; i++)
            {
                _weapons[i] = i < WeaponIds.Count && WeaponIds[i] is { } id && balance.Weapons.TryGetValue(id, out var weapon)
                    ? _rules.ScaledWeapon(Type, Level, weapon)
                    : null;
            }
        }
        return slot < _weapons.Length ? _weapons[slot] : null;
    }

    public override int RespawnTicks(Balance balance) => balance.Npc.RespawnTicks;

    /// <summary>Появление в логове: ИИ начинает с патруля.</summary>
    public void ResetAi()
    {
        State = PirateState.Patrol;
        TargetId = 0;
        FireHeld = false;
        HasWaypoint = false;
        Avenge = 0;
        LootId = 0;
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
        _weaponsFor = null;
        // Имя сюжетного корабля правкой баланса не сбивается: иначе «Звено „Клык“» посреди плейтеста
        // стало бы «Рейнджером Ур.3» — ровно в том случае, ради которого и живёт слежение за файлами.
        if (!Story) Name = NpcRules.Name(type, Level);
        var hullId = NpcRules.HullOf(type, Level);
        HullId = newHulls.ContainsKey(hullId) ? hullId : SimConfig.DefaultHull;
        WeaponIds = type.WeaponList;

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
