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
        FuelPerDistance: 1,
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new("Home", Gates: [new GateDef("wild", 3000, 0)],
                Drones: [new DroneSpec("Учебный дрон", "light", 0, -600, Table: "drone")]),
            ["wild"] = new("Wild", Danger: 3, Pvp: GalaxyRules.PvpFree, Station: false,
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
        });

    private static readonly LootRules Loot = new(
        FadeSeconds: 0,
        Items: new Dictionary<string, LootItem> { ["metal"] = new("Металл", Volume: 1, Price: 10) },
        Tables: new Dictionary<string, LootTable> { ["drone"] = new([new LootRoll("metal", 1, 2, 2)]) });

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

    private Galaxy New(MissionRules missions) =>
        new(TestBalance.Create(new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), Npcs, Loot,
                shop: new ShopRules(StartCredits: 1000)) with
            {
                GalaxySet = Rules,
                MissionSet = missions,
            }, NullLogger.Instance, _accounts, roll: () => 0, random: seed => new Random(seed));

    private FakeConnection Pilot(string name = "Alice")
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

    [Fact]
    public void NewPilot_StartsDocked_AndWalksTheTutorial()
    {
        var a = Pilot();
        Assert.True(a.Last<HangarMsg>().Docked);
        Assert.Equal(new TutorialDto(0, 5, MissionRules.UndockStep, "Вылетите", ""), Missions(a).Tutorial);

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
        Assert.Equal(MissionLog.Finished, _accounts.Profile(AccountId())!.Tutorial);
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
        var a = Pilot();
        Do(a, r => r.Mission(a, Protocol.SkipTutorial, null));
        Assert.Null(Missions(a).Tutorial);
        Assert.Equal(1000, Credits(a));
        Assert.Equal(MissionLog.Finished, _accounts.Profile(AccountId())!.Tutorial);
    }

    [Fact]
    public void ProfileOlderThanM8_HasNoTutorial()
    {
        _accounts.Save(AccountId(), new AccountProfile(500, "light", "pulse", ["light"], ["pulse"], new Dictionary<string, int>()));
        var a = Pilot();
        Assert.False(a.Last<HangarMsg>().Docked);
        Assert.Null(Missions(a).Tutorial);
    }

    [Fact]
    public void Guest_HasNoTutorial_ButSeesTheBoard()
    {
        var a = Guest();
        Assert.False(a.Last<HangarMsg>().Docked);
        Assert.Null(Missions(a).Tutorial);
        Assert.Equal(4, Missions(a).Offers.Count);
    }

    [Fact]
    public void Board_IsTheSameUntilAMissionIsTaken_AndEmptyWithoutAStation()
    {
        var a = Veteran();
        var board = Missions(a).Offers;
        Assert.All(board, o => Assert.Equal(("wild", "home"), (o.System, o.From))); // пираты есть только в wild
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

        PlayerOf(a).Cargo.Add("metal", 5);
        var credits = PlayerOf(a).Credits;
        Do(a, r => r.Mission(a, Protocol.CompleteMission, null));
        Assert.Null(Missions(a).Active);
        Assert.Equal(credits + reward, Credits(a));
        Assert.Equal(2, a.Last<CargoMsg>().Items["metal"]);
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
}
