using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Вторая половина кампании (M20b). Проверяется опять не текст «Тихой войны», а то, чему её ради
/// научился движок: за работу можно заплатить вперёд и со скидкой своим, на предложение можно ответить
/// «не сейчас», вызванный корабль умеет убегать к вратам и ронять груз своему пилоту, охрана стоит там,
/// куда её поставили, и знает, за кем пришла, ящики лежат в той системе, которую назвала миссия,
/// а корабль в награду достаётся только тому, кто тогда поступил определённым образом.
/// </summary>
public sealed class M20bStoryTests : IDisposable
{
    private const string Password = "secret";
    private const string Campaign = "deal";

    private static readonly GalaxyRules Galaxy = new(
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new("Home", Gates: [new GateDef("port", 3000, 0)]),
            ["port"] = new("Port", Gates: [new GateDef("home", -3000, 0)]),
        },
        Links: [new LinkDef("home", "port", 10)]);

    private static readonly NpcRules Npcs = new(
        RespawnSeconds: 1,
        Types: new Dictionary<string, NpcType>
        {
            ["pirate"] = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, Bounty: 60),
            // Корабли корпорации: дерутся по-пиратски, а выглядят по-своему — на своих корпусах.
            ["courier"] = new("Курьер", "light", "pulse", Hp: 200, Shield: 0, Damage: 0.1, Bounty: 0, Look: NpcType.CorpLook),
            ["guard"] = new("Охрана", "heavy", "pulse", Hp: 400, Shield: 100, Damage: 0.3, Bounty: 0, Look: NpcType.CorpLook),
        });

    private static readonly LootRules Loot = new(
        FadeSeconds: 0,
        Items: new Dictionary<string, LootItem>
        {
            ["metal"] = new("Металл", Volume: 1, Price: 10),
            ["part"] = new("Деталь", Volume: 1, Price: 0, Story: true),
            ["plan"] = new("Чертёж", Volume: 1, Price: 0, Story: true),
            ["cores"] = new("Приводы", Volume: 1, Price: 0, Story: true),
        });

    private static readonly MissionRules Missions = new(Offers: 2, Collect: [new CollectTemplate("metal", 2, 2)]);

    private static readonly StoryRules Story = new(new Dictionary<string, StoryCampaign>
    {
        [Campaign] = new(
            "Сделка",
            Missions:
            [
                new(
                    "pay", "Покупка", "st:home", "Гор", "литейщик",
                    Objective: "Довезите деталь в Порт",
                    Kind: MissionRules.DeliverKind,
                    Dest: "st:port",
                    Count: 1,
                    Reward: 100,
                    Give: ["part"],
                    Cost: 500,
                    CostRep: "friend",
                    CostCut: 200),
                new(
                    "ask", "Просьба", "st:home", "Ева", "инженер",
                    Objective: "Привезите металл",
                    Kind: MissionRules.CollectKind,
                    Item: "metal",
                    Count: 1,
                    Dest: "st:home",
                    Reward: 50,
                    Cost: 300,
                    Choice: new StoryChoice(
                        "Вы с нами?",
                        [
                            new StoryOption("Я с вами", "yes", ["Тогда за дело."]),
                            new StoryOption("Не сейчас", "no", ["Я подожду."], Decline: true),
                        ],
                        Trigger: StoryRules.OnAccept)),
                new(
                    "hunt", "Курьер", "st:home", "Ева", "инженер",
                    Objective: "Перехватите курьера",
                    Kind: MissionRules.CollectKind,
                    Item: "plan",
                    Count: 1,
                    System: "home",
                    Dest: "st:home",
                    Reward: 100,
                    Point: new StoryPoint(900, 0),
                    Spawns:
                    [
                        new(
                            StoryRules.OnUndock, "courier", 1, 1, "Курьер корпорации",
                            At: new StoryPoint(900, 0), Gate: "port", Drop: "plan", Flee: true),
                    ]),
                new(
                    "far", "Далеко", "st:home", "Ева", "инженер",
                    Objective: "Заберите приводы в Порту",
                    Kind: MissionRules.CollectKind,
                    Item: "cores",
                    Count: 1,
                    System: "port",
                    Dest: "st:home",
                    Reward: 100,
                    Point: new StoryPoint(-800, 400),
                    Spawns:
                    [
                        new(StoryRules.OnArrive, "guard", 1, 1, "Пост", At: new StoryPoint(-800, 400), Hold: true),
                    ]),
                new(
                    "gift", "Награда", "st:home", "Ева", "инженер",
                    Objective: "Привезите металл",
                    Kind: MissionRules.CollectKind,
                    Item: "metal",
                    Count: 1,
                    Dest: "st:home",
                    Reward: 10,
                    RewardHull: "heavy",
                    RewardIf: "yes",
                    Lines: new StoryLines(Done: ["Спасибо."]),
                    Alt: new StoryAlt("yes", new StoryLines(Done: ["Забирайте «Тяжёлый»."]), DoneBy: "Дан", DoneRole: "сварщик")),
            ]),
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-story-b-" + Guid.NewGuid().ToString("N"));
    private readonly Accounts.AccountStore _accounts;
    private readonly Galaxy _galaxy;
    private int _nextConnection;

    public M20bStoryTests()
    {
        _accounts = new Accounts.AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _galaxy = new Galaxy(
            TestBalance.Create(
                new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), Npcs, Loot,
                shop: new ShopRules(StartCredits: 1000), reputation: TestBalance.Reputation(decayPerDay: 0),
                missions: Missions) with
            {
                GalaxySet = Galaxy,
                StorySet = Story,
            }, NullLogger.Instance, _accounts, roll: () => 0, random: seed => new Random(seed));
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ------------------------------------------------------------------ вспомогательное

    private FakeConnection Pilot(string name = "Alice")
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.JoinAccount(connection, login.Id, login.Name);
        Do(connection, r => r.Mission(connection, Protocol.SkipTutorial, null));
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Room RoomOf(FakeConnection connection) => _galaxy.RoomOf(connection)!;

    private Player PlayerOf(FakeConnection connection) => RoomOf(connection).Pilot(IdOf(connection))!;

    private static MissionsMsg Missions_(FakeConnection connection) => connection.Last<MissionsMsg>();

    private static StoryStateDto? State(FakeConnection connection) => Missions_(connection).Story;

    private void Do(FakeConnection connection, Action<Room> command) => _galaxy.With(connection, command);

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private void Place(FakeConnection connection, double x, double y) =>
        PlayerOf(connection).Ship = new ShipState { X = x, Y = y };

    private void Undock(FakeConnection connection) => Do(connection, r => r.Dock(connection, false));

    private void Dock(FakeConnection connection)
    {
        Place(connection, 0, 50);
        Do(connection, r => r.Dock(connection, true));
        Assert.True(connection.Last<HangarMsg>().Docked);
    }

    private void JumpTo(FakeConnection connection, string to)
    {
        var gate = RoomOf(connection).Balance.SystemDef.GateTo(to)!;
        Place(connection, gate.X, gate.Y);
        Do(connection, r => r.Jump(connection, to));
        Steps(SimConfig.TickRate);
        Assert.Equal(to, RoomOf(connection).SystemId);
    }

    /// <summary>Пройденное «на бумаге»: цепочка линейная, и ради одной миссии её незачем играть целиком.</summary>
    private void Passed(FakeConnection connection, params string[] missions)
    {
        var log = PlayerOf(connection).StoryOf(Campaign);
        foreach (var id in missions) log.Done.Add(id);
        Undock(connection);
        Dock(connection);
    }

    private void TakeStory(FakeConnection connection)
    {
        var offer = State(connection)?.Offer;
        Assert.NotNull(offer);
        Do(connection, r => r.Mission(connection, Protocol.AcceptMission, offer!.Id));
    }

    private static int Credits(FakeConnection connection) => connection.Last<CargoMsg>().Credits;

    private void MakeFriendHere(FakeConnection connection) =>
        PlayerOf(connection).Rep.Add("st:home", 40, 0, RoomOf(connection).Balance.Reputation);

    // ------------------------------------------------------------------ плата вперёд

    [Fact]
    public void PayingUpFrontTakesTheCreditsAndFriendsPayLess()
    {
        var a = Pilot();
        var before = Credits(a);
        TakeStory(a);
        Assert.NotNull(Missions_(a).Active?.Offer.Story);
        Assert.Equal(before - 500, Credits(a));
        // Деталь при этом выдана: за деньги пилот получает груз, а не обещание.
        Assert.Equal(1, PlayerOf(a).Cargo.Count("part"));

        var b = Pilot("Bob");
        MakeFriendHere(b);
        var his = Credits(b);
        TakeStory(b);
        Assert.Equal(his - 200, Credits(b));
    }

    [Fact]
    public void WithoutTheMoneyTheWorkIsNotTaken()
    {
        var a = Pilot();
        PlayerOf(a).Credits = 100;
        TakeStory(a);
        Assert.Null(Missions_(a).Active);
        Assert.Equal(Protocol.NoCreditsNotice, a.Last<NoticeMsg>().Code);
        // И груза не выдали: не оплачено — не отгружено.
        Assert.Equal(0, PlayerOf(a).Cargo.Count("part"));
    }

    // ------------------------------------------------------------------ «не сейчас»

    [Fact]
    public void SayingNoGivesTheMoneyBackAndLeavesTheCampaignWhereItWas()
    {
        var a = Pilot();
        Passed(a, "pay");
        var before = Credits(a);
        TakeStory(a);

        // Вопрос задан сразу при взятии, а не по подбору груза.
        var dialog = a.Last<DialogMsg>();
        Assert.Equal("Вы с нами?", dialog.Lines[0]);
        Assert.Equal(["yes", "no"], dialog.Options!.Select(o => o.Flag));
        Assert.Equal(before - 300, Credits(a));

        Do(a, r => r.Mission(a, Protocol.ChooseStory, "no"));
        Assert.Null(Missions_(a).Active);
        // Работа вернулась на доску той же самой, а деньги — пилоту.
        Assert.Equal("ask", State(a)!.Offer!.Story!.Mission);
        Assert.Equal(before, Credits(a));
        // Отказ помнится: Ева потом скажет своё именно поэтому.
        Assert.Contains("no", PlayerOf(a).StoryOf(Campaign).Flags);
    }

    [Fact]
    public void SayingYesLeavesTheWorkInHand()
    {
        var a = Pilot();
        Passed(a, "pay");
        TakeStory(a);
        Do(a, r => r.Mission(a, Protocol.ChooseStory, "yes"));
        Assert.Equal("ask", Missions_(a).Active?.Offer.Story?.Mission);
        Assert.Contains("yes", PlayerOf(a).StoryOf(Campaign).Flags);
    }

    // ------------------------------------------------------------------ курьер

    [Fact]
    public void TheCourierRunsForTheGateAndDropsItsCargoForItsOwner()
    {
        var a = Pilot();
        Passed(a, "pay", "ask");
        TakeStory(a);
        Undock(a);

        var courier = Assert.Single(RoomOf(a).Pirates.Where(p => p.Story));
        Assert.Equal("Курьер корпорации", courier.Name);
        // Вышел там, где написано (место в звене — на круге вокруг точки), и сразу идёт на выход.
        Assert.InRange(courier.Ship.X, 800, 1000);
        Assert.Equal(PirateState.Leave, courier.State);
        Assert.True(courier.ExitIsGate);
        Assert.Equal(3000, courier.ExitX);
        // На экране это корабль корпорации, а не буксир повстанца.
        Assert.Equal(Protocol.CorpKind, a.Last<PlayersMsg>().Players.First(p => p.Id == courier.Id).Kind);
        // Чертёж не валяется на точке: его надо взять с корабля.
        Assert.Empty(RoomOf(a).Drops.Where(d => d.Item == "plan"));

        PlayerOf(a).WeaponId = "doom";
        Place(a, courier.Ship.X, courier.Ship.Y + 200);
        Do(a, r => r.SetTarget(a, courier.Id));
        Do(a, r => r.SetFire(a, true));
        Steps(2);
        Do(a, r => r.SetFire(a, false));
        Assert.True(courier.IsDead);

        var drop = Assert.Single(RoomOf(a).Drops.Where(d => d.Item == "plan"));
        Assert.Equal(IdOf(a), drop.Owner);
        // Ждёт хозяина и не протухает: цепочка не должна рваться из-за двух минут.
        Assert.Equal(long.MaxValue, drop.ExpiresAtTick);
    }

    // ------------------------------------------------------------------ охрана и чужая система

    [Fact]
    public void TheBoxesAndTheGuardWaitInTheSystemTheMissionNames()
    {
        var a = Pilot();
        Passed(a, "pay", "ask", "hunt");
        TakeStory(a);
        Undock(a);
        // Дома — ни ящиков, ни поста: миссия назвала Порт.
        Assert.Empty(RoomOf(a).Drops.Where(d => d.Owner == IdOf(a)));
        Assert.Empty(RoomOf(a).Pirates.Where(p => p.Story));

        JumpTo(a, "port");
        var drop = Assert.Single(RoomOf(a).Drops.Where(d => d.Owner == IdOf(a)));
        Assert.Equal("cores", drop.Item);
        var guard = Assert.Single(RoomOf(a).Pirates.Where(p => p.Story));
        Assert.Equal("Пост", guard.Name);
        Assert.InRange(guard.Ship.X, -900, -700);
        // Стоит на посту и ждёт своего: посторонних в комнате он не касается.
        Assert.True(guard.HoldsGround);
        Assert.Equal(IdOf(a), guard.OwnerId);
    }

    [Fact]
    public void TheGuardComesOutOnceHoweverOftenThePilotDocks()
    {
        var a = Pilot();
        Passed(a, "pay", "ask", "hunt");
        TakeStory(a);
        Undock(a);
        JumpTo(a, "port");
        Assert.Single(RoomOf(a).Pirates.Where(p => p.Story));
        Dock(a);
        Undock(a);
        Assert.Single(RoomOf(a).Pirates.Where(p => p.Story));
    }

    // ------------------------------------------------------------------ награда и развилка

    [Fact]
    public void TheShipIsGivenOnlyToThoseWhoKeptTheirWord()
    {
        var a = Pilot();
        Passed(a, "pay", "ask", "hunt", "far");
        TakeStory(a);
        PlayerOf(a).Cargo.Add("metal", 1);
        Do(a, r => r.Mission(a, Protocol.CompleteMission, null));
        Assert.Null(Missions_(a).Active);
        // Флага «я с вами» у этого пилота нет: корабля тоже нет.
        Assert.DoesNotContain("heavy", PlayerOf(a).Hulls);
        Assert.Equal("Спасибо.", a.Last<DialogMsg>().Lines[^1]);
        Assert.Equal(("Ева", "инженер"), (a.Last<DialogMsg>().Who, a.Last<DialogMsg>().Role));

        var b = Pilot("Bob");
        PlayerOf(b).StoryOf(Campaign).Flags.Add("yes");
        Passed(b, "pay", "ask", "hunt", "far");
        TakeStory(b);
        PlayerOf(b).Cargo.Add("metal", 1);
        Do(b, r => r.Mission(b, Protocol.CompleteMission, null));
        Assert.Contains("heavy", PlayerOf(b).Hulls);
        // Корабль стоит там, где работу сдали, а пилот остался в своём.
        Assert.Equal("st:home", PlayerOf(b).HullPlaces["heavy"]);
        Assert.NotEqual("heavy", PlayerOf(b).HullId);
        // И реплика у него своя: выбор слышно, а не только видно в ангаре.
        Assert.Equal("Забирайте «Тяжёлый».", b.Last<DialogMsg>().Lines[^1]);
        // Корабль отдаёт его хозяин: в этом варианте на сдаче говорит он, а не та, кто выдала работу.
        Assert.Equal(("Дан", "сварщик"), (b.Last<DialogMsg>().Who, b.Last<DialogMsg>().Role));
    }

    [Fact]
    public void HandingInACollectTakesOnlyWhatWasAsked()
    {
        var a = Pilot();
        Passed(a, "pay", "ask", "hunt", "far");
        TakeStory(a);
        PlayerOf(a).Cargo.Add("metal", 5);
        Do(a, r => r.Mission(a, Protocol.CompleteMission, null));
        // Просили один: остальные четыре — груз пилота, а не заказчика.
        Assert.Equal(4, PlayerOf(a).Cargo.Count("metal"));
        Assert.Null(Missions_(a).Active);
    }

    // ------------------------------------------------------------------ заглушка

    [Fact]
    public void TheRelayShowsUpWhereTheCampaignEnded()
    {
        var a = Pilot();
        Assert.False(State(a)!.Relay);
        Passed(a, "pay", "ask", "hunt", "far", "gift");
        Assert.True(State(a)!.Relay);
        Assert.Null(State(a)!.Offer);

        // В другом доке кнопки нет: продолжение ждёт там, где кампания кончилась.
        Undock(a);
        JumpTo(a, "port");
        Dock(a);
        Assert.False(State(a)!.Relay);
    }
}
