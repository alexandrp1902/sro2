using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>Обучение (GDD §54) и задания станций (§36): уничтожить, собрать, доставить.</summary>
public sealed class MissionTests : IDisposable
{
    private const string Password = "secret";
    private const int JumpTicks = SimConfig.TickRate; // jumpSeconds: 1

    /// <summary>home (станция, дрон) — wild (без станции, логово пиратов) — port (станция).</summary>
    private static readonly GalaxyRules Rules = new(
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new("Home", Gates: [new GateDef("wild", 3000, 0)],
                Traders: new TraderRules(Count: 1, RespawnSeconds: 60, Type: "trader", Throttle: 0.7),
                Spawns: [new NpcSpawn("ranger", 1, 0, 2500)],
                Drones: [new DroneSpec("Учебный дрон", "light", 0, -600, Table: "drone")]),
            // Камни летают только у дома: иначе охота выпадала бы то сюда, то к соседям, и тест зависел бы от сида.
            ["wild"] = new("Wild", Danger: 3, Pvp: GalaxyRules.PvpFree, Station: false, Meteors: 0,
                Gates: [new GateDef("home", -3000, 0), new GateDef("port", 0, 3000)],
                Spawns: [new NpcSpawn("pirate", 1, 0, -2000)]),
            ["port"] = new("Port", Gates: [new GateDef("wild", 0, -3000)]),
        },
        Links: [new LinkDef("home", "wild", 10), new LinkDef("wild", "port", 10)]);

    private static readonly NpcRules Npcs = new(
        RespawnSeconds: 1,
        Types: new Dictionary<string, NpcType>
        {
            ["pirate"] = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320),
            ["trader"] = new("Торговец", "light", "pulse", Faction: NpcType.TraderFaction, Hp: 400, Shield: 100, Damage: 0.3),
            ["ranger"] = new("Рейнджер", "light", "pulse", Faction: NpcType.RangerFaction,
                Hp: 600, Shield: 200, Damage: 0.45, HoldRange: 360, RetreatHp: 0.3, DefendRange: 2500, LeashRange: 3500),
        });

    private static readonly LootRules Loot = new(
        FadeSeconds: 0,
        Items: new Dictionary<string, LootItem> { ["metal"] = new("Металл", Volume: 1, Price: 10) },
        Tables: new Dictionary<string, LootTable>
        {
            ["drone"] = new([new LootRoll("metal", 1, 2, 2)]),
            ["rock"] = new([new LootRoll("metal", 1, 1, 1)]),
        });

    private static readonly IReadOnlyList<TutorialStep> Tutorial =
    [
        new(MissionRules.UndockStep, "Вылетите", Reward: 10),
        new(MissionRules.DroneStep, "Дрон", Reward: 20),
        new(MissionRules.GrabStep, "Груз", Reward: 30),
        new(MissionRules.SellStep, "Продайте", Reward: 40),
        new(MissionRules.JumpStep, "Прыжок", Reward: 50),
    ];

    private static readonly MissionRules KillOnly = new(DangerBonus: 0, Tutorial: Tutorial, Kill: [new KillTemplate(null, 2, 2, 100)]);
    private static readonly MissionRules CollectOnly = new(Tutorial: Tutorial, Collect: [new CollectTemplate("metal", 3, 3, 2)]);
    private static readonly MissionRules DeliverOnly = new(Tutorial: Tutorial, Deliver: [new DeliverTemplate(5, 5, 10, 100)]);

    /// <summary>Письмо в port: 2 прыжка, и времени ровно столько, чтобы срок можно было проверить не ожиданием.</summary>
    private static readonly MissionRules CourierOnly = new(
        Tutorial: Tutorial,
        Courier: [new CourierTemplate(Reward: 200, PerJump: 100, SecondsPerJump: 60, Seconds: 60)]);

    private static readonly MissionRules HuntOnly = new(
        DangerBonus: 0, Tutorial: Tutorial, Hunt: [new HuntTemplate(null, 2, 2, 50)]);

    private static readonly MissionRules HuntLargeOnly = new(
        DangerBonus: 0, Tutorial: Tutorial, Hunt: [new HuntTemplate("large", 2, 2, 100)]);

    /// <summary>Сопровождение с одной засадой: её хватает, чтобы увидеть и появление волны, и счётчик.</summary>
    private static readonly MissionRules EscortOnly = new(
        DangerBonus: 0,
        Tutorial: Tutorial,
        Escort: [new EscortTemplate(1, 1, 300, 200, Radius: 900, AwaySeconds: 2)],
        Ambush: [[new InvasionGroup("pirate", 1, 1)]]);

    /// <summary>Патруль из двух точек без засад: правило «звено ждёт» проверяется и без боя.</summary>
    private static readonly MissionRules PatrolOnly = new(
        DangerBonus: 0,
        Tutorial: Tutorial,
        Patrol: [new PatrolTemplate(2, 2, 300, 160, Wing: 2, Radius: 700)]);

    /// <summary>Тот же патруль, но с пиратами: на двух точках засада всегда на второй.</summary>
    private static readonly MissionRules PatrolFight = PatrolOnly with
    {
        Ambush = [[new InvasionGroup("pirate", 1, 2)]],
    };

    /// <summary>Камни для охоты: сами не появляются — тест запускает их руками, куда ему надо.</summary>
    private static readonly MeteorRules Meteors = new(
        MaxAlive: 0,
        LifetimeSeconds: 90,
        Gravity: 0,
        Sizes: new Dictionary<string, MeteorSize>
        {
            ["small"] = new("Мелкий метеорит", 14, 60, 240, 300, 110, 1, "rock"),
            ["large"] = new("Крупный метеорит", 34, 320, 180, 220, 300, 1, "rock"),
        });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-missions-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private Galaxy _galaxy;
    private int _nextConnection;

    public MissionTests()
    {
        _accounts = new AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _galaxy = New(KillOnly);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Galaxy New(MissionRules missions, ReputationRules? reputation = null) =>
        new(TestBalance.Create(new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), Npcs, Loot,
                Meteors, shop: new ShopRules(StartCredits: 1000), reputation: reputation) with
            {
                GalaxySet = Rules,
                MissionSet = missions,
            }, NullLogger.Instance, _accounts, roll: () => 0, random: seed => new Random(seed));

    /// <summary>Расстрелянный камень: сбит именно пилотом, а не разбился о чей-то борт.</summary>
    private void Shoot(FakeConnection connection, string size)
    {
        var room = RoomOf(connection);
        var rock = room.LaunchMeteor(size, 1000, 1000, 0, 0)!;
        rock.Hp = 0;
        rock.KilledBy = IdOf(connection);
        Steps(1);
    }

    /// <summary>
    /// Камень разбился о чей-то корабль. by — чей: id пилота засчитывает камень ему (M16a),
    /// 0 — камень разбился о чужой борт, и засчитывать его некому.
    /// </summary>
    private void Ram(FakeConnection connection, string size, int by = 0)
    {
        var rock = RoomOf(connection).LaunchMeteor(size, 1000, 1000, 0, 0)!;
        rock.Hp = 0;
        rock.Rammed = true;
        rock.KilledBy = by;
        Steps(1);
    }

    private FakeConnection Pilot(string name = "Alice")
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.JoinAccount(connection, login.Id, login.Name);
        _galaxy.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    /// <summary>Пилот как есть, сразу после входа: в доке. Обучение начинается именно оттуда.</summary>
    private FakeConnection PilotInDock(string name = "Alice")
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.JoinAccount(connection, login.Id, login.Name);
        return connection;
    }

    /// <summary>Пилот, который обучение пропустил, — в космосе у станции.</summary>
    private FakeConnection Veteran(string name = "Alice")
    {
        var a = Pilot(name);
        Do(a, r => r.Mission(a, Protocol.SkipTutorial, null));
        Do(a, r => r.Dock(a, false));
        return a;
    }

    private FakeConnection Guest()
    {
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.Join(connection, null, "Guest", null);
        _galaxy.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    private string AccountId(string name = "Alice") => _accounts.Login(name, Password).Id;

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Room RoomOf(FakeConnection connection) => _galaxy.RoomOf(connection)!;

    private Player PlayerOf(FakeConnection connection) => RoomOf(connection).Pilot(IdOf(connection))!;

    private static MissionsMsg Missions(FakeConnection connection) => connection.Last<MissionsMsg>();

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private void Do(FakeConnection connection, Action<Room> command) => _galaxy.With(connection, command);

    private void Place(FakeConnection connection, double x, double y) =>
        PlayerOf(connection).Ship = new ShipState { X = x, Y = y };

    private void JumpTo(FakeConnection connection, string to)
    {
        var gate = RoomOf(connection).Balance.SystemDef.GateTo(to)!;
        Place(connection, gate.X, gate.Y);
        Do(connection, r => r.Jump(connection, to));
        Steps(JumpTicks);
        Assert.Equal(to, RoomOf(connection).SystemId);
    }

    private void Dock(FakeConnection connection)
    {
        Place(connection, 0, 50);
        Do(connection, r => r.Dock(connection, true));
        Assert.True(connection.Last<HangarMsg>().Docked);
    }

    /// <summary>Вылетает, находит в космосе count штук item (будто обломки метеорита) и подбирает их.</summary>
    private void Gather(FakeConnection connection, string item, int count)
    {
        if (PlayerOf(connection).Docked) Do(connection, r => r.Dock(connection, false));
        RoomOf(connection).SpillAt(item, count, -900, 900);
        Steps(1);
        GrabAll(connection, item);
    }

    /// <summary>Подбирает все стопки item, какие видно в снапшоте.</summary>
    private void GrabAll(FakeConnection connection, string item)
    {
        foreach (var drop in connection.Last<SnapshotMsg>().Loot!.Where(d => d.I == item).ToList())
        {
            Place(connection, drop.X, drop.Y);
            Do(connection, r => r.SetLootTarget(connection, drop.Id));
            Do(connection, r => r.Grab(connection));
        }
    }

    /// <summary>Встаёт рядом с кораблём id и уничтожает его одним выстрелом.</summary>
    private void Kill(FakeConnection connection, int id)
    {
        var target = RoomOf(connection).Entity(id)!;
        PlayerOf(connection).WeaponId = "doom";
        Place(connection, target.Ship.X, target.Ship.Y + 200);
        Do(connection, r => r.SetTarget(connection, id));
        Do(connection, r => r.SetFire(connection, true));
        Steps(2);
        Do(connection, r => r.SetFire(connection, false));
        Assert.True(target.IsDead);
    }

    private static int NpcId(FakeConnection connection, string kind) =>
        connection.Last<PlayersMsg>().Players.First(p => p.Kind == kind).Id;

    private void Accept(FakeConnection connection, string kind)
    {
        var offer = Missions(connection).Offers.First(o => o.Kind == kind);
        Do(connection, r => r.Mission(connection, Protocol.AcceptMission, offer.Id));
        Assert.Equal(offer, Missions(connection).Active?.Offer);
    }

    private static int Credits(FakeConnection connection) => connection.Last<CargoMsg>().Credits;

    /// <summary>Конвой этого задания; null — его уже нет.</summary>
    private Trader? Convoy(FakeConnection connection) =>
        RoomOf(connection).Traders.FirstOrDefault(t => t.MissionId != 0);

    /// <summary>Живое звено рейнджеров этого задания.</summary>
    private List<Pirate> Wing(FakeConnection connection) =>
        RoomOf(connection).Pirates.Where(p => p.MissionId != 0 && p.Type.IsRanger && !p.IsDead && !p.Gone).ToList();

    /// <summary>Живые пираты, вызванные заданием: засада на конвой или те, кто ждал патруль на точке.</summary>
    private List<Pirate> Ambush(FakeConnection connection) =>
        RoomOf(connection).Pirates.Where(p => p.MissionId != 0 && p.Type.IsPirate && !p.IsDead && !p.Gone).ToList();

    /// <summary>Гонит звено и пилота к текущей метке: в тесте важно правило, а не дорога.</summary>
    private void ReachWaypoint(FakeConnection connection)
    {
        var mark = Missions(connection).Mark!;
        foreach (var ranger in Wing(connection)) ranger.Ship = new ShipState { X = mark.X, Y = mark.Y };
        Place(connection, mark.X, mark.Y);
        Steps(2);
    }

    /// <summary>Берёт живое задание и вылетает: конвой и звено появляются именно на вылете.</summary>
    private void Launch(FakeConnection connection, string kind)
    {
        Dock(connection);
        Accept(connection, kind);
        Do(connection, r => r.Dock(connection, false));
    }

    /// <summary>Ставит пилота рядом с меткой задания — там, куда указывает трекер.</summary>
    private void GoToMark(FakeConnection connection)
    {
        var mark = Missions(connection).Mark!;
        var (x, y) = mark.Ship != 0
            ? (RoomOf(connection).Entity(mark.Ship)!.Ship.X, RoomOf(connection).Entity(mark.Ship)!.Ship.Y)
            : (mark.X, mark.Y);
        Place(connection, x, y);
    }

    [Fact]
    public void NewPilot_StartsDocked_AndWalksTheTutorial()
    {
        var a = PilotInDock();
        Assert.True(a.Last<HangarMsg>().Docked);
        Assert.Equal(new TutorialDto(0, 5, MissionRules.UndockStep, "Вылетите", "", MissionRules.UndockStep), Missions(a).Tutorial);

        Do(a, r => r.Dock(a, false));
        Assert.Equal(new MissionDoneDto(Protocol.TutorialDone, 10, "Вылетите"), Missions(a).Done);
        Assert.Equal(MissionRules.DroneStep, Missions(a).Tutorial?.Id);

        Kill(a, NpcId(a, Protocol.DroneKind));
        Assert.Equal(MissionRules.GrabStep, Missions(a).Tutorial?.Id);

        var drop = a.Last<SnapshotMsg>().Loot!.Single();
        Place(a, drop.X, drop.Y);
        Do(a, r => r.SetLootTarget(a, drop.Id));
        Do(a, r => r.Grab(a));
        Assert.Equal(MissionRules.SellStep, Missions(a).Tutorial?.Id);

        Dock(a);
        Do(a, r => r.Sell(a, null));
        Assert.Equal(MissionRules.JumpStep, Missions(a).Tutorial?.Id);

        Do(a, r => r.Dock(a, false));
        JumpTo(a, "wild");
        var last = Missions(a);
        Assert.Null(last.Tutorial);
        Assert.Equal(new MissionDoneDto(Protocol.TutorialDone, 50, "Прыжок", Last: true), last.Done);
        // Старт, награды за пять шагов и два металла с дрона.
        Assert.Equal(1000 + 10 + 20 + 30 + 40 + 50 + 20, Credits(a));
        Assert.Equal(MissionLog.Finished, _accounts.Profile(AccountId())!.TutorialStep);
    }

    [Fact]
    public void Tutorial_CountsStepsOnlyInOrder()
    {
        var a = Pilot();
        Do(a, r => r.Dock(a, false));
        JumpTo(a, "wild"); // прыжок — пятый шаг, а сейчас второй
        Assert.Equal(MissionRules.DroneStep, Missions(a).Tutorial?.Id);
        Assert.Equal(1010, Credits(a));
    }

    [Fact]
    public void Tutorial_SurvivesRelogin()
    {
        var a = Pilot();
        Do(a, r => r.Dock(a, false));
        _galaxy.Disconnect(a);
        Steps(Room.ReconnectGraceTicks + 1);

        var again = Pilot();
        Assert.False(again.Last<HangarMsg>().Docked); // первый шаг сделан — в доке больше не держим
        Assert.Equal(MissionRules.DroneStep, Missions(again).Tutorial?.Id);
    }

    [Fact]
    public void SkipTutorial_EndsItWithoutRewards()
    {
        var a = PilotInDock();
        Do(a, r => r.Mission(a, Protocol.SkipTutorial, null));
        Assert.Null(Missions(a).Tutorial);
        Assert.Equal(1000, Credits(a));
        Assert.Equal(MissionLog.Finished, _accounts.Profile(AccountId())!.TutorialStep);
    }

    [Fact]
    public void ProfileOlderThanM8_HasNoTutorial()
    {
        _accounts.Save(AccountId(), new AccountProfile(500, "light", "pulse", ["light"], ["pulse"], new Dictionary<string, int>()));
        var a = PilotInDock();
        // В доке, как и всякий вход с M15.6, но без обучения: его шаг не с чего начинать.
        Assert.True(a.Last<HangarMsg>().Docked);
        Assert.Null(Missions(a).Tutorial);
    }

    /// <summary>Профиль старше M18 хранит номер шага — в id он переводится по списку, каким тот был тогда.</summary>
    [Theory]
    [InlineData(null, 2, MissionRules.GrabStep)]
    [InlineData(null, 4, MissionRules.JumpStep)]
    [InlineData("trader", 2, MissionRules.BuyStep)]
    [InlineData("trader", 3, MissionRules.DroneStep)]
    [InlineData(null, int.MaxValue, null)]
    [InlineData("trader", 7, null)]
    public void ProfileOlderThanM18_KeepsItsStep_ByTheOldList(string? career, int index, string? expected)
    {
        // Между шагами обоих путей вставлен новый — «остановиться»: он уже позади.
        _galaxy = New(KillOnly with
        {
            Tutorial = [Tutorial[0], new TutorialStep(MissionRules.StopStep, "Стоп"), .. Tutorial.Skip(1)],
            Tutorials = new Dictionary<string, IReadOnlyList<TutorialStep>>
            {
                ["trader"] =
                [
                    new(MissionRules.UndockStep, "Вылет"), new(MissionRules.StopStep, "Стоп"),
                    new(MissionRules.SellStep, "Продать"), new(MissionRules.BuyStep, "Купить"),
                    new(MissionRules.DroneStep, "Дрон"), new(MissionRules.JumpStep, "Прыжок"),
                    new("sellFar", "Продать там", Kind: MissionRules.SellStep),
                ],
            },
        });
        _accounts.Save(AccountId(), new AccountProfile(
            500, "light", "pulse", ["light"], [], new Dictionary<string, int>(), Tutorial: index, Career: career));
        var a = PilotInDock();
        Assert.Equal(expected, Missions(a).Tutorial?.Id);
        // Переведённый шаг записан сразу, уже по id.
        Assert.Equal(expected ?? MissionLog.Finished, _accounts.Profile(AccountId())!.TutorialStep);
    }

    [Fact]
    public void TutorialStep_IsKeptById_AndAStepThatIsGoneMeansDone()
    {
        _accounts.Save(AccountId(), new AccountProfile(
            500, "light", "pulse", ["light"], [], new Dictionary<string, int>(), TutorialStep: MissionRules.SellStep));
        var a = PilotInDock();
        Assert.Equal(new TutorialDto(3, 5, MissionRules.SellStep, "Продайте", "", MissionRules.SellStep), Missions(a).Tutorial);
        _galaxy.Disconnect(a);
        Steps(Room.ReconnectGraceTicks + 1);

        _accounts.Save(AccountId(), _accounts.Profile(AccountId())! with { TutorialStep = "renamed" });
        Assert.Null(Missions(PilotInDock()).Tutorial);
    }

    /// <summary>Мировые координаты учебного буя — так же, как их посчитает клиент.</summary>
    private (double X, double Y) BuoyAt(FakeConnection connection)
    {
        var buoy = Missions(connection).Tutorial!.Buoy!;
        var room = RoomOf(connection);
        return room.Balance.Place(buoy.Place)!.Orbit.ToWorld(room.OrbitSeconds, buoy.X, buoy.Y);
    }

    private static readonly MissionRules StopFirst = KillOnly with
    {
        Tutorial =
        [
            new(MissionRules.UndockStep, "Вылетите"),
            new(MissionRules.StopStep, "Остановитесь", "Удерживайте {brake}", Reward: 25, HintTouch: "Двойной тап"),
            new(MissionRules.DroneStep, "Дрон"),
        ],
    };

    [Fact]
    public void StopStep_NeedsARunUp_TheBuoy_AndASecondStill()
    {
        _galaxy = New(StopFirst);
        var a = Pilot();
        var step = Missions(a).Tutorial!;
        Assert.Equal((MissionRules.StopStep, "Двойной тап", "st:home"), (step.Kind, step.HintTouch, step.Buoy?.Place));

        // Стоит у дока, газа не трогал — не засчитано, сколько ни стой.
        Steps(SimConfig.TickRate * 2);
        Assert.Equal(MissionRules.StopStep, Missions(a).Tutorial?.Id);

        // Разогнался, но остановился далеко от буя — тоже нет.
        PlayerOf(a).Ship = new ShipState { X = 0, Y = 0, Vx = 100 };
        Steps(1);
        Place(a, -3000, -3000);
        Steps(SimConfig.TickRate * 2);
        Assert.Equal(MissionRules.StopStep, Missions(a).Tutorial?.Id);

        // У буя: полсекунды мало, секунда — шаг.
        var (bx, by) = BuoyAt(a);
        Place(a, bx + 100, by);
        Steps(SimConfig.TickRate / 2);
        Assert.Equal(MissionRules.StopStep, Missions(a).Tutorial?.Id);
        Steps(SimConfig.TickRate);
        Assert.Equal(MissionRules.DroneStep, Missions(a).Tutorial?.Id);
        Assert.Equal(new MissionDoneDto(Protocol.TutorialDone, 25, "Остановитесь"), Missions(a).Done);
        Assert.Null(Missions(a).Tutorial!.Buoy); // у следующего шага буя нет
    }

    [Fact]
    public void StopStep_ForgetsTheRunUp_OnTheNextUndock()
    {
        _galaxy = New(StopFirst);
        var a = Pilot();
        PlayerOf(a).Ship = new ShipState { X = 0, Y = 0, Vx = 100 };
        Steps(1);
        Dock(a);
        Do(a, r => r.Dock(a, false));
        var (bx, by) = BuoyAt(a);
        Place(a, bx, by);
        Steps(SimConfig.TickRate * 2);
        Assert.Equal(MissionRules.StopStep, Missions(a).Tutorial?.Id);
    }

    [Fact]
    public void SellStep_WithAPlaceAndGoods_WaitsForExactlyThem()
    {
        _galaxy = New(KillOnly with
        {
            Tutorial =
            [
                new(MissionRules.UndockStep, "Вылетите"),
                new("sellElsewhere", "Продайте в порту", Kind: MissionRules.SellStep, Place: "st:port"),
                new("sellMetal", "Продайте металл", Kind: MissionRules.SellStep, Place: "st:home", Goods: "metal"),
            ],
        });
        var a = Pilot();
        PlayerOf(a).Cargo.Add("metal", 2);
        Dock(a);
        Do(a, r => r.Sell(a, "metal", 1));
        Assert.Equal("sellElsewhere", Missions(a).Tutorial?.Id); // продали, но не там
    }

    [Fact]
    public void SellAll_CountsTheStep_WhenTheWantedGoodsWereInIt()
    {
        _galaxy = New(KillOnly with
        {
            Tutorial =
            [
                new(MissionRules.UndockStep, "Вылетите"),
                new("sellMetal", "Продайте металл", Kind: MissionRules.SellStep, Place: "st:home", Goods: "metal"),
            ],
        });
        var a = Pilot();
        PlayerOf(a).Cargo.Add("metal", 2);
        Dock(a);
        Do(a, r => r.Sell(a, null));
        Assert.Null(Missions(a).Tutorial);
        Assert.True(Missions(a).Done?.Last);
    }

    [Fact]
    public void KillStep_CountsAPirate_OnlyInItsSystem_AndBoardStepIsTakingAMission()
    {
        _galaxy = New(KillOnly with
        {
            Tutorial =
            [
                new(MissionRules.UndockStep, "Вылетите"),
                new(MissionRules.JumpStep, "В wild", System: "wild"),
                new("pirate", "Сбейте пирата", Kind: MissionRules.KillStep, System: "wild", Reward: 200),
                new(MissionRules.BoardStep, "Возьмите задание", Reward: 100),
            ],
        });
        var a = Pilot();
        JumpTo(a, "wild");
        Assert.Equal((MissionRules.KillStep, "wild"), (Missions(a).Tutorial?.Kind, Missions(a).Tutorial?.System));
        Kill(a, NpcId(a, Protocol.PirateKind));
        Assert.Equal(MissionRules.BoardStep, Missions(a).Tutorial?.Id);

        JumpTo(a, "home");
        Dock(a);
        var before = Credits(a);
        Accept(a, MissionRules.KillKind);
        Assert.Null(Missions(a).Tutorial);
        Assert.Equal(new MissionDoneDto(Protocol.TutorialDone, 100, "Возьмите задание", Last: true), Missions(a).Done);
        Assert.NotNull(Missions(a).Active);
        Assert.Equal(before + 100, Credits(a));
    }

    [Fact]
    public void Guest_HasNoTutorial_ButSeesTheBoard()
    {
        // Guest() уже вылетел: гость, как и пилот, входит в доке (M15.6), просто учить его некому.
        var a = Guest();
        Assert.Null(Missions(a).Tutorial);
        Assert.Equal(4, Missions(a).Offers.Count);
    }

    [Fact]
    public void Board_IsTheSameUntilAMissionIsTaken_AndEmptyWithoutAStation()
    {
        var a = Veteran();
        var board = Missions(a).Offers;
        Assert.All(board, o => Assert.Equal(("wild", "st:home"), (o.System, o.From))); // пираты есть только в wild
        Dock(a);
        Assert.Equal(board, Missions(a).Offers);

        Accept(a, MissionRules.KillKind);
        Assert.NotEqual(board, Missions(a).Offers);

        Do(a, r => r.Dock(a, false));
        JumpTo(a, "wild");
        Assert.Empty(Missions(a).Offers);
    }

    [Fact]
    public void Accept_NeedsTheDock_AndOneMissionAtATime()
    {
        var a = Veteran();
        var offer = Missions(a).Offers[0];
        Do(a, r => r.Mission(a, Protocol.AcceptMission, offer.Id));
        Assert.Equal(Protocol.TooFarNotice, a.Last<NoticeMsg>().Code);
        Assert.Null(Missions(a).Active);

        Dock(a);
        Accept(a, MissionRules.KillKind);
        var second = Missions(a).Offers[0];
        Do(a, r => r.Mission(a, Protocol.AcceptMission, second.Id));
        Assert.Equal(offer.Kind, Missions(a).Active!.Offer.Kind);
        Assert.NotEqual(second.Id, Missions(a).Active!.Offer.Id);
    }

    [Fact]
    public void Kill_CountsOwnKillsInTheTargetSystem_AndPaysOnTheLast()
    {
        var a = Veteran();
        var b = Veteran("Bob");
        Dock(a);
        Accept(a, MissionRules.KillKind);
        var reward = Missions(a).Active!.Offer.Reward;
        Assert.Equal(200, reward); // 2 × 100, опасность без надбавки
        Do(a, r => r.Dock(a, false));
        JumpTo(a, "wild");
        JumpTo(b, "wild");

        var pirate = NpcId(a, Protocol.PirateKind);
        Kill(b, pirate); // чужое убийство не в счёт
        Assert.Equal(0, Missions(a).Active!.Progress);

        Steps(2 * SimConfig.TickRate); // логово возрождает пирата
        Kill(a, pirate);
        Assert.Equal(1, Missions(a).Active!.Progress);

        var credits = Credits(a);
        Steps(2 * SimConfig.TickRate);
        Kill(a, pirate);
        Assert.Null(Missions(a).Active);
        Assert.Equal(Protocol.MissionDone, Missions(a).Done?.Kind);
        Assert.Equal(credits + reward, Credits(a));
    }

    [Fact]
    public void Abandon_DropsTheMission()
    {
        var a = Veteran();
        Dock(a);
        Accept(a, MissionRules.KillKind);
        Do(a, r => r.Dock(a, false));
        Do(a, r => r.Mission(a, Protocol.AbandonMission, null));
        Assert.Null(Missions(a).Active);
        Assert.Null(_accounts.Profile(AccountId())!.Mission);
    }

    [Fact]
    public void Collect_TakesTheItemsAtTheStation()
    {
        _galaxy = New(CollectOnly);
        var a = Veteran();
        Dock(a);
        Accept(a, MissionRules.CollectKind);
        var reward = Missions(a).Active!.Offer.Reward;
        Assert.Equal(60, reward); // 10 кр × 3 × 2

        Do(a, r => r.Mission(a, Protocol.CompleteMission, null)); // трюм пуст — сдавать нечего
        Assert.NotNull(Missions(a).Active);

        // Купленное (здесь — просто положенное в трюм) не в счёт: «собрать» — это добыть в космосе.
        PlayerOf(a).Cargo.Add("metal", 2);
        Do(a, r => r.Mission(a, Protocol.CompleteMission, null));
        Assert.NotNull(Missions(a).Active);
        Assert.Equal(Protocol.NotGatheredNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal(0, Missions(a).Active!.Progress);

        Gather(a, "metal", 3);
        Assert.Equal(3, Missions(a).Active!.Progress);
        Dock(a);
        var credits = PlayerOf(a).Credits;
        Do(a, r => r.Mission(a, Protocol.CompleteMission, null));
        Assert.Null(Missions(a).Active);
        Assert.Equal(credits + reward, Credits(a));
        Assert.Equal(2, a.Last<CargoMsg>().Items["metal"]);
    }

    [Fact]
    public void Collect_DoesNotCountWhatWasJettisonedAndPickedUpAgain()
    {
        _galaxy = New(CollectOnly);
        var a = Veteran();
        Dock(a);
        Accept(a, MissionRules.CollectKind);
        PlayerOf(a).Cargo.Add("metal", 3);
        Do(a, r => r.Dock(a, false));
        Place(a, 800, 800);
        Do(a, r => r.Jettison(a, "metal"));
        Steps(1);
        GrabAll(a, "metal");
        Assert.Equal(3, PlayerOf(a).Cargo.Count("metal"));
        Assert.Equal(0, Missions(a).Active!.Progress);
        Assert.Equal(0, Missions(a).Active!.Gathered);

        // Добытое засчитывается, но не больше, чем лежит в трюме: продал — счёт упал.
        Gather(a, "metal", 2);
        Assert.Equal(2, Missions(a).Active!.Progress);
        Dock(a);
        Do(a, r => r.Sell(a, "metal", 4));
        Assert.Equal(1, Missions(a).Active!.Progress);
    }

    [Fact]
    public void Deliver_ReservesTheHold_SurvivesDeathAndRelogin_AndPaysAtTheOtherStation()
    {
        _galaxy = New(DeliverOnly);
        var a = Veteran();
        Dock(a);
        Accept(a, MissionRules.DeliverKind);
        var offer = Missions(a).Active!.Offer;
        Assert.Equal(("port", 5, 5 * 10 + 2 * 100), (offer.System, offer.Count, offer.Reward));
        Assert.Equal((5, 5.0), (a.Last<CargoMsg>().Reserved, a.Last<CargoMsg>().Used));

        PlayerOf(a).Cargo.Add("metal", 2);
        Do(a, r => r.Sell(a, null)); // «Продать всё» груз доставки не трогает
        Assert.Equal(5, a.Last<CargoMsg>().Reserved);

        Do(a, r => r.Dock(a, false));
        PlayerOf(a).Hp = 0;
        Steps(2 * SimConfig.TickRate);
        Assert.Equal(offer, Missions(a).Active?.Offer);

        _galaxy.Disconnect(a);
        Steps(Room.ReconnectGraceTicks + 1);
        a = Pilot();
        Assert.Equal(offer, Missions(a).Active?.Offer);
        Assert.Equal(5, a.Last<CargoMsg>().Reserved);

        JumpTo(a, "wild");
        JumpTo(a, "port");
        var credits = Credits(a);
        Dock(a);
        Assert.Null(Missions(a).Active);
        Assert.Equal((credits + offer.Reward, 0), (Credits(a), a.Last<CargoMsg>().Reserved));
    }

    [Fact]
    public void Hunt_CountsShotMeteors_AndPaysOnTheLast()
    {
        _galaxy = New(HuntOnly);
        var a = Veteran();
        Dock(a);
        Accept(a, MissionRules.HuntKind);
        var offer = Missions(a).Active!.Offer;
        Assert.Equal(("home", 2, 2 * 50), (offer.System, offer.Count, offer.Reward));
        Do(a, r => r.Dock(a, false));
        var credits = Credits(a);

        Shoot(a, "small");
        Assert.Equal(1, Missions(a).Active?.Progress);
        Shoot(a, "small");
        Assert.Null(Missions(a).Active);
        Assert.Equal(credits + offer.Reward, Credits(a));
    }

    /// <summary>
    /// Таран засчитывается наравне с выстрелом (M16a): камень, разбившийся о корабль пилота, уничтожен им.
    /// Размер и система по-прежнему проверяются, а камень, разбившийся не о него, ему и не идёт.
    /// </summary>
    [Fact]
    public void Hunt_CountsRammedRocks_ButNotTheWrongSizeOrSystem()
    {
        _galaxy = New(HuntLargeOnly);
        var a = Veteran();
        Dock(a);
        Accept(a, MissionRules.HuntKind);
        Assert.Equal("large", Missions(a).Active?.Offer.Size);
        Do(a, r => r.Dock(a, false));
        var me = IdOf(a);

        Shoot(a, "small"); // не тот размер
        Ram(a, "small", me); // тоже не тот размер
        Ram(a, "large"); // разбился о чужой борт — засчитывать некому
        Assert.Equal(0, Missions(a).Active?.Progress);

        Ram(a, "large", me); // а вот этот — о его собственный
        Assert.Equal(1, Missions(a).Active?.Progress);

        JumpTo(a, "wild"); // чужая система
        Shoot(a, "large");
        Assert.Equal(1, Missions(a).Active?.Progress);

        JumpTo(a, "home");
        Shoot(a, "large"); // второй из двух — задание закрыто
        Assert.Null(Missions(a).Active);
    }

    [Fact]
    public void Courier_TakesNoHold_AndPaysTheReceiver()
    {
        _galaxy = New(CourierOnly, TestBalance.Reputation());
        var a = Veteran();
        Dock(a);
        PlayerOf(a).Cargo.Add("metal", 20); // трюм под завязку — письму это не помеха
        Accept(a, MissionRules.CourierKind);
        var active = Missions(a).Active!;
        Assert.Equal(("port", 200 + 2 * 100), (active.Offer.System, active.Offer.Reward));
        Assert.Equal(60 + 2 * 60, active.Offer.Seconds);
        Assert.True(active.Until > 0);
        Assert.Equal(0, a.Last<CargoMsg>().Reserved);

        Do(a, r => r.Dock(a, false));
        JumpTo(a, "wild");
        JumpTo(a, "port");
        var credits = Credits(a);
        Dock(a);
        Assert.Null(Missions(a).Active);
        Assert.Equal(credits + active.Offer.Reward, Credits(a));
        // Спасибо говорит получатель, а не тот, кто письмо вручил.
        var rep = a.Last<RepMsg>();
        Assert.Equal(8, rep.Places[Reputation.Station("port")]);
        Assert.False(rep.Places.ContainsKey(Reputation.Station("home")));
    }

    [Fact]
    public void Courier_FailsWhenTimeRunsOut()
    {
        _galaxy = New(CourierOnly, TestBalance.Reputation());
        var a = Veteran();
        Dock(a);
        Accept(a, MissionRules.CourierKind);
        Do(a, r => r.Dock(a, false));
        var credits = Credits(a);

        // Срок идёт по стенным часам — тест двигает не время, а отметку.
        PlayerOf(a).Missions.Active = Missions(a).Active! with { Until = 1 };
        Steps(SimConfig.TickRate);

        var done = Missions(a).Done!;
        Assert.Equal((Protocol.MissionFailed, Protocol.TimeFail, 0), (done.Kind, done.Reason, done.Reward));
        Assert.Null(Missions(a).Active);
        Assert.Equal(credits, Credits(a));
        // Спрос с того, кто ждал письмо.
        Assert.Equal(-8, a.Last<RepMsg>().Places[Reputation.Station("port")]);
    }

    [Fact]
    public void Courier_LosesTheLetterWithTheShip_ButDeliveryOutlivesIt()
    {
        _galaxy = New(CourierOnly);
        var a = Veteran();
        Dock(a);
        Accept(a, MissionRules.CourierKind);
        Do(a, r => r.Dock(a, false));
        PlayerOf(a).Hp = 0;
        Steps(2 * SimConfig.TickRate);
        Assert.Null(Missions(a).Active);
        Assert.Equal(Protocol.DeadFail, Missions(a).Done?.Reason);
    }

    [Fact]
    public void Escort_StartsOnUndock_AndMarksTheConvoy()
    {
        _galaxy = New(EscortOnly);
        var a = Veteran();
        Dock(a);
        Accept(a, MissionRules.EscortKind);
        var offer = Missions(a).Active!.Offer;
        Assert.Equal(("wild", 1, 300 + 200), (offer.System, offer.Count, offer.Reward));
        Assert.Null(Convoy(a)); // в доке конвоя ещё нет: он выходит вместе с пилотом

        Do(a, r => r.Dock(a, false));
        var convoy = Convoy(a)!;
        Assert.Equal(convoy.Id, Missions(a).Mark?.Ship);
        // Конвой задания не входит в норму торговцев системы: обычный трафик остаётся.
        Assert.Contains(RoomOf(a).Traders, t => t.MissionId == 0);
    }

    [Fact]
    public void Escort_PaysWhenTheConvoyJumpsOut()
    {
        _galaxy = New(EscortOnly);
        var a = Veteran();
        Launch(a, MissionRules.EscortKind);
        var convoy = Convoy(a)!;
        var credits = Credits(a);

        // Конвой у самых врат — там его и встречает засада. Пока по нему стреляют, прыжок не собрать.
        convoy.Ship = new ShipState { X = convoy.DestX, Y = convoy.DestY };
        Place(a, convoy.DestX, convoy.DestY);
        Steps(2 * JumpTicks);
        Assert.NotNull(Missions(a).Active);

        foreach (var raider in Ambush(a)) raider.Hp = 0;
        Steps(2 * JumpTicks + 4);

        Assert.Null(Missions(a).Active);
        Assert.Equal(Protocol.MissionDone, Missions(a).Done?.Kind);
        Assert.Equal(credits + 500, Credits(a));
    }

    [Fact]
    public void Escort_FailsWhenTheConvoyDies()
    {
        _galaxy = New(EscortOnly, TestBalance.Reputation());
        var a = Veteran();
        Launch(a, MissionRules.EscortKind);
        var credits = Credits(a);

        Convoy(a)!.Hp = 0;
        Steps(2);

        Assert.Equal(Protocol.TraderFail, Missions(a).Done?.Reason);
        Assert.Null(Missions(a).Active);
        Assert.Equal(credits, Credits(a));
        Assert.Equal(-8, a.Last<RepMsg>().Places[Reputation.Station("home")]);
    }

    [Fact]
    public void Escort_FailsWhenThePilotFallsBehind()
    {
        _galaxy = New(EscortOnly);
        var a = Veteran();
        Launch(a, MissionRules.EscortKind);

        Place(a, 20000, 20000);
        Steps(2);
        Assert.Equal(Protocol.MissionAwayNotice, a.Last<NoticeMsg>().Code);
        Assert.NotNull(Missions(a).Active); // окно терпения ещё не вышло

        Steps(2 * SimConfig.TickRate + 2);
        Assert.Equal(Protocol.AwayFail, Missions(a).Done?.Reason);
    }

    [Fact]
    public void Escort_FailsWhenThePilotDocksOrJumpsAway()
    {
        _galaxy = New(EscortOnly);
        var a = Veteran();
        Launch(a, MissionRules.EscortKind);
        Dock(a);
        Assert.Equal(Protocol.AwayFail, Missions(a).Done?.Reason);
        Assert.Null(Convoy(a)); // конвой отпущен: он стал обычным торговцем и долетит сам

        Do(a, r => r.Dock(a, false));
        Launch(a, MissionRules.EscortKind);
        JumpTo(a, "wild");
        Assert.Null(Missions(a).Active);
    }

    [Fact]
    public void Escort_SendsAnAmbushOnTheWay()
    {
        _galaxy = New(EscortOnly);
        var a = Veteran();
        Launch(a, MissionRules.EscortKind);
        var convoy = Convoy(a)!;
        var pirates = RoomOf(a).Pirates.Count;

        // Полпути позади — по расписанию это и есть единственная засада.
        convoy.Ship = new ShipState { X = convoy.DestX * 0.6, Y = convoy.DestY * 0.6 };
        Place(a, convoy.Ship.X, convoy.Ship.Y);
        Steps(2);

        Assert.Equal(1, Missions(a).Active?.Progress);
        Assert.True(RoomOf(a).Pirates.Count > pirates);
        Assert.Equal(Protocol.AmbushNotice, a.Last<NoticeMsg>().Code);
    }

    [Fact]
    public void LiveMission_IsNotRestoredOnLogin()
    {
        _galaxy = New(EscortOnly);
        var a = Veteran();
        Launch(a, MissionRules.EscortKind);
        _galaxy.Disconnect(a);
        Steps(Room.ReconnectGraceTicks + 1);

        a = Pilot();
        Assert.Null(Missions(a).Active);
    }

    [Fact]
    public void Patrol_WaitsAtTheWaypointForThePilot()
    {
        _galaxy = New(PatrolOnly);
        var a = Veteran();
        Launch(a, MissionRules.PatrolKind);
        Assert.Equal(2, Wing(a).Count);
        var mark = Missions(a).Mark!;
        Assert.Equal(0, mark.Ship);

        // Пилот остался у станции: звено долетает до точки и стоит там.
        Place(a, 0, 50);
        Steps(30 * SimConfig.TickRate);
        Assert.Equal(0, Missions(a).Active?.Progress);

        GoToMark(a);
        Steps(2);
        Assert.Equal(1, Missions(a).Active?.Progress);
        Assert.NotEqual((mark.X, mark.Y), (Missions(a).Mark!.X, Missions(a).Mark!.Y));
    }

    [Fact]
    public void Patrol_PaysAfterTheLastWaypoint()
    {
        _galaxy = New(PatrolOnly);
        var a = Veteran();
        Launch(a, MissionRules.PatrolKind);
        var credits = Credits(a);

        for (var point = 0; point < 2; point++) ReachWaypoint(a);

        Assert.Null(Missions(a).Active);
        Assert.Equal(credits + 300 + 2 * 160, Credits(a));
        Assert.Empty(Wing(a)); // звено больше не наше — уходит из системы
    }

    [Fact]
    public void Patrol_CannotBeWalkedThrough_TheAmbushHasToBeCleared()
    {
        _galaxy = New(PatrolFight);
        var a = Veteran();
        Launch(a, MissionRules.PatrolKind);
        var credits = Credits(a);

        // Первая точка проходится с ходу: засада ждёт дальше по маршруту.
        ReachWaypoint(a);
        Assert.Equal(1, Missions(a).Active?.Progress);
        Assert.Empty(Ambush(a));

        // Вторая — с пиратами, и она не засчитывается, пока они живы.
        ReachWaypoint(a);
        var foes = Ambush(a);
        Assert.Equal(2, foes.Count);
        Assert.Equal(Protocol.AmbushNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal(1, Missions(a).Active?.Progress);

        // Стоять на точке и ждать бесполезно — и сбежать тоже: бой надо кончить.
        Steps(3 * SimConfig.TickRate);
        Assert.Equal(1, Missions(a).Active?.Progress);

        foreach (var foe in foes) foe.Hp = 0;
        Steps(2);
        Assert.Null(Missions(a).Active);
        Assert.Equal(credits + 300 + 2 * 160, Credits(a));
    }

    [Fact]
    public void Patrol_FailsWhenTheWingIsWipedOut()
    {
        _galaxy = New(PatrolOnly);
        var a = Veteran();
        Launch(a, MissionRules.PatrolKind);

        foreach (var ranger in Wing(a)) ranger.Hp = 0;
        Steps(2);

        Assert.Equal(Protocol.WingFail, Missions(a).Done?.Reason);
        Assert.Null(Missions(a).Active);
    }

    [Fact]
    public void Patrol_WingDoesNotHealAtTheWaypoint()
    {
        _galaxy = New(PatrolOnly);
        var a = Veteran();
        Launch(a, MissionRules.PatrolKind);

        var ranger = Wing(a)[0];
        var mark = Missions(a).Mark!;
        ranger.Hp = 100;
        ranger.Ship = new ShipState { X = mark.X, Y = mark.Y };
        Steps(SimConfig.TickRate);

        // Обычный налётчик починился бы дома до полного — звену этого нельзя, иначе его не выбить.
        Assert.Equal(100, ranger.Hp);
    }

    [Fact]
    public void Deliver_NeedsRoomInTheHold()
    {
        _galaxy = New(DeliverOnly);
        var a = Veteran();
        Dock(a);
        PlayerOf(a).Cargo.Add("metal", 18); // лёгкий трюм — 20
        var offer = Missions(a).Offers[0];
        Do(a, r => r.Mission(a, Protocol.AcceptMission, offer.Id));
        Assert.Equal(Protocol.CargoFullNotice, a.Last<NoticeMsg>().Code);
        Assert.Null(Missions(a).Active);
    }

    /// <summary>Доска, которая обновляется каждую секунду: проверить смену, не ожидая двенадцати минут.</summary>
    private static readonly MissionRules Restless = new(
        Offers: 4,
        DangerBonus: 0,
        RefreshMinutes: 1.0 / 60,
        Tutorial: Tutorial,
        Kill: [new KillTemplate(null, 2, 2, 100)],
        Collect: [new CollectTemplate("metal", 3, 3, 2)],
        Deliver: [new DeliverTemplate(5, 5, 10, 100)]);

    [Fact]
    public void TheBoardRefreshesItself_ForWhoeverIsLookingAtIt()
    {
        _galaxy = New(Restless);
        var a = Pilot();
        Do(a, r => r.Mission(a, Protocol.SkipTutorial, null));
        Dock(a);
        var before = Missions(a).Offers.Select(o => o.Id).ToList();
        Assert.NotEmpty(before);

        // Ждём смену оборота: доска приходит сама, пилот для этого ничего не делает.
        Steps(SimConfig.TickRate * 2);

        var after = Missions(a).Offers.Select(o => o.Id).ToList();
        Assert.NotEqual(before, after);
        // Имя предложения живёт внутри своего оборота: иначе взяли бы не то, что видели.
        Assert.Empty(before.Intersect(after));
    }

    [Fact]
    public void ARefreshedBoard_DoesNotHandOutTheMissionYouNoLongerSee()
    {
        _galaxy = New(Restless);
        var a = Pilot();
        Do(a, r => r.Mission(a, Protocol.SkipTutorial, null));
        Dock(a);
        var stale = Missions(a).Offers[0].Id;

        Steps(SimConfig.TickRate * 2);
        Do(a, r => r.Mission(a, Protocol.AcceptMission, stale));

        Assert.Null(Missions(a).Active);
        Assert.NotEmpty(Missions(a).Offers);
    }

    [Fact]
    public void AnIdleBoardStandsStill()
    {
        // Без refreshMinutes доска меняется только от того, что делает пилот, — как было до M15.1.
        _galaxy = New(KillOnly);
        var a = Pilot();
        Do(a, r => r.Mission(a, Protocol.SkipTutorial, null));
        Dock(a);
        var before = Missions(a).Offers.Select(o => o.Id).ToList();

        Steps(SimConfig.TickRate * 2);

        Assert.Equal(before, Missions(a).Offers.Select(o => o.Id));
    }
}
