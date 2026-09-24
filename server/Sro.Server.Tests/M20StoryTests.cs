using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Сюжетные кампании (M20a). Проверяется не текст «Тихой войны», а движок под ним: цепочка помнит
/// выполненное, работа даётся в своём месте и своей репутации, скриптованный груз ждёт хозяина,
/// сюжетный предмет переживает гибель и не продаётся, вызванный сюжетом корабль не стоит ничего,
/// а выбор в диалоге оставляет флаг.
/// </summary>
public sealed class M20StoryTests : IDisposable
{
    private const string Password = "secret";
    private const string Campaign = "test";

    /// <summary>home (станция) — port (станция): двух мест хватает, чтобы сдавать не там, где взял.</summary>
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
            // Повстанец — пират по механике: именно поэтому за него и надо отдельно не платить.
            ["rebel"] = new("Повстанец", "light", "pulse", Hp: 200, Shield: 50, Damage: 0.3, Bounty: 60),
        });

    private static readonly LootRules Loot = new(
        FadeSeconds: 0,
        Items: new Dictionary<string, LootItem>
        {
            ["metal"] = new("Металл", Volume: 1, Price: 10),
            ["cores"] = new("Привод", Volume: 1, Price: 0, Story: true),
            ["evidence"] = new("Секции", Volume: 10, Price: 0, Story: true),
            ["relic"] = new("Журнал", Volume: 1, Price: 0, Story: true),
        });

    /// <summary>Доска из одних «собрать»: она нужна только затем, чтобы сюжет не был единственной строкой.</summary>
    private static readonly MissionRules Missions = new(Offers: 2, Collect: [new CollectTemplate("metal", 2, 2)]);

    private static readonly StoryRules Story = new(new Dictionary<string, StoryCampaign>
    {
        [Campaign] = new(
            "Проверка",
            Total: 5, // задумано пять, написано три — журнал должен говорить «из 5»
            Missions:
            [
                new(
                    "fetch", "Приводы", "st:home", "Холт", "охрана",
                    Objective: "Соберите два привода",
                    Kind: MissionRules.CollectKind,
                    Item: "cores",
                    Count: 2,
                    Dest: "st:home",
                    Reward: 100,
                    RepReward: 5,
                    Point: new StoryPoint(1000, 1000),
                    Lines: new StoryLines(Accept: ["Два ящика."], Done: ["Это не приводы."])),
                new(
                    "carry", "Секции", "st:home", "Холт", "охрана",
                    Objective: "Отвезите секции в Порт",
                    Rep: "friend",
                    Kind: MissionRules.DeliverKind,
                    Dest: "st:port",
                    Count: 1,
                    Reward: 200,
                    Give: ["evidence"],
                    Spawns: [new StorySpawn(StoryRules.OnUndock, "rebel", 1, 1, "Перехват", new StoryPoint(600, 0))]),
                new(
                    "probe", "Журнал", "st:port", "Ева", "инженер",
                    Objective: "Поднимите журнал",
                    Kind: MissionRules.CollectKind,
                    Item: "relic",
                    Count: 1,
                    Reward: 300,
                    Finish: StoryRules.FinishChoice,
                    Point: new StoryPoint(-1000, 1000),
                    Choice: new StoryChoice(
                        "Отдадите?",
                        [
                            new StoryOption("Отдать", "given", ["Принято."], Take: true),
                            new StoryOption("Оставить", "kept", ["Мы вас не нашли."]),
                        ])),
            ]),
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-story-" + Guid.NewGuid().ToString("N"));
    private readonly Accounts.AccountStore _accounts;
    private Galaxy _galaxy;
    private int _nextConnection;

    public M20StoryTests()
    {
        _accounts = new Accounts.AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _galaxy = New();
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Galaxy New() =>
        new(TestBalance.Create(
                new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), Npcs, Loot,
                shop: new ShopRules(StartCredits: 1000), reputation: TestBalance.Reputation(decayPerDay: 0),
                missions: Missions) with
            {
                GalaxySet = Galaxy,
                StorySet = Story,
            }, NullLogger.Instance, _accounts, roll: () => 0, random: seed => new Random(seed));

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

    /// <summary>Взять сюжетную работу, которую здесь дают.</summary>
    private void TakeStory(FakeConnection connection)
    {
        var offer = State(connection)?.Offer;
        Assert.NotNull(offer);
        Do(connection, r => r.Mission(connection, Protocol.AcceptMission, offer!.Id));
        Assert.NotNull(Missions_(connection).Active?.Offer.Story);
    }

    /// <summary>Долететь до скриптованного груза и собрать его весь.</summary>
    private void Collect(FakeConnection connection)
    {
        var room = RoomOf(connection);
        for (var guard = 0; guard < 20; guard++)
        {
            var drop = room.Drops.FirstOrDefault(d => d.Owner == IdOf(connection));
            if (drop is null) break;
            Place(connection, drop.X, drop.Y);
            room.SetLootTarget(connection, drop.Id);
            room.Grab(connection);
            Steps(1);
        }
    }

    private static int Credits(FakeConnection connection) => connection.Last<CargoMsg>().Credits;

    /// <summary>Очки места («st:home»): их клиент получает полным ключом.</summary>
    private static double Rep(FakeConnection connection, string key) =>
        connection.Last<RepMsg>().Places.GetValueOrDefault(key);

    /// <summary>Очки системы: их клиент получает голым id.</summary>
    private static double SystemRep(FakeConnection connection, string system) =>
        connection.Last<RepMsg>().Systems.GetValueOrDefault(system);

    /// <summary>Сюжетный гейт смотрит на отношение системы, а не места: власти Новы — это система.</summary>
    private void MakeFriend(FakeConnection connection, string system = "home") =>
        PlayerOf(connection).Rep.Add(Reputation.System(system), 40, 0, RoomOf(connection).Balance.Reputation);

    // ------------------------------------------------------------------ тесты

    [Fact]
    public void TheCampaignStartsWhereItIsWritten()
    {
        var a = Pilot();
        var state = State(a);
        Assert.NotNull(state);
        Assert.Equal("Проверка", state!.Name);
        Assert.Equal(0, state.Done);
        // Кампания задумана длиннее, чем написана: журнал должен говорить «из 5», а не «из 3».
        Assert.Equal(5, state.Total);
        Assert.Equal("Приводы", state.Offer?.Story?.Title);
        // Сюжетная строка стоит в том же списке, что и доска: иначе «Взять» нечего было бы найти.
        Assert.Contains(Missions_(a).Offers, o => o.Story is not null);
    }

    [Fact]
    public void ThereIsNoStoryWorkInSpaceOrInTheWrongPlace()
    {
        var a = Pilot();
        Undock(a);
        JumpTo(a, "port");
        Dock(a);
        // В Порту первой миссии не дают: её выдают на home.
        Assert.Null(State(a)?.Offer);
    }

    [Fact]
    public void TheSecondMissionWaitsForTheFirst()
    {
        var a = Pilot();
        MakeFriend(a); // чтобы вторую не держала репутация
        Assert.Equal("fetch", State(a)!.Offer!.Story!.Mission);
        Finish(a);
        Assert.Equal("carry", State(a)!.Offer!.Story!.Mission);
        Assert.Equal(1, State(a)!.Done);
    }

    [Fact]
    public void ReputationGatesAMission()
    {
        var a = Pilot();
        Finish(a);
        // Вторая просит «Друга»: на нуле её на доске нет вовсе, а первой уже нет — она пройдена.
        Assert.Null(State(a)!.Offer);

        MakeFriend(a);
        Undock(a);
        Dock(a); // доска и сюжет пересчитываются на стыковке
        Assert.Equal("carry", State(a)!.Offer!.Story!.Mission);
    }

    [Fact]
    public void ScriptedCargoWaitsAtThePointAndOnlyForItsOwner()
    {
        var a = Pilot();
        TakeStory(a);
        Undock(a);
        var mine = RoomOf(a).Drops.Where(d => d.Owner == IdOf(a)).ToList();
        Assert.Equal(2, mine.Sum(d => d.Count));
        // Лежит там, где написано, и не протухает: это точка на карте, а не обломки.
        Assert.All(mine, d => Assert.True(Math.Abs(d.X - 1000) < 200 && Math.Abs(d.Y - 1000) < 200));
        Assert.All(mine, d => Assert.True(d.ExpiresAtTick == long.MaxValue));
        // И метка цели ведёт именно туда.
        Assert.Equal((1000d, 1000d), (Missions_(a).Mark!.X, Missions_(a).Mark!.Y));

        var b = Pilot("Bob");
        Undock(b);
        var room = RoomOf(b);
        Place(b, mine[0].X, mine[0].Y);
        room.SetLootTarget(b, mine[0].Id);
        room.Grab(b);
        Assert.Equal(Protocol.NotYoursNotice, b.Last<NoticeMsg>().Code);
        Assert.True(PlayerOf(b).Cargo.IsEmpty);
    }

    [Fact]
    public void CollectIsHandedInWhereItWasPromised()
    {
        var a = Pilot();
        TakeStory(a);
        Undock(a);
        Collect(a);
        Assert.Equal(2, PlayerOf(a).Cargo.Count("cores"));

        // Не в том доке — не принимают: обычное «собрать» сдаётся где угодно, сюжетное — там, где ждут.
        JumpTo(a, "port");
        Dock(a);
        Do(a, r => r.Mission(a, Protocol.CompleteMission, null));
        Assert.Equal(Protocol.TooFarNotice, a.Last<NoticeMsg>().Code);
        Assert.NotNull(Missions_(a).Active);

        Undock(a);
        JumpTo(a, "home");
        Dock(a);
        var before = Credits(a);
        Do(a, r => r.Mission(a, Protocol.CompleteMission, null));
        Assert.Null(Missions_(a).Active);
        Assert.Equal(before + 100, Credits(a));
        // Сданное уехало из трюма, миссия записана, отношение ушло месту.
        Assert.Equal(0, PlayerOf(a).Cargo.Count("cores"));
        Assert.Equal(1, State(a)!.Done);
        Assert.Equal(5, Rep(a, "st:home"));
    }

    [Fact]
    public void AStoryItemSurvivesDeathAndIsNotSold()
    {
        var a = Pilot();
        MakeFriend(a);
        Finish(a);
        Dock(a);
        TakeStory(a); // carry: секции выдаются в трюм
        Assert.Equal(1, PlayerOf(a).Cargo.Count("evidence"));
        // Настоящий предмет уже занимает трюм — бронировать под него объём второй раз незачем.
        Assert.Equal(0, PlayerOf(a).Cargo.Reserved);

        Undock(a);
        PlayerOf(a).Cargo.Add("metal", 3);
        Place(a, 1500, 1500);
        PlayerOf(a).Hp = 0;
        Steps(2);
        // Обычный груз высыпался, сюжетный остался: цепочка не рвётся на первом же респауне.
        Assert.Equal(0, PlayerOf(a).Cargo.Count("metal"));
        Assert.Equal(1, PlayerOf(a).Cargo.Count("evidence"));

        Steps(SimConfig.TickRate * 3);
        Dock(a);
        PlayerOf(a).Cargo.Add("metal", 2);
        Do(a, r => r.Sell(a, null));
        Assert.Equal(0, PlayerOf(a).Cargo.Count("metal"));
        Assert.Equal(1, PlayerOf(a).Cargo.Count("evidence"));

        // Поимённо — тоже нет, и за борт не выбрасывается.
        Do(a, r => r.Sell(a, "evidence"));
        Assert.Equal(Protocol.NoGoodsNotice, a.Last<NoticeMsg>().Code);
        Undock(a);
        Do(a, r => r.Jettison(a, "evidence"));
        Assert.Equal(Protocol.StoryItemNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal(1, PlayerOf(a).Cargo.Count("evidence"));
    }

    [Fact]
    public void AStoryShipCostsNothingAndChangesNothing()
    {
        var a = Pilot();
        MakeFriend(a);
        Finish(a);
        Dock(a);
        TakeStory(a);
        Undock(a); // onUndock: «Перехват»

        var rebel = Assert.Single(RoomOf(a).Pirates.Where(p => p.Story));
        Assert.Equal("Перехват", rebel.Name);
        Assert.Equal(Protocol.RebelKind, a.Last<PlayersMsg>().Players.First(p => p.Id == rebel.Id).Kind);
        // Горячая правка баланса имя сюжетного корабля не сбивает.
        rebel.Rebind(rebel.Type, RoomOf(a).Balance.Npc, TestBalance.Hulls, TestBalance.Hulls);
        Assert.Equal("Перехват", rebel.Name);

        var credits = Credits(a);
        var system = SystemRep(a, "home");
        PlayerOf(a).WeaponId = "doom";
        Place(a, rebel.Ship.X, rebel.Ship.Y + 200);
        Do(a, r => r.SetTarget(a, rebel.Id));
        Do(a, r => r.SetFire(a, true));
        Steps(2);
        Do(a, r => r.SetFire(a, false));
        Assert.True(rebel.IsDead);
        // Ни головы, ни очков: кампания — не источник дохода и не способ отмыть репутацию.
        Assert.Equal(credits, Credits(a));
        Assert.Equal(system, SystemRep(a, "home"));
    }

    [Fact]
    public void AbandoningAStoryMissionPutsItBackWithoutAPenalty()
    {
        var a = Pilot();
        TakeStory(a);
        var rep = Rep(a, "st:home");
        Do(a, r => r.Mission(a, Protocol.AbandonMission, null));
        Assert.Null(Missions_(a).Active);
        // Отказ от сюжета — это «не сейчас», а не «подвёл заказчика».
        Assert.Equal(rep, Rep(a, "st:home"));
        Assert.Equal("fetch", State(a)!.Offer!.Story!.Mission);
        Assert.Equal(0, State(a)!.Done);
        // И ящики убраны: второй заход разложит их заново.
        Assert.Empty(RoomOf(a).Drops.Where(d => d.Owner == IdOf(a)));
    }

    [Fact]
    public void TheChoiceWritesAFlagAndFinishesTheMission()
    {
        var a = Pilot();
        MakeFriend(a);
        Finish(a);      // fetch
        Dock(a);
        TakeStory(a);   // carry
        Undock(a);
        JumpTo(a, "port");
        Dock(a);        // доставка сдаётся сама
        Assert.Null(Missions_(a).Active);
        Assert.Equal(2, State(a)!.Done);

        TakeStory(a);   // probe, здесь же в Порту
        Undock(a);
        Collect(a);

        // Вопрос задан, миссия ещё идёт.
        var dialog = a.Last<DialogMsg>();
        Assert.Equal("Отдадите?", dialog.Lines[0]);
        Assert.Equal(["given", "kept"], dialog.Options!.Select(o => o.Flag));
        Assert.NotNull(Missions_(a).Active);

        // Чужого варианта не бывает: сервер отвечает только на то, что было на кнопке.
        Do(a, r => r.Mission(a, Protocol.ChooseStory, "ransom"));
        Assert.NotNull(Missions_(a).Active);

        Do(a, r => r.Mission(a, Protocol.ChooseStory, "kept"));
        Assert.Null(Missions_(a).Active);
        Assert.Equal(3, State(a)!.Done);
        // «Оставить» — журнал остаётся уликой в трюме; «отдать» забрал бы его.
        Assert.Equal(1, PlayerOf(a).Cargo.Count("relic"));
        Assert.Contains("kept", PlayerOf(a).StoryOf(Campaign).Flags);
    }

    [Fact]
    public void GivingTheRelicAwayTakesItFromTheHold()
    {
        var a = Pilot();
        MakeFriend(a);
        Finish(a);
        Dock(a);
        TakeStory(a);
        Undock(a);
        JumpTo(a, "port");
        Dock(a);
        TakeStory(a);
        Undock(a);
        Collect(a);
        Do(a, r => r.Mission(a, Protocol.ChooseStory, "given"));
        Assert.Equal(0, PlayerOf(a).Cargo.Count("relic"));
        Assert.Contains("given", PlayerOf(a).StoryOf(Campaign).Flags);
    }

    [Fact]
    public void ProgressAndFlagsSurviveALogout()
    {
        var a = Pilot();
        Finish(a);
        var account = _accounts.Login("Alice", Password).Id;
        _accounts.Flush();

        // Новая галактика — как новый запуск сервера: всё, что пилот помнит о кампании, приехало из файла.
        _galaxy = New();
        var back = new FakeConnection(++_nextConnection);
        _galaxy.JoinAccount(back, account, "Alice");
        Assert.Equal(1, State(back)!.Done);
        Assert.Contains("fetch", PlayerOf(back).StoryOf(Campaign).Done);
    }

    [Fact]
    public void WhenTheWrittenPartRunsOutTheJournalSaysSo()
    {
        var a = Pilot();
        MakeFriend(a);
        Finish(a);
        Dock(a);
        TakeStory(a);
        Undock(a);
        JumpTo(a, "port");
        Dock(a);
        TakeStory(a);
        Undock(a);
        Collect(a);
        Do(a, r => r.Mission(a, Protocol.ChooseStory, "kept"));

        var state = State(a)!;
        Assert.Equal(3, state.Done);
        Assert.Equal(5, state.Total);
        Assert.Null(state.Offer);
        Assert.True(state.More); // «продолжение следует»
    }

    /// <summary>Пройти первую миссию целиком: взять, собрать, вернуться, сдать.</summary>
    private void Finish(FakeConnection a)
    {
        TakeStory(a);
        Undock(a);
        Collect(a);
        Dock(a);
        Do(a, r => r.Mission(a, Protocol.CompleteMission, null));
        Assert.Null(Missions_(a).Active);
    }
}
