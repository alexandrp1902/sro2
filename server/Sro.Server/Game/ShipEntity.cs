using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Корабль в системе — игрок или NPC: движение и боевое состояние. Меняется только в потоке тика.
/// Состояние — поля, а не свойства: Movement.Step и Combat.ApplyDamage меняют их по ссылке.
/// </summary>
public abstract class ShipEntity(int id, string name, string hullId, string weaponId)
{
    /// <summary>Id корабля в снапшотах. Не меняется при переподключении, в отличие от id соединения.</summary>
    public int Id { get; } = id;
    public string Name { get; set; } = name;
    public string HullId { get; set; } = hullId;
    public string WeaponId { get; set; } = weaponId;

    public ShipState Ship;
    public double Hp;
    public double Shield;
    /// <summary>Тик последнего попадания: щит восстанавливается после паузы без урона.</summary>
    public long LastDamageTick = long.MinValue / 2;
    public long NextFireTick;

    /// <summary>Цель выбирает клиент; сервер обнуляет её, только если корабль-цель исчез из системы.</summary>
    public int TargetId;
    /// <summary>Атака удерживается: пушка стреляет сама, как только готова (GDD §47).</summary>
    public bool FireHeld;

    /// <summary>0 — корабль цел; иначе — тик, когда он появится снова.</summary>
    public long DeadUntilTick;
    public long ProtectedUntilTick;
    /// <summary>Кто нанёс смертельный удар — для ленты «A уничтожает B».</summary>
    public int KilledBy;
    /// <summary>Кто последним стрелял по кораблю, в том числе мимо; 0 — никто. По нему пират понимает, что на него напали.</summary>
    public int LastAttackerId;
    public DamageStats Stats;

    public bool IsDead => DeadUntilTick > 0;

    public bool IsProtected(long tick) => tick < ProtectedUntilTick;

    public virtual double MaxHp(HullParams hull) => hull.Hp;

    public virtual double MaxShield(HullParams hull) => hull.Shield;

    /// <summary>Пушка, из которой корабль стреляет; null — такой пушки больше нет.</summary>
    public virtual WeaponParams? Weapon(Balance balance) => balance.Weapons.GetValueOrDefault(WeaponId);

    /// <summary>Через столько тиков уничтоженный корабль появляется снова.</summary>
    public virtual int RespawnTicks(Balance balance) => balance.Rules.RespawnTicks;

    public HullParams Hull(IReadOnlyDictionary<string, HullParams> hulls)
    {
        if (hulls.TryGetValue(HullId, out var hull)) return hull;
        HullId = SimConfig.DefaultHull; // корпус убрали из hulls.json на лету
        return hulls[SimConfig.DefaultHull];
    }

    /// <summary>Смена корпуса или его параметров: доли корпуса и щита сохраняются, иначе смена лечила бы.</summary>
    public void ChangeHull(HullParams from, string hullId, HullParams to)
    {
        var hpShare = Hp / MaxHp(from);
        var fromShield = MaxShield(from);
        var shieldShare = fromShield > 0 ? Shield / fromShield : 1;
        HullId = hullId;
        Hp = hpShare * MaxHp(to);
        Shield = shieldShare * MaxShield(to);
    }

    /// <summary>Появление: полные корпус и щит, огня нет, статистика урона заново.</summary>
    /// <param name="protectedUntil">Защита после появления (GDD §25); 0 — без защиты.</param>
    public void Revive(HullParams hull, long protectedUntil)
    {
        Hp = MaxHp(hull);
        Shield = MaxShield(hull);
        DeadUntilTick = 0;
        KilledBy = 0;
        LastAttackerId = 0;
        LastDamageTick = long.MinValue / 2;
        ProtectedUntilTick = protectedUntil;
        FireHeld = false;
        Stats = default;
    }
}

/// <summary>Сколько по кораблю стреляли с последнего появления — для лога TTK на плейтесте.</summary>
public struct DamageStats
{
    public long? FirstHitTick;
    public int Shots;
    public int Hits;
    public double ChanceSum;

    public void Record(long tick, bool hit, double chance)
    {
        Shots++;
        ChanceSum += chance;
        if (!hit) return;
        Hits++;
        FirstHitTick ??= tick;
    }
}
