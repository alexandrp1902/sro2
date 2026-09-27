using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Сюжет в своём слоте (плейтест 2026-09-26): сюжетная миссия берётся параллельно с работой с доски,
/// живое задание из них — одно, сдача одного не трогает другое, а следующая встреча по сюжету известна
/// всегда — где бы пилот ни стоял. Пролог ветераны кампании не видят.
/// </summary>
public sealed class StorySlotTests : IDisposable
{
    private const string Password = "secret";
    private const string Campaign = "test";

    /// <summary>home — станция и поселение Терра; port — станция за вратами.</summary>
    private static readonly GalaxyRules Galaxy = new(
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new(
                "Home",
                Gates: [new GateDef("port", 3000, 0)],
                Planets: [new PlanetDef("Терра", "terran", 150, new OrbitDef(Radius: 2000, PeriodMinutes: 600), "terra", new SettlementDef("Новый Порт"))],
                Traders: new TraderRules(Count: 0, RespawnSeconds: 60, Type: "trader", Throttle: 0.7)),
            // Камни только дома: иначе охота с доски уезжала бы в соседнюю систему.
            ["port"] = new("Port", Meteors: 0, Gates: [new GateDef("home", -3000, 0)]),
        },
        Links: [new LinkDef("home", "port", 10)]);

    private static readonly NpcRules Npcs = new(
        RespawnSeconds: 1,
        Types: new Dictionary<string, NpcType>
        {
            ["pirate"] = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320),
            ["trader"] = new("Торговец", "light", "pulse", Faction: NpcType.TraderFaction, Hp: 400, Shield: 100, Damage: 0.3),
        });

    private static readonly LootRules Loot = new(
        FadeSeconds: 0,
        Items: new Dictionary<string, LootItem>
        {
            ["metal"] = new("Металл", Volume: 1, Price: 10),
            ["papers"] = new("Бумаги", Volume: 1, Price: 0, Story: true),
            ["cores"] = new("Привод", Volume: 1, Price: 0, Story: true),
        },
        Tables: new Dictionary<string, LootTable> { ["rock"] = new([new LootRoll("metal", 1, 1, 1)]) });

    private static readonly MeteorRules Meteors = new(
        MaxAlive: 0,
        LifetimeSeconds: 90,
        Gravity: 0,
        Sizes: new Dictionary<string, MeteorSize> { ["small"] = new("Мелкий метеорит", 14, 60, 240, 300, 110, 1, "rock") });

    /// <summary>Пролог, живая миссия (конвой до Терры) и обычное «собрать».</summary>
    private static readonly StoryRules Story = new(new Dictionary<string, StoryCampaign>
    {
        [Campaign] = new(
            "Проверка",
            Missions:
            [
                new(
                    "intro", "Бумаги", "st:home", "Ильина", "найм",
                    Kind: MissionRules.DeliverKind, Dest: "st:port", Count: 1, Give: ["papers"], Reward: 50,
                    Prologue: true),
                new(
                    "guard", "Конвой", "st:home", "Холт", "охрана",
                    Kind: MissionRules.EscortKind, Dest: "pl:terra", Count: 1, Radius: 5000, Reward: 100,
                    Waves: [[new InvasionGroup("pirate", 1, 1)]]),
                new(
                    "fetch", "Приводы", "st:home", "Холт", "охрана",
                    Kind: MissionRules.CollectKind, Item: "cores", Count: 1, Dest: "st:home", Reward: 100,
                    Point: new StoryPoint(1000, 1000)),
            ]),
    });

    private static readonly IReadOnlyList<TutorialStep> Tutorial =
    [
        new(MissionRules.UndockStep, "Вылетите"),
        new(MissionRules.BoardStep, "Возьмите работу"),
    ];

    private static readonly MissionRules CollectBoard = new(Tutorial: Tutorial, Collect: [new CollectTemplate("metal", 2, 2)]);
    private static readonly MissionRules HuntBoard = new(DangerBonus: 0, Tutorial: Tutorial, Hunt: [new HuntTemplate(null, 1, 1, 50)]);

    private static readonly MissionRules EscortBoard = new(
        DangerBonus: 0,
        Tutorial: Tutorial,
        Escort: [new EscortTemplate(1, 1, 300, 200, Radius: 900, AwaySeconds: 30)],
        Ambush: [[new InvasionGroup("pirate", 1, 1)]]);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-slots-" + Guid.NewGuid().ToString("N"));
    private readonly Accounts.AccountStore _accounts;
    private Galaxy _galaxy;
    private int _nextConnection;

    public StorySlotTests()
    {
        _accounts = new Accounts.AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _galaxy = New(CollectBoard);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Galaxy New(MissionRules board) =>
        new(TestBalance.Create(
                new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), Npcs, Loot, Meteors,
                shop: new ShopRules(StartCredits: 1000), reputation: TestBalance.Reputation(decayPerDay: 0),
                missions: board) with
            {
                GalaxySet = Galaxy,
                StorySet = Story,
            }, NullLogger.Instance, _accounts, roll: () => 0, random: seed => new Random(seed));

    // ------------------------------------------------------------------ вспомогательное

    private FakeConnection Pilot(string name = "Alice", bool tutorial = false)
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.JoinAccount(connection, login.Id, login.Name);
        if (!tutorial) Do(connection, r => r.Mission(connection, Protocol.SkipTutorial, null));
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Room RoomOf(FakeConnection connection) => _galaxy.RoomOf(connection)!;

    private Player PlayerOf(FakeConnection connection) => RoomOf(connection).Pilot(IdOf(connection))!;

    private static MissionsMsg Missions(FakeConnection connection) => connection.Last<MissionsMsg>();

    private static StoryStateDto? State(FakeConnection connection) => Missions(connection).Story;

    private void Do(FakeConnection connection, Action<Room> command) => _galaxy.With(connection, command);

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private void Place(FakeConnection connection, double x, double y) =>
        PlayerOf(connection).Ship = new ShipState { X = x, Y = y };

    private void Undock(FakeConnection connection) => Do(connection, r => r.Dock(connection, false));

    /// <summary>Встать в док станции; стоящий в доке сначала вылетает — так клиент получает свежее состояние.</summary>
    private void Dock(FakeConnection connection)
    {
        if (PlayerOf(connection).Docked) Undock(connection);
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

    private void Take(FakeConnection connection, string id) =>
        Do(connection, r => r.Mission(connection, Protocol.AcceptMission, id));

    /// <summary>Отметить миссии кампании пройденными — так, будто пилот сделал их раньше.</summary>
    private void Passed(FakeConnection connection, params string[] ids)
    {
        foreach (var id in ids) PlayerOf(connection).StoryOf(Campaign).Done.Add(id);
    }

    // ------------------------------------------------------------------ слоты

    [Fact]
    public void StoryAndBoardAreHeldTogether()
    {
        var a = Pilot();
        Dock(a);
        var story = State(a)!.Offer!;
        var board = Missions(a).Offers.First(o => o.Story is null);

        Take(a, story.Id);
        Take(a, board.Id);

        Assert.Equal(story.Id, Missions(a).StoryActive?.Offer.Id);
        Assert.Equal(board.Id, Missions(a).Active?.Offer.Id);

        // Отказ от сюжета не трогает работу с доски — и наоборот.
        Do(a, r => r.Mission(a, Protocol.AbandonMission, story.Id));
        Assert.Null(Missions(a).StoryActive);
        Assert.Equal(board.Id, Missions(a).Active?.Offer.Id);
    }

    [Fact]
    public void TakingTheStoryDoesNotReshuffleTheBoard()
    {
        var a = Pilot();
        Dock(a);
        var before = Missions(a).Offers.Where(o => o.Story is null).Select(o => o.Id).ToList();

        Take(a, State(a)!.Offer!.Id);

        Assert.Equal(before, Missions(a).Offers.Where(o => o.Story is null).Select(o => o.Id));
    }

    [Fact]
    public void AStoryDeliveryAndABoardCollectShareTheHoldHonestly()
    {
        var a = Pilot();
        Dock(a);
        Take(a, State(a)!.Offer!.Id); // бумаги лежат в трюме настоящим предметом
        Assert.Equal(1, PlayerOf(a).Cargo.Count("papers"));

        // Доставка сдаётся стыковкой в порту; работа с доски остаётся.
        Take(a, Missions(a).Offers.First(o => o.Story is null).Id);
        Undock(a);
        JumpTo(a, "port");
        Dock(a);

        Assert.Null(Missions(a).StoryActive);
        Assert.NotNull(Missions(a).Active);
        Assert.Contains("intro", PlayerOf(a).StoryOf(Campaign).Done);
        Assert.Equal(0, PlayerOf(a).Cargo.Count("papers"));
    }

    [Fact]
    public void TwoLiveMissionsAtOnceAreRefused()
    {
        _galaxy = New(EscortBoard);
        var a = Pilot();
        Passed(a, "intro"); // следующая — живая: конвой до Терры
        Dock(a);
        Take(a, Missions(a).Offers.First(o => o.Kind == MissionRules.EscortKind && o.Story is null).Id);
        Assert.NotNull(Missions(a).Active);

        var story = State(a)!.Offer!;
        Assert.Equal(MissionRules.EscortKind, story.Kind);
        Take(a, story.Id);

        Assert.Equal(Protocol.LiveBusyNotice, a.Last<NoticeMsg>().Code);
        Assert.Null(Missions(a).StoryActive);
    }

    [Fact]
    public void FinishingBoardWorkDoesNotEndAStoryRun()
    {
        _galaxy = New(HuntBoard);
        var a = Pilot();
        Passed(a, "intro");
        Dock(a);
        Take(a, Missions(a).Offers.First(o => o.Kind == MissionRules.HuntKind).Id);
        Take(a, State(a)!.Offer!.Id);
        Undock(a);
        Steps(1);
        var convoy = Missions(a).StoryMark?.Ship ?? 0;
        Assert.NotEqual(0, convoy); // конвой вышел вместе с пилотом

        // Сбитый камень закрывает охоту с доски; сюжетный конвой при этом идёт дальше.
        var rock = RoomOf(a).LaunchMeteor("small", 1000, 1000, 0, 0)!;
        rock.Hp = 0;
        rock.KilledBy = IdOf(a);
        Steps(1);

        Assert.Null(Missions(a).Active);
        Assert.Equal(MissionRules.EscortKind, Missions(a).StoryActive?.Offer.Kind);
        Assert.Equal(convoy, Missions(a).StoryMark?.Ship);
        Assert.False(RoomOf(a).Entity(convoy)!.IsDead);
    }

    [Fact]
    public void DeathFailsOnlyWhatDiesWithTheShip()
    {
        var a = Pilot();
        Passed(a, "intro");
        Dock(a);
        Take(a, Missions(a).Offers.First(o => o.Story is null).Id); // «собрать» переживает гибель
        Take(a, State(a)!.Offer!.Id);                               // конвой — нет
        Undock(a);
        Steps(1);

        var player = PlayerOf(a);
        player.Hp = 0;
        Steps(2);

        Assert.Null(Missions(a).StoryActive);
        Assert.NotNull(Missions(a).Active);
    }

    // ------------------------------------------------------------------ куда дальше

    [Fact]
    public void TheNextMeetingIsAlwaysKnown()
    {
        var a = Pilot();
        Undock(a);
        JumpTo(a, "port");

        // Кампанию ещё не трогали, до места выдачи прыжок — а указатель уже есть.
        var next = State(a)?.Next;
        Assert.NotNull(next);
        Assert.Equal(("intro", "st:home", "home", "Ильина"), (next!.Mission, next.Place, next.System, next.Giver));
        Assert.Null(State(a)!.Offer);

        JumpTo(a, "home");
        Dock(a);
        Take(a, State(a)!.Offer!.Id);
        Assert.Null(State(a)!.Next); // миссия взята — ведёт она сама

        Undock(a);
        JumpTo(a, "port");
        Dock(a);
        Assert.Equal("guard", State(a)!.Next!.Mission);
    }

    [Fact]
    public void VeteransSkipThePrologue()
    {
        var a = Pilot();
        Passed(a, "guard"); // кампания начата до того, как появился пролог
        Dock(a);

        Assert.Equal("fetch", State(a)!.Next!.Mission);
        Assert.Equal(StoryRules.OfferId(Campaign, "fetch"), State(a)!.Offer!.Id);
        Assert.Equal(2, State(a)!.Done); // пролог засчитан, хоть в профиле его нет
    }

    [Fact]
    public void TheStoryIsNotOfferedMidTutorial_ButItsPointerIs()
    {
        // Вход — в доке станции, где сюжет и выдают. Шаг «вылетите»: на доску сюжет ещё не выходит,
        // но куда он позовёт — уже известно.
        var a = Pilot(tutorial: true);
        Assert.True(PlayerOf(a).Docked);
        Assert.Null(State(a)!.Offer);
        Assert.Equal("intro", State(a)!.Next!.Mission);

        Dock(a); // вылет засчитан, и снова в док: дальше — «возьмите работу»
        var offer = State(a)!.Offer;
        Assert.NotNull(offer);
        Take(a, offer!.Id);
        // Взятая сюжетная миссия закрывает и последний шаг обучения.
        Assert.Null(Missions(a).Tutorial);
    }

    [Fact]
    public void AnOldProfileWithTheStoryInTheBoardSlotMovesItOver()
    {
        var a = Pilot();
        Dock(a);
        var story = State(a)!.Offer!;
        Take(a, story.Id);

        // Профиль до этого этапа держал сюжет в Mission: так его и запишем, а потом зайдём заново.
        var account = _accounts.Login("Alice", Password).Id;
        var profile = _accounts.Profile(account)!;
        Assert.NotNull(profile.StoryMission);
        _accounts.Save(account, profile with { Mission = profile.StoryMission, StoryMission = null });
        _accounts.Flush();

        _galaxy = New(CollectBoard);
        var back = new FakeConnection(++_nextConnection);
        _galaxy.JoinAccount(back, account, "Alice");

        Assert.Equal(story.Id, Missions(back).StoryActive?.Offer.Id);
        Assert.Null(Missions(back).Active);
    }
}
