using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>Лут в космосе (GDD §21–23): дроп с уничтоженных, дрейф обломков, протухание, выбор предмета.</summary>
public class LootTests
{
    /// <summary>Бросок, при котором попадание гарантировано.</summary>
    private const double Hit = 0;
    private const double LairX = 0;
    private const double LairY = -2500;

    /// <summary>Без защиты после появления: тестам нужен выстрел сразу.</summary>
    private static readonly CombatRules Rules = new(RespawnSeconds: 2, ProtectionSeconds: 0, SpawnJitter: 0);

    private static readonly NpcType PirateType =
        new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320);

    private static readonly IReadOnlyDictionary<string, LootItem> Items = new Dictionary<string, LootItem>
    {
        ["metal"] = new("Металл", Volume: 1, Price: 10),
        ["tech"] = new("Компонент", "epic", Volume: 2, Price: 200),
    };

    private double _roll = Hit;
    private Room _room;
    private int _nextConnection;

    public LootTests() => _room = NewRoom(Loot());

    private static NpcRules Npcs() => new(
        RespawnSeconds: 5,
        Types: new Dictionary<string, NpcType> { ["pirate"] = PirateType },
        Spawns: [new NpcSpawn("pirate", 1, LairX, LairY)]);

    /// <summary>По умолчанию пират роняет ровно «Металл ×2» — так тесты не зависят от случайности.</summary>
    private static LootRules Loot(
        double lifetimeSeconds = 60,
        int maxItems = 200,
        double dropRadius = 40,
        bool stationUnload = true,
        int maxContainers = 0,
        IReadOnlyList<LootContainer>? containers = null,
        IReadOnlyDictionary<string, LootTable>? tables = null,
        IReadOnlyDictionary<string, LootItem>? items = null) =>
        new(
            LifetimeSeconds: lifetimeSeconds,
            FadeSeconds: 0,
            MaxItems: maxItems,
            DropRadius: dropRadius,
            StationUnload: stationUnload,
            MaxContainers: maxContainers,
            Containers: containers,
            Items: items ?? Items,
            Tables: tables ?? new Dictionary<string, LootTable>
            {
                ["pirate"] = new([new LootRoll("metal", 1, 2, 2)]),
            });

    private Room NewRoom(LootRules loot, CombatRules? rules = null, int seed = 1) => new(
        TestBalance.Create(rules ?? Rules, Npcs(), loot),
        NullLogger.Instance,
        () => _roll,
        ai: new Random(1),
        loot: new Random(seed));

    /// <summary>Объёмный предмет: две штуки не влезают в лёгкий трюм (20) — для проверок «мест нет».</summary>
    private static readonly IReadOnlyDictionary<string, LootItem> BulkyItems = new Dictionary<string, LootItem>
    {
        ["metal"] = new("Металл", Volume: 30, Price: 10),
    };

    private FakeConnection Connect(string? weapon = null, string? token = null)
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, token, "Pilot", null, weapon);
        return connection;
    }

    private Player PlayerOf(FakeConnection connection) => (Player)_room.Entity(IdOf(connection))!;

    /// <summary>Ставит игрока вплотную к предмету, чтобы его забрал тракторный луч.</summary>
    private void PlaceNear(FakeConnection connection, LootDto drop, double offset) =>
        Place(IdOf(connection), drop.X, drop.Y + offset);

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Pirate PirateOf(FakeConnection observer) =>
        observer.Last<PlayersMsg>().Players.Where(p => p.Kind == Protocol.PirateKind).Select(p => (Pirate)_room.Entity(p.Id)!).Single();

    private void Place(int id, double x, double y) => _room.Entity(id)!.Ship = new ShipState { X = x, Y = y };

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private static IReadOnlyList<LootDto> LootOf(FakeConnection observer) => observer.Last<SnapshotMsg>().Loot ?? [];

    /// <summary>Помечает предмет и берёт его: подбор ручной, сам луч ничего не хватает.</summary>
    private void Grab(FakeConnection a, int lootId)
    {
        _room.SetLootTarget(a, lootId);
        _room.Grab(a);
        _room.Step(); // чтобы подбор попал в снапшот
    }

    /// <summary>Игрок с пушкой, убивающей с одного попадания, сбивает пирата у логова.</summary>
    private (FakeConnection Conn, Pirate Pirate) KillPirate()
    {
        var a = Connect(weapon: "doom");
        var pirate = PirateOf(a);
        Place(pirate.Id, LairX, LairY);
        Place(IdOf(a), LairX, LairY + 300);
        _room.SetTarget(a, pirate.Id);
        _room.SetFire(a, true);
        _room.Step();
        return (a, pirate);
    }

    [Fact]
    public void KilledPirate_DropsItemsFromItsTable()
    {
        var (a, _) = KillPirate();

        var drop = Assert.Single(LootOf(a));
        Assert.Equal(("metal", 2), (drop.I, drop.N));
        Assert.False(drop.C); // обломки, не контейнер
    }

    [Fact]
    public void Drop_LandsNextToTheWreck()
    {
        var (a, pirate) = KillPirate();

        var drop = Assert.Single(LootOf(a));
        var distance = Math.Sqrt(Sq(drop.X - pirate.Ship.X) + Sq(drop.Y - pirate.Ship.Y));
        Assert.InRange(distance, 0, 40);
    }

    [Fact]
    public void Drop_IsDeterministicForASeed()
    {
        var first = LootOf(KillPirate().Conn).Select(d => (d.I, d.N, d.X, d.Y)).ToList();

        _nextConnection = 0;
        _room = NewRoom(Loot());
        var second = LootOf(KillPirate().Conn).Select(d => (d.I, d.N, d.X, d.Y)).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void PirateWithoutATable_DropsNothing()
    {
        _room = NewRoom(Loot(tables: new Dictionary<string, LootTable>()));
        var (a, _) = KillPirate();

        Assert.Empty(LootOf(a));
    }

    [Fact]
    public void KilledDrone_DropsNothing()
    {
        // Источник дропа — только пираты: у дрона нет ни типа, ни таблицы.
        var rules = new CombatRules(RespawnSeconds: 2, ProtectionSeconds: 0, SpawnJitter: 0,
            Drones: [new DroneSpec("Учебный дрон", "light", 0, -600)]);
        _room = NewRoom(Loot(), rules);

        var a = Connect(weapon: "doom");
        var drone = a.Last<PlayersMsg>().Players.Single(p => p.Kind == Protocol.DroneKind);
        Place(IdOf(a), 0, -300);
        _room.SetTarget(a, drone.Id);
        _room.SetFire(a, true);
        _room.Step();

        Assert.Equal(1, a.Last<SnapshotMsg>().Kills?.Count);
        Assert.Empty(LootOf(a));
    }

    [Fact]
    public void MaxItems_CapsTheField()
    {
        _room = NewRoom(Loot(
            maxItems: 1,
            tables: new Dictionary<string, LootTable>
            {
                ["pirate"] = new([new LootRoll("metal", 1, 1, 1), new LootRoll("tech", 1, 1, 1)]),
            }));
        var (a, _) = KillPirate();

        Assert.Single(LootOf(a));
    }

    [Fact]
    public void ExpiredLoot_DisappearsAndClearsTheSelection()
    {
        _room = NewRoom(Loot(lifetimeSeconds: 0.5)); // 10 тиков
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));

        _room.SetLootTarget(a, drop.Id);
        var player = (Player)_room.Entity(IdOf(a))!;
        Assert.Equal(drop.Id, player.SelectedLootId);

        Steps(SimConfig.TickRate);

        Assert.Empty(LootOf(a));
        Assert.Equal(0, player.SelectedLootId);
    }

    [Fact]
    public void SelectedLoot_SurvivesWhileTheItemLies()
    {
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));
        _room.SetLootTarget(a, drop.Id);

        Steps(20);

        Assert.Equal(drop.Id, ((Player)_room.Entity(IdOf(a))!).SelectedLootId);
        Assert.Single(LootOf(a));
    }

    [Fact]
    public void BalanceChange_RemovesItemsThatLeftTheCatalog()
    {
        var (a, _) = KillPirate();
        Assert.Single(LootOf(a));

        // «Металл» убрали из loot.json на лету — подписать такой предмет на экране уже нечем.
        var without = new Dictionary<string, LootItem> { ["tech"] = Items["tech"] };
        _room.ApplyBalance(TestBalance.Create(Rules, Npcs(), Loot(items: without, tables: new Dictionary<string, LootTable>())));
        _room.Step();

        Assert.Empty(LootOf(a));
        Assert.Equal(0, ((Player)_room.Entity(IdOf(a))!).SelectedLootId);
    }

    [Fact]
    public void Drop_InheritsTheInertiaOfTheKilled()
    {
        var a = Connect(weapon: "doom");
        var pirate = PirateOf(a);
        Place(IdOf(a), LairX, LairY + 300);

        // Скорость задаём сами: ждать, пока разгонится ИИ, — значит проверять поведение пирата вместо инерции.
        // Movement.Step её не обнуляет, а гасит, так что до момента гибели она доживёт.
        _room.Entity(pirate.Id)!.Ship = new ShipState { X = LairX, Y = LairY, Vx = 100 };

        _room.SetTarget(a, pirate.Id);
        _room.SetFire(a, true);
        _room.Step();

        // Battle гасит скорость при гибели, поэтому обломкам её надо было запомнить заранее.
        Assert.True(Math.Abs(pirate.DeathVx) + Math.Abs(pirate.DeathVy) > 0, "the speed at death was lost");

        var drop = Assert.Single(LootOf(a));
        var (x, y) = (drop.X, drop.Y);
        Steps(1);
        var moved = LootOf(a).Single();
        Assert.True(Math.Abs(moved.X - x) + Math.Abs(moved.Y - y) > 0, "the drop did not drift");
    }

    [Fact]
    public void GrabbingAnItemInRange_PutsItInTheCargo()
    {
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));

        PlaceNear(a, drop, 100); // ближе 130
        Grab(a, drop.Id);

        Assert.Empty(LootOf(a));
        var cargo = a.Last<CargoMsg>();
        Assert.Equal(2, cargo.Items["metal"]);
        Assert.Equal(2, cargo.Used);
        Assert.Equal(20, cargo.Max);
        Assert.Equal(0, PlayerOf(a).SelectedLootId); // предмет забран — выбор снят
    }

    [Fact]
    public void ItemNearby_IsNotTakenWithoutTheCommand()
    {
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));

        // Подбор ручной: можно висеть над обломками сколько угодно, пока не скомандуешь.
        PlaceNear(a, drop, 10);
        _room.SetLootTarget(a, drop.Id);
        Steps(20);

        Assert.Single(LootOf(a));
        Assert.True(PlayerOf(a).Cargo.IsEmpty);
    }

    [Fact]
    public void GrabbingFromTooFar_SaysSoAndLeavesTheItem()
    {
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));

        PlaceNear(a, drop, 160); // дальше 130
        Grab(a, drop.Id);

        Assert.Single(LootOf(a));
        Assert.True(PlayerOf(a).Cargo.IsEmpty);
        Assert.Equal(Protocol.TooFarNotice, a.Last<NoticeMsg>().Code);
    }

    [Fact]
    public void GrabWithoutASelectedItem_DoesNothing()
    {
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));
        PlaceNear(a, drop, 10);

        _room.Grab(a); // ничего не выбрано
        _room.Step();

        Assert.Single(LootOf(a));
        Assert.Empty(a.Messages.OfType<NoticeMsg>());
    }

    [Fact]
    public void Pickup_IsVisibleToEveryone()
    {
        var (a, _) = KillPirate();
        var b = Connect();
        var drop = Assert.Single(LootOf(a));

        PlaceNear(a, drop, 100);
        Grab(a, drop.Id);

        // Чужой луч объясняет, куда делся предмет, поэтому подбор едет в общий снапшот.
        var pick = Assert.Single(b.Last<SnapshotMsg>().Picks ?? []);
        Assert.Equal((IdOf(a), drop.Id, "metal", 2), (pick.By, pick.Id, pick.I, pick.N));
    }

    [Fact]
    public void DeadPlayer_CannotGrab()
    {
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));

        PlaceNear(a, drop, 100);
        PlayerOf(a).Hp = 0; // Battle заметит это в ближайшем тике
        _room.Step();
        Assert.True(PlayerOf(a).IsDead);

        Grab(a, drop.Id);

        Assert.Single(LootOf(a));
        Assert.True(PlayerOf(a).Cargo.IsEmpty);
    }

    [Fact]
    public void TwoPlayers_TheFirstToGrabTakesIt()
    {
        var (a, _) = KillPirate();
        var b = Connect();
        var drop = Assert.Single(LootOf(a));

        // Оба рядом, но право на добычу теперь у того, кто скомандовал первым, а не у ближайшего.
        PlaceNear(a, drop, 120);
        PlaceNear(b, drop, 20);
        Grab(a, drop.Id);

        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]);

        Grab(b, drop.Id);
        Assert.True(PlayerOf(b).Cargo.IsEmpty); // предмета уже нет
        Assert.Equal(0, PlayerOf(b).SelectedLootId);
    }

    [Fact]
    public void FullCargo_RefusesTheGrabAndSaysSo()
    {
        _room = NewRoom(Loot(items: BulkyItems)); // «Металл ×2» — это 60 при ёмкости 20
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));

        PlaceNear(a, drop, 100);
        Grab(a, drop.Id);

        Assert.Single(LootOf(a)); // предмет остался лежать
        Assert.True(PlayerOf(a).Cargo.IsEmpty); // и ничего не взято даже частично
        Assert.Equal(1, a.Count<NoticeMsg>());
        Assert.Equal(Protocol.CargoFullNotice, a.Last<NoticeMsg>().Code);

        Grab(a, drop.Id); // подряд не тараторим
        Assert.Equal(1, a.Count<NoticeMsg>());

        Steps(SimConfig.TickRate * 5); // прошло fullHoldSeconds
        Grab(a, drop.Id);
        Assert.Equal(2, a.Count<NoticeMsg>());
    }

    [Fact]
    public void ItemGoesToWhoeverHasRoom()
    {
        _room = NewRoom(Loot(items: BulkyItems));
        var (a, _) = KillPirate();
        var b = Connect();
        _room.SetHull(b, "heavy"); // трюм 60 — влезает
        var drop = Assert.Single(LootOf(a));

        PlaceNear(a, drop, 40);
        PlaceNear(b, drop, 100);
        Grab(a, drop.Id); // мест нет — предмет остался
        Grab(b, drop.Id);

        Assert.True(PlayerOf(a).Cargo.IsEmpty);
        Assert.Equal(2, PlayerOf(b).Cargo.Items["metal"]);
    }

    [Fact]
    public void Cargo_SurvivesDeathAndRespawn()
    {
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));
        PlaceNear(a, drop, 100);
        Grab(a, drop.Id);
        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]);

        // GDD §24: игрок не теряет груз при уничтожении корабля.
        PlayerOf(a).Hp = 0;
        _room.Step();
        Assert.True(PlayerOf(a).IsDead);
        Steps(SimConfig.TickRate * 3);

        Assert.False(PlayerOf(a).IsDead);
        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]);
    }

    [Fact]
    public void Cargo_SurvivesReconnect()
    {
        const string token = "loot-session-bbbb";
        var a = Connect(weapon: "doom", token: token);
        var pirate = PirateOf(a);
        Place(pirate.Id, LairX, LairY);
        Place(IdOf(a), LairX, LairY + 300);
        _room.SetTarget(a, pirate.Id);
        _room.SetFire(a, true);
        _room.Step();
        var drop = Assert.Single(LootOf(a));
        PlaceNear(a, drop, 100);
        Grab(a, drop.Id);
        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]);

        _room.Disconnect(a);
        var back = Connect(token: token);

        Assert.True(back.Last<WelcomeMsg>().Resumed);
        Assert.Equal(2, back.Last<CargoMsg>().Items["metal"]); // трюм виден сразу, без первого подбора
    }

    [Fact]
    public void SmallerHull_KeepsTheCargoButBlocksNewItems()
    {
        _room = NewRoom(Loot(items: BulkyItems));
        var a = Connect(weapon: "doom");
        _room.SetHull(a, "heavy"); // трюм 60
        var pirate = PirateOf(a);
        Place(pirate.Id, LairX, LairY);
        Place(IdOf(a), LairX, LairY + 300);
        _room.SetTarget(a, pirate.Id);
        _room.SetFire(a, true);
        _room.Step();

        var drop = Assert.Single(LootOf(a));
        PlaceNear(a, drop, 100);
        Grab(a, drop.Id);
        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]); // 60 из 60

        // Пересели на лёгкий (20): груз не выбрасывается (GDD §24), но это перегруз.
        _room.SetHull(a, "light");
        var cargo = a.Last<CargoMsg>();
        Assert.Equal((60.0, 20.0), (cargo.Used, cargo.Max));
        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]);
    }

    /// <summary>Подбирает выпавший с пирата предмет и возвращает игрока.</summary>
    private FakeConnection WithCargo()
    {
        var (a, _) = KillPirate();
        var drop = Assert.Single(LootOf(a));
        PlaceNear(a, drop, 100);
        Grab(a, drop.Id);
        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]);
        return a;
    }

    [Fact]
    public void SellingAtTheStation_TurnsCargoIntoCredits()
    {
        var a = WithCargo();

        Place(IdOf(a), SimConfig.StationX, SimConfig.StationY);
        _room.Sell(a, null);

        Assert.True(PlayerOf(a).Cargo.IsEmpty);
        Assert.Equal(20, PlayerOf(a).Credits); // «Металл ×2» по 10 кредитов
        var cargo = a.Last<CargoMsg>();
        Assert.Equal((0.0, 20), (cargo.Used, cargo.Credits));
        Assert.Equal(Protocol.UnloadedNotice, a.Last<NoticeMsg>().Code);
    }

    [Fact]
    public void SellingOneKind_LeavesTheRest()
    {
        var a = WithCargo();
        PlayerOf(a).Cargo.Add("tech", 1); // «Компонент» по 200

        Place(IdOf(a), SimConfig.StationX, SimConfig.StationY);
        _room.Sell(a, "metal");

        Assert.Equal(20, PlayerOf(a).Credits);
        Assert.False(PlayerOf(a).Cargo.Items.ContainsKey("metal"));
        Assert.Equal(1, PlayerOf(a).Cargo.Items["tech"]); // что везти дальше — решает игрок
    }

    [Fact]
    public void CargoStays_WithoutTheSellCommand()
    {
        var a = WithCargo();

        // Сдача ручная: стоять в круге станции мало.
        Place(IdOf(a), SimConfig.StationX, SimConfig.StationY);
        Steps(20);

        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]);
        Assert.Equal(0, PlayerOf(a).Credits);
    }

    [Fact]
    public void SellingFarFromTheStation_SaysSoAndKeepsTheCargo()
    {
        var a = WithCargo();

        Place(IdOf(a), SimConfig.StationX, SimConfig.StationY + 260); // дальше stationRange 200
        _room.Sell(a, null);

        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]);
        Assert.Equal(0, PlayerOf(a).Credits);
        Assert.Equal(Protocol.TooFarNotice, a.Last<NoticeMsg>().Code);
    }

    [Fact]
    public void StationUnloadOff_RefusesToSell()
    {
        _room = NewRoom(Loot(stationUnload: false));
        var a = WithCargo();

        Place(IdOf(a), SimConfig.StationX, SimConfig.StationY);
        _room.Sell(a, null);

        // Продажу выключают на лету — механику можно снять прямо на плейтесте.
        Assert.Equal(2, PlayerOf(a).Cargo.Items["metal"]);
        Assert.Equal(0, PlayerOf(a).Credits);
    }

    /// <summary>Точка контейнера: далеко и от станции, и от логова — чтобы пират не мешал.</summary>
    private const double BoxX = 1500;
    private const double BoxY = 0;

    private static LootContainer Box(double respawnSeconds = 5, string? item = "metal", string? table = null) =>
        new("Ящик", BoxX, BoxY, item, 3, table, respawnSeconds);

    /// <summary>
    /// Ждёт, пока точка бросит удачную монету и контейнер появится. Появление вероятностное: срок в файле —
    /// это среднее время между попытками, а не расписание.
    /// </summary>
    /// <returns>Тики, которые пришлось прождать; -1 — так и не появился.</returns>
    private int WaitForBox(FakeConnection observer, int maxTicks = 20 * SimConfig.TickRate)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            _room.Step();
            if (LootOf(observer).Any(d => d.C)) return i;
        }
        return -1;
    }

    /// <summary>Подлетает к контейнеру и забирает его.</summary>
    private FakeConnection TakeBox()
    {
        var a = Connect();
        Assert.True(WaitForBox(a) >= 0, "the container never appeared");
        var box = Assert.Single(LootOf(a));
        Assert.True(box.C);

        Place(IdOf(a), BoxX, BoxY);
        Grab(a, box.Id);
        Assert.Empty(LootOf(a));
        return a;
    }

    [Fact]
    public void Container_AppearsAndNeverExpires()
    {
        _room = NewRoom(Loot(lifetimeSeconds: 0.5, containers: [Box()]));
        var a = Connect();
        Assert.True(WaitForBox(a) >= 0, "the container never appeared");

        var box = Assert.Single(LootOf(a));
        Assert.Equal(("metal", 3, true), (box.I, box.N, box.C));
        Assert.Equal((BoxX, BoxY), (box.X, box.Y));

        // Обломки за это время протухли бы, а контейнер ждёт игрока.
        Steps(SimConfig.TickRate * 2);
        Assert.Single(LootOf(a));
    }

    [Fact]
    public void Container_ComesBackAfterAWhile()
    {
        _room = NewRoom(Loot(containers: [Box(respawnSeconds: 5)]));
        var a = TakeBox();
        Assert.Equal(3, PlayerOf(a).Cargo.Items["metal"]);

        // Срок вразнобой: раньше половины среднего точка не наполняется никогда, к полутора — уже наверняка.
        Steps(SimConfig.TickRate * 2);
        Assert.Empty(LootOf(a));

        Steps(SimConfig.TickRate * 6);
        Assert.Single(LootOf(a));
    }

    [Fact]
    public void ContainerPoints_RollTheirOwnChance()
    {
        // Точка с шансом 0.2 ждёт своего контейнера в несколько раз дольше, чем точка, которая не промахивается:
        // усредняем по сидам, потому что каждая отдельная попытка — это монета.
        var waits = new List<double>();
        foreach (var chance in new[] { 0.2, 1.0 })
        {
            var total = 0.0;
            for (var seed = 1; seed <= 12; seed++)
            {
                _nextConnection = 0;
                _room = NewRoom(Loot(containers: [new LootContainer("Точка", BoxX, BoxY, "metal", 3, null, 2, chance)]), seed: seed);
                var a = Connect();
                var waited = WaitForBox(a, 60 * SimConfig.TickRate);
                Assert.True(waited >= 0, $"chance {chance}, seed {seed}: the container never appeared");
                total += waited;
            }
            waits.Add(total / 12);
        }

        Assert.True(waits[0] > waits[1] * 2, $"chance did not matter: {waits[0]:0} vs {waits[1]:0} ticks of waiting");
    }

    [Fact]
    public void MaxContainers_CapsTheField()
    {
        var points = Enumerable.Range(0, 10)
            .Select(i => new LootContainer($"Точка {i}", 1500 + i * 120, 0, "metal", 1, null, 1))
            .ToList();
        _room = NewRoom(Loot(containers: points, maxContainers: 3));
        var a = Connect();

        Steps(30 * SimConfig.TickRate);

        Assert.Equal(3, LootOf(a).Count(d => d.C));
    }

    [Fact]
    public void ContainerPoints_DoNotFillAllAtOnce()
    {
        // Десять точек с одним сроком: при старте комнаты они получают разные первые попытки, а не тикают в такт.
        var points = Enumerable.Range(0, 10)
            .Select(i => new LootContainer($"Точка {i}", 1500 + i * 120, 0, "metal", 1, null, 20))
            .ToList();
        _room = NewRoom(Loot(containers: points));
        var a = Connect();

        Steps(2 * SimConfig.TickRate);
        var early = LootOf(a).Count(d => d.C);

        Steps(40 * SimConfig.TickRate);
        var later = LootOf(a).Count(d => d.C);

        Assert.InRange(early, 0, 4);
        Assert.True(later > early, $"points did not fill over time: {early} → {later}");
    }

    [Fact]
    public void OneShotContainer_NeverComesBack()
    {
        _room = NewRoom(Loot(containers: [Box(respawnSeconds: 0)]));
        var a = TakeBox();

        Steps(SimConfig.TickRate * 10);

        Assert.Empty(LootOf(a));
    }

    [Fact]
    public void ContainerWithATable_RollsOneItem()
    {
        _room = NewRoom(Loot(
            containers: [Box(item: null, table: "cache")],
            tables: new Dictionary<string, LootTable>
            {
                ["cache"] = new([new LootRoll("tech", 1, 1, 2)]),
            }));
        var a = Connect();
        Assert.True(WaitForBox(a) >= 0, "the container never appeared");

        // Контейнер — один предмет, а не облако: несколько стопок в одной точке не разобрать тапом.
        var box = Assert.Single(LootOf(a));
        Assert.Equal("tech", box.I);
        Assert.InRange(box.N, 1, 2);
    }

    [Fact]
    public void BalanceChange_MovesContainers()
    {
        _room = NewRoom(Loot(containers: [Box()]));
        var a = Connect();
        Assert.True(WaitForBox(a) >= 0, "the container never appeared");
        Assert.Equal(BoxX, Assert.Single(LootOf(a)).X);

        var moved = new LootContainer("Ящик", -1500, 0, "metal", 3, null, 5);
        _room.ApplyBalance(TestBalance.Create(Rules, Npcs(), Loot(containers: [moved])));
        Assert.True(WaitForBox(a) >= 0, "the moved container never appeared");

        Assert.Equal(-1500, Assert.Single(LootOf(a)).X);
    }

    [Fact]
    public void Drift_DampsToAStop()
    {
        var drop = new LootDrop(1, "metal", 1, 0, 0, 100, 0, long.MaxValue);
        for (var i = 0; i < 4 * SimConfig.TickRate; i++) drop.Step(4, SimConfig.Dt);

        // За driftDampTime скорость падает примерно до 5% — обломки почти встают.
        Assert.InRange(Math.Abs(drop.Vx), 0, 6);
        Assert.InRange(drop.X, 100, 200);
    }

    private static double Sq(double v) => v * v;
}
