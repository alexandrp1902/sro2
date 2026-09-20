using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Лут системы (GDD §21–23): что лежит в космосе, как оно дрейфует и когда исчезает.
/// Источник предмета системе безразличен — обломки, контейнер или метеорит зовут один и тот же <see cref="Spawn"/>.
/// Как и <see cref="Room"/>, живёт только в потоке тика.
/// </summary>
/// <param name="nextId">Общий счётчик id комнаты: предметы и корабли не путаются между собой.</param>
/// <param name="rng">Случайность дропа — отдельно от разброса спауна и от ИИ, чтобы тесты были воспроизводимы.</param>
internal sealed class LootSystem(Func<int> nextId, Random rng, ILogger log)
{
    /// <summary>Точка контейнера: пока DropId не 0 — он на месте, иначе ждёт до ReadyAtTick следующей попытки.</summary>
    private sealed class ContainerSlot(LootContainer spec)
    {
        public LootContainer Spec { get; } = spec;
        public int DropId;
        public long ReadyAtTick;
    }

    private readonly List<LootDrop> _drops = [];
    private readonly List<LootDto> _dtos = [];
    private readonly List<PickDto> _picks = [];
    private readonly List<ContainerSlot> _slots = [];
    private readonly List<(string Item, int Count)> _rolled = [];

    public IReadOnlyList<LootDrop> Drops => _drops;

    /// <summary>Подобранное в этом тике — уходит в общий снапшот: чужой луч видят все.</summary>
    public IReadOnlyList<PickDto> Picks => _picks;

    public void ClearPicks() => _picks.Clear();

    public bool Has(int id)
    {
        foreach (var drop in _drops)
        {
            if (drop.Id == id) return true;
        }
        return false;
    }

    /// <summary>
    /// Кладёт предмет в космос. Возвращает null, если предмета нет в каталоге или система уже забита:
    /// переполнение — штатная защита от засорения, а не ошибка.
    /// </summary>
    public LootDrop? Spawn(
        LootRules loot,
        long tick,
        double x,
        double y,
        double vx,
        double vy,
        string item,
        int count,
        bool fromContainer = false)
    {
        if (count < 1 || !loot.Knows(item)) return null;
        if (_drops.Count >= loot.MaxItems)
        {
            log.LogDebug("Loot field is full ({Max}), dropping {Item} x{Count}", loot.MaxItems, item, count);
            return null;
        }
        // Содержимое контейнера ждёт игрока сколько угодно: оно и есть постоянная точка на карте.
        var expires = fromContainer ? long.MaxValue : tick + loot.LifetimeTicks;
        var drop = new LootDrop(nextId(), item, count, x, y, vx, vy, expires, fromContainer);
        _drops.Add(drop);
        return drop;
    }

