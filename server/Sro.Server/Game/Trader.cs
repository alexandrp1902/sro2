using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Торговец (GDD §31): везёт груз между станцией и вратами системы. Пираты на него охотятся, пилот может его
/// ограбить — с уничтоженного выпадает груз по таблице лута. Сам ни на кого не нападает, но обидчику отвечает
/// огнём — слабее пирата (множитель урона типа). Долетел — ушёл в док или в прыжок, из системы он исчезает,
/// а через срок появляется новый (<see cref="TraderRules"/>).
/// </summary>
public sealed class Trader(int id, string typeId, NpcType type, NpcRules rules)
    : ShipEntity(id, type.Name, NpcRules.HullOf(type, TraderLevel), type.WeaponList), IScavenger
{
    /// <summary>Торговцы все одного уровня: их не растят, как пиратские логова.</summary>
    public const int TraderLevel = 1;

    /// <summary>Что подобрал по дороге: погибнет — высыплет вместе с грузом рейса.</summary>
    public Cargo Hold { get; } = new();
    /// <summary>Груз, за которым он свернул; 0 — ни за каким.</summary>
    public int LootId { get; set; }
    public double LootX { get; set; }
    public double LootY { get; set; }

    private readonly WeaponParams?[] _weapons = new WeaponParams?[Fitting.MaxWeaponSlots];
    private Balance? _weaponsFor;

    /// <summary>Id типа в npcs.json — по нему и таблица лута, если у типа нет своей.</summary>
    public string TypeId { get; } = typeId;
    public NpcType Type { get; } = type;
    public string LootTable => Type.Table ?? TypeId;

    public MoveInput LastInput = new(0, -1, 0);

    /// <summary>
    /// Номер прогона задания «сопровождение» (M14); 0 — обычный торговец маршрута. Конвой задания не входит
    /// в норму торговцев системы и переживает горячую правку баланса.
    /// </summary>
    public int MissionId;

    /// <summary>Куда летит: к станции (она ходит по орбите — точка берётся каждый тик) или к вратам.</summary>
    public bool ToStation;
    public double DestX;
    public double DestY;

    /// <summary>Система за вратами, к которым он идёт; null — летит к здешней станции.</summary>
    public string? Gate;

    /// <summary>
    /// В какую систему он вёз груз: к станции — в эту же, к вратам — в соседнюю (M13, репутация).
    /// null — назначение неизвестно.
    /// </summary>
    public string? Destination(string here) => ToStation ? here : Gate;

    /// <summary>Готовит прыжок у врат и уйдёт в этот тик; 0 — ещё летит.</summary>
    public long LeaveAtTick;
    /// <summary>Под огнём: дальше до цели — на полной тяге.</summary>
    public bool Fleeing;
    /// <summary>Конвой задания ждёт, пока его засаду не отобьют: до этого тика тяга — ноль, руль прежний.</summary>
    public long HoldUntilTick;
    /// <summary>Кто напал последним — ему торговец отвечает огнём; 0 — никто.</summary>
    public int Attacker;
    /// <summary>Ушёл из системы: комната уберёт его после шага.</summary>
    public bool Gone;

    /// <summary>
    /// Что везёт (M12): id товара из market.json; null — рынка в системе нет. Летит к станции — это поставка,
    /// она придёт на склад, если торговца не собьют. Летит от станции — груз со склада уже забрали.
    /// </summary>
    public string? Good;
    /// <summary>Сколько единиц товара он двигает.</summary>
    public int Units;

    /// <summary>Подал SOS: пока не наступил этот тик без новых выстрелов по нему, он зовёт на помощь; 0 — не зовёт.</summary>
    public long SosUntilTick;
    /// <summary>Когда снова разослать, где он: SOS видно на миникарте и за радаром.</summary>
    public long SosPingTick;
    /// <summary>Кто по нему стрелял, пока идёт SOS: попадания по ним — помощь.</summary>
    public readonly HashSet<int> Aggressors = [];
    /// <summary>Пилоты, которые били нападавших: спасённый торговец заплатит каждому.</summary>
    public readonly HashSet<int> Helpers = [];

    public bool InDistress => SosUntilTick > 0;

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
                    ? rules.ScaledWeapon(Type, TraderLevel, weapon)
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

        // Намечен груз по дороге — сперва к нему: подбирает его комната, когда торговец подлетит.
        // Цель рейса при этом не забыта, просто ждёт: поводок ей задан так, что крюк всегда вперёд, не назад.
        var toLoot = trader.LootId != 0 && !trader.Fleeing;
        var (x, y) = toLoot ? (trader.LootX, trader.LootY)
            : trader.ToStation ? station
            : (trader.DestX, trader.DestY);
        var dx = x - trader.Ship.X;
        var dy = y - trader.Ship.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (!toLoot && distance <= (trader.ToStation ? stationRange : GateRadius))
        {
            if (trader.ToStation) trader.Gone = true;
            else
            {
                trader.LeaveAtTick = tick + jumpTicks;
                Set(trader, dx, dy, 0);
            }
            return;
        }

        var throttle = trader.Fleeing ? 1 : rules.Throttle;
        Set(trader, dx / distance, dy / distance, throttle, heat);
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

    /// <summary>
    /// Единственный выход ИИ торговца наружу. Жар звезды огибается здесь же (M16a) — раньше это делал
    /// вызывающий, и всякий новый вызов молча проходил бы сквозь звезду.
    /// </summary>
    /// <param name="heat">Радиус зоны жара звезды; 0 — звезды нет.</param>
    private static void Set(Trader trader, double dx, double dy, double throttle, double heat = 0)
    {
        var speed = Math.Sqrt(trader.Ship.Vx * trader.Ship.Vx + trader.Ship.Vy * trader.Ship.Vy);
        var (x, y) = Heat.Avoid(trader.Ship.X, trader.Ship.Y, dx, dy, heat, speed);
        MoveInput.TryCreate(x, y, throttle, out var input);
        trader.LastInput = input;
    }
}
