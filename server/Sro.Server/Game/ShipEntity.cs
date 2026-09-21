using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Корабль в системе — игрок или NPC: движение и боевое состояние. Меняется только в потоке тика.
/// Состояние — поля, а не свойства: Movement.Step и Combat.ApplyDamage меняют их по ссылке.
/// </summary>
/// <param name="weaponIds">Пушки по оружейным слотам (GDD §12); null в слоте — пусто.</param>
public abstract class ShipEntity(int id, string name, string hullId, IReadOnlyList<string?> weaponIds)
{
    /// <summary>Id корабля в снапшотах. Не меняется при переподключении, в отличие от id соединения.</summary>
    public int Id { get; } = id;
    public string Name { get; set; } = name;
    public string HullId { get; set; } = hullId;

    /// <summary>Пушки по оружейным слотам; каждая стреляет по цели сама, со своей перезарядкой.</summary>
    public virtual IReadOnlyList<string?> WeaponIds { get; set; } = weaponIds;

    /// <summary>Первая стоящая пушка — по ней клиент рисует чужой корабль; "" — пушек нет.</summary>
    public string MainWeaponId => WeaponIds.FirstOrDefault(w => w is not null) ?? "";

    public ShipState Ship;
    public double Hp;
    public double Shield;
    /// <summary>Тик последнего попадания: щит восстанавливается после паузы без урона.</summary>
    public long LastDamageTick = long.MinValue / 2;
    /// <summary>Перезарядка каждого оружейного слота: раньше этого тика слот не стреляет.</summary>
    public readonly long[] NextFireTicks = new long[Fitting.MaxWeaponSlots];

    /// <summary>Цель выбирает клиент; сервер обнуляет её, только если корабль-цель исчез из системы.</summary>
    public int TargetId;
    /// <summary>Атака удерживается: пушка стреляет сама, как только готова (GDD §47).</summary>
    public bool FireHeld;

    /// <summary>0 — корабль цел; иначе — тик, когда он появится снова.</summary>
    public long DeadUntilTick;
    public long ProtectedUntilTick;
    /// <summary>Кто нанёс смертельный удар — для ленты «A уничтожает B».</summary>
    public int KilledBy;
    /// <summary>Скорость в момент гибели: Battle гасит её сразу, а обломкам нужна инерция убитого.</summary>
    public double DeathVx;
    public double DeathVy;
    /// <summary>Кто последним стрелял по кораблю, в том числе мимо; 0 — никто. По нему пират понимает, что на него напали.</summary>
    public int LastAttackerId;
    public DamageStats Stats;

    /// <summary>Ионный разрядник (M11): до этого тика корабль замедлен на <see cref="SlowFactor"/>.</summary>
    public long SlowUntilTick;
    public double SlowFactor;

    public bool IsDead => DeadUntilTick > 0;

    public bool IsSlowed(long tick) => tick < SlowUntilTick;

    /// <summary>Попадание замедляющей пушкой: сильнее из двух замедлений, дольше из двух сроков.</summary>
    public void SlowDown(WeaponParams weapon, long tick)
    {
        if (weapon.Slow <= 0 || weapon.SlowTicks <= 0) return;
        SlowFactor = IsSlowed(tick) ? Math.Max(SlowFactor, weapon.Slow) : weapon.Slow;
        SlowUntilTick = Math.Max(SlowUntilTick, tick + weapon.SlowTicks);
    }

    /// <summary>
    /// Корпус для шага движения: замедленный ионкой летит медленнее и хуже разгоняется, тормозит как обычно.
    /// Зеркало slowedHull в client/src/sim/movement.ts — предсказание своего корабля.
    /// </summary>
    public HullParams MoveHull(HullParams hull, long tick) =>
        IsSlowed(tick) ? Movement.Slowed(hull, SlowFactor) : hull;

    /// <summary>Ремонт корпуса в секунду вне боя (ремонтный блок, M11); 0 — нечем.</summary>
    public virtual double RepairRate(Balance balance) => 0;

    /// <summary>Множитель перезарядки пушек (охлаждение, M11); 1 — без него.</summary>
    public virtual double CooldownScale(Balance balance) => 1;

    /// <summary>Шанс отбить попадание этого вида урона, % (M15.6); 0 — нечем. У NPC модулей нет.</summary>
    public virtual double Block(Balance balance, string damageType) => 0;

    /// <summary>Противоракетный комплекс (M15.6); null — его нет.</summary>
    public virtual InterceptParams? Guard(Balance balance) => null;

    /// <summary>
    /// Тик, когда противоракетный комплекс снова готов. Одно поле, а не массив по слотам: комплекс
    /// оружейного слота не занимает, и работает на корабле только один.
    /// </summary>
    public long NextGuardTick;

    public bool IsProtected(long tick) => tick < ProtectedUntilTick;

    public virtual double MaxHp(HullParams hull) => hull.Hp;

    public virtual double MaxShield(HullParams hull) => hull.Shield;

    /// <summary>Пушка в оружейном слоте; null — слот пуст или такой пушки больше нет.</summary>
    public virtual WeaponParams? WeaponAt(Balance balance, int slot) =>
        slot < WeaponIds.Count && WeaponIds[slot] is { } id ? balance.Weapons.GetValueOrDefault(id) : null;

    /// <summary>Все стоящие пушки.</summary>
    public IEnumerable<WeaponParams> Weapons(Balance balance)
    {
        for (var i = 0; i < WeaponIds.Count; i++) if (WeaponAt(balance, i) is { } weapon) yield return weapon;
    }

    /// <summary>
    /// Корпус, по которому корабль летает, держит щит и видит радаром. У пилота — с учётом модулей
    /// (<see cref="Fitting.Effective"/>), у NPC — как в hulls.json.
    /// </summary>
    public virtual HullParams Effective(Balance balance) => Hull(balance.Hulls);

    /// <summary>Уклонение, % (§39–40): по корпусу и текущей скорости.</summary>
    public virtual double Evasion(Balance balance, double speed) => Combat.Evasion(Effective(balance), speed);

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
        HullId = hullId;
        Rescale(from, to);
    }

    /// <summary>Максимумы корпуса и щита сменились (корпус, модули, баланс): доли сохраняются.</summary>
    public void Rescale(HullParams from, HullParams to)
    {
        var hpShare = Hp / MaxHp(from);
        var fromShield = MaxShield(from);
        var shieldShare = fromShield > 0 ? Shield / fromShield : 1;
        Hp = hpShare * MaxHp(to);
        Shield = shieldShare * MaxShield(to);
    }

    /// <summary>Появление: щит полный, корпус — по доле, огня нет, статистика урона заново.</summary>
    /// <param name="protectedUntil">Защита после появления (GDD §25); 0 — без защиты.</param>
    /// <param name="hullShare">
    /// Сколько корпуса дать: 1 — целый, как у NPC. Пилот с M15.7 возвращается разбитым и чинится в доке;
    /// щит при этом полный — он и сам отрастает в полёте, чинить его отдельно было бы мучением.
    /// </param>
    public void Revive(HullParams hull, long protectedUntil, double hullShare = 1)
    {
        Hp = MaxHp(hull) * Math.Clamp(hullShare, 0, 1);
        Shield = MaxShield(hull);
        DeadUntilTick = 0;
        KilledBy = 0;
        LastAttackerId = 0;
        LastDamageTick = long.MinValue / 2;
        SlowUntilTick = 0;
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