    /// <summary>Расставляет контейнеры заново: при старте и когда их список изменился в loot.json.</summary>
    public void SetContainers(IReadOnlyList<LootContainer> containers)
    {
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            if (_drops[i].FromContainer) _drops.RemoveAt(i);
        }
        _slots.Clear();
        foreach (var spec in containers) _slots.Add(new ContainerSlot(spec));
        // Первые попытки — вразнобой по всему сроку: иначе при старте комнаты все точки наполнились бы разом.
        foreach (var slot in _slots) slot.ReadyAtTick = (long)(rng.NextDouble() * Math.Max(1, slot.Spec.RespawnTicks));
    }

    /// <summary>
    /// Пустые точки, у которых вышел срок, бросают монету: повезло — контейнер появился, нет — ждём ещё срок.
    /// Поэтому места наполняются вразнобой, а богатые точки пустуют чаще бедных.
    /// </summary>
    private void RefillContainers(long tick, LootRules loot)
    {
        var filled = 0;
        foreach (var slot in _slots)
        {
            if (slot.DropId != 0) filled++;
        }

        foreach (var slot in _slots)
        {
            if (slot.DropId != 0 || tick < slot.ReadyAtTick) continue;
            if (loot.MaxContainers > 0 && filled >= loot.MaxContainers) continue;

            var spec = slot.Spec;
            // Следующая попытка назначается независимо от исхода: неудача — это просто «в этот раз не легло».
            slot.ReadyAtTick = spec.RespawnTicks > 0 ? tick + NextWait(spec.RespawnTicks) : long.MaxValue;
            if (rng.NextDouble() >= spec.Chance) continue;
            var item = spec.Item;
            var count = spec.Count;
            if (item is null && spec.Table is not null && loot.TableMap.TryGetValue(spec.Table, out var table))
            {
                // Контейнер — один предмет, а не облако: несколько стопок в одной точке не разобрать тапом.
                _rolled.Clear();
                table.Roll(1, rng.NextDouble, _rolled);
                if (_rolled.Count == 0) continue;
                (item, count) = _rolled[0];
            }
            if (item is null) continue;

            slot.DropId = Spawn(loot, tick, spec.X, spec.Y, 0, 0, item, count, fromContainer: true)?.Id ?? 0;
            if (slot.DropId != 0) filled++;
        }
    }

    /// <summary>Срок до следующей попытки: половина — полтора от среднего, чтобы точки не тикали в такт.</summary>
    private long NextWait(int respawnTicks) => Math.Max(1, (long)Math.Round(respawnTicks * (0.5 + rng.NextDouble())));

    /// <summary>Точка освободилась: следующая попытка — через свой срок вразнобой; 0 секунд — уже никогда.</summary>
    private void Release(int dropId, long tick)
    {
        foreach (var slot in _slots)
        {
            if (slot.DropId != dropId) continue;
            slot.DropId = 0;
            slot.ReadyAtTick = slot.Spec.RespawnTicks > 0 ? tick + NextWait(slot.Spec.RespawnTicks) : long.MaxValue;
            return;
        }
    }

    /// <summary>Высыпанный трюм делится на кучки не больше этого объёма: иначе стопку не взял бы ни один трюм.</summary>
    public const double PileVolume = 5;

    /// <summary>
    /// Дроп с уничтоженных в этом тике. Таблица пирата ищется по его типу: имя таблицы в loot.json — это ключ типа
    /// в npcs.json, поэтому новый тип пиратов начинает ронять добычу без правок кода. У дрона таблица своя, в описании.
    /// Трюм погибшего — игрока или NPC, подобравшего груз, — высыпается весь (GDD §24).
    /// </summary>
    public void DropFrom(IReadOnlyList<KillDto> kills, IReadOnlyDictionary<int, ShipEntity> ships, LootRules loot, long tick)
    {
        foreach (var kill in kills)
        {
            var hold = ships.GetValueOrDefault(kill.Id) switch
            {
                Player player => player.Cargo,
                IScavenger scavenger => scavenger.Hold,
                _ => null,
            };
            if (hold is { IsEmpty: false })
            {
                var dead = ships[kill.Id];
                Spill(hold, loot, dead.Ship.X, dead.Ship.Y, dead.DeathVx, dead.DeathVy, tick);
            }
            var (tableId, level) = ships.GetValueOrDefault(kill.Id) switch
            {
                Pirate pirate => (pirate.Spawn.Type, pirate.Level),
                Drone { Spec.Table: { } own } => (own, 1),
                Trader trader => (trader.LootTable, 1),
                _ => (null, 0),
            };
            if (tableId is null || !loot.TableMap.TryGetValue(tableId, out var table)) continue;
            var ship = ships[kill.Id];
            DropAt(loot, table, level, ship.Ship.X, ship.Ship.Y, ship.DeathVx, ship.DeathVy, tick);
        }
    }

    /// <summary>
    /// Весь груз трюма — в космос вокруг точки гибели, кучками не больше <see cref="PileVolume"/>. Груз доставки
    /// (Reserved) — не предмет: он остаётся за пилотом.
    /// </summary>
    public void Spill(Cargo hold, LootRules loot, double x, double y, double vx, double vy, long tick)
    {
        foreach (var (item, total) in hold.Items)
        {
            var pile = Math.Max(1, (int)Math.Floor(PileVolume / Math.Max(loot.Volume(item), 1e-9)));
            for (var left = total; left > 0; left -= pile) Scatter(loot, tick, x, y, vx, vy, item, Math.Min(pile, left));
        }
        hold.Clear();
    }

    /// <summary>
    /// Одна стопка за борт (M15.1): столько же куч, как при гибели, но остальной трюм остаётся на месте.
    /// </summary>
    public void SpillOne(LootRules loot, string item, int count, double x, double y, double vx, double vy, long tick)
    {
        var pile = Math.Max(1, (int)Math.Floor(PileVolume / Math.Max(loot.Volume(item), 1e-9)));
        for (var left = count; left > 0; left -= pile) Scatter(loot, tick, x, y, vx, vy, item, Math.Min(pile, left));
    }

    /// <summary>Предмет в случайной точке круга DropRadius: стопка в одной точке не разбирается тапом.</summary>
    private void Scatter(LootRules loot, long tick, double x, double y, double vx, double vy, string item, int count)
    {
        var radius = loot.DropRadius * Math.Sqrt(rng.NextDouble());
        var angle = rng.NextDouble() * 2 * Math.PI;
        Spawn(loot, tick, x + radius * Math.Cos(angle), y + radius * Math.Sin(angle), vx * loot.DriftFactor, vy * loot.DriftFactor, item, count);
    }

    /// <summary>Дроп по таблице вокруг точки гибели: предметы наследуют долю скорости погибшего (vx, vy).</summary>
    public void DropAt(LootRules loot, LootTable table, int level, double x, double y, double vx, double vy, long tick)
    {
        _rolled.Clear();
        table.Roll(level, rng.NextDouble, _rolled);
        foreach (var (item, count) in _rolled) Scatter(loot, tick, x, y, vx, vy, item, count);
    }

    /// <summary>
    /// Ближайший груз, за которым корабль свернёт с дороги: не дальше range от него и не дальше leash
    /// от якоря, влезает в трюм. У пирата и рейнджера якорь — логово, у торговца — его цель: так он
    /// подбирает то, что по пути, и не разворачивается назад.
    /// Контейнеры — постоянные точки для пилотов: их NPC не трогают.
    /// </summary>
    /// <param name="allow">Дополнительная проверка груза; null — берём любой, что прошёл по расстояниям.</param>
    public LootDrop? ScavengeTarget<T>(
        T ship,
        double anchorX,
        double anchorY,
        double range,
        double leash,
        double capacity,
        LootRules loot,
        Func<LootDrop, bool>? allow = null)
        where T : ShipEntity, IScavenger
    {
        LootDrop? best = null;
        var bestDistance = range;
        foreach (var drop in _drops)
        {
            // Снаряжение NPC ни к чему: трюм считает объём, а у пушки его нет.
            if (drop.FromContainer || loot.IsGear(drop.Item) || !ship.Hold.Fits(drop.Item, drop.Count, capacity, loot)) continue;
            if (Math.Sqrt(Sq(drop.X - anchorX) + Sq(drop.Y - anchorY)) > leash) continue;
            var distance = Math.Sqrt(Sq(drop.X - ship.Ship.X) + Sq(drop.Y - ship.Ship.Y));
            if (distance > bestDistance) continue;
            if (allow is not null && !allow(drop)) continue;
            best = drop;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>Корабль подлетел к грузу — луч забирает его в трюм; все видят луч, как у пилота.</summary>
    /// <returns>true — забрал.</returns>
    public bool TryScavenge<T>(T ship, int lootId, LootRules loot, double capacity)
        where T : ShipEntity, IScavenger
    {
        var index = _drops.FindIndex(d => d.Id == lootId);
        if (index < 0) return false;
        var drop = _drops[index];
        if (Math.Sqrt(Sq(ship.Ship.X - drop.X) + Sq(ship.Ship.Y - drop.Y)) > loot.PickupRange) return false;
        if (loot.IsGear(drop.Item) || !ship.Hold.Fits(drop.Item, drop.Count, capacity, loot)) return false;
        ship.Hold.Add(drop.Item, drop.Count);
        _drops.RemoveAt(index);
        _picks.Add(new PickDto(ship.Id, drop.Id, drop.Item, drop.Count));
        return true;
    }

    /// <summary>Чем кончилась попытка взять предмет.</summary>
    public enum GrabResult
    {
        /// <summary>Предмета уже нет: забрали или протух.</summary>
        Gone,
        Taken,
        TooFar,
        NoRoom,
    }

    /// <summary>
    /// Взять выбранный предмет тракторным лучом (GDD §21). Подбор ручной: луч берёт ровно то, что игрок
    /// пометил, и только когда тот подлетел ближе PickupRange.
    /// </summary>
    /// <param name="capacity">Трюм корпуса с модулями: грузовой расширитель (M11) его увеличивает.</param>
    public GrabResult TryGrab(
        Player player,
        int lootId,
        LootRules loot,
        double capacity,
        long tick)
    {
        var index = _drops.FindIndex(d => d.Id == lootId);
        if (index < 0) return GrabResult.Gone;

        var drop = _drops[index];
        var distance = Math.Sqrt(Sq(player.Ship.X - drop.X) + Sq(player.Ship.Y - drop.Y));
        if (distance > loot.PickupRange) return GrabResult.TooFar;
        // Снаряжение (M11) не занимает трюм: пушка или модуль сразу ложатся на склад пилота.
        var gear = loot.IsGear(drop.Item);
        if (!gear && !player.Cargo.Fits(drop.Item, drop.Count, capacity, loot)) return GrabResult.NoRoom;

        if (gear) player.Store(drop.Item, drop.Count);
        else player.Cargo.Add(drop.Item, drop.Count);
        player.CargoFullUntilTick = 0; // место освободилось — о следующем отказе скажем сразу
        if (drop.FromContainer) Release(drop.Id, tick);
        _drops.RemoveAt(index);
        _picks.Add(new PickDto(player.Id, drop.Id, drop.Item, drop.Count));
        return GrabResult.Taken;
    }

    private static double Sq(double v) => v * v;

    /// <summary>Дрейф и уборка протухшего.</summary>
    /// <returns>true — что-то исчезло: выбор предмета у игроков мог осиротеть.</returns>
    public bool Step(long tick, LootRules loot)
    {
        var removed = false;
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            var drop = _drops[i];
            if (tick >= drop.ExpiresAtTick)
            {
                _drops.RemoveAt(i);
                removed = true;
                continue;
            }
            drop.Step(loot.DriftDampTime, SimConfig.Dt);
        }
        RefillContainers(tick, loot);
        return removed;
    }

    /// <summary>Баланс изменился: предмет мог исчезнуть из каталога — тогда его нечем подписать на экране.</summary>
    /// <returns>true — что-то исчезло.</returns>
    public bool DropUnknown(LootRules loot)
    {
        var removed = false;
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            var drop = _drops[i];
            if (loot.Knows(drop.Item)) continue;
            if (drop.FromContainer) Release(drop.Id, 0);
            _drops.RemoveAt(i);
            removed = true;
        }
        return removed;
    }

    /// <returns>Предметы для снапшота или null, если в космосе пусто — тогда поле не пишется вовсе.</returns>
    public IReadOnlyList<LootDto>? ToDtos()
    {
        if (_drops.Count == 0) return null;
        _dtos.Clear();
        foreach (var drop in _drops)
            _dtos.Add(new LootDto(drop.Id, drop.X, drop.Y, drop.Item, drop.Count, drop.ExpiresAtTick, drop.FromContainer));
        return _dtos;
    }
}
