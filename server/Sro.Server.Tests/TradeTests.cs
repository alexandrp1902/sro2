using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Обмен между игроками (M16b): предложение, стол, подтверждение с номером редакции и всё, чем сделку
/// можно сорвать. Главное здесь — что половина сделки не проходит никогда.
/// </summary>
public sealed class TradeTests
{
    private const int JumpTicks = SimConfig.TickRate; // jumpSeconds: 1

    private static readonly GalaxyRules Rules = new(
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            // PvP везде: иначе в тесте «сбили посреди обмена» стрелять было бы нельзя.
            ["home"] = new("Home", Pvp: GalaxyRules.PvpFree, Gates: [new GateDef("wild", 3000, 0)]),
            ["wild"] = new("Wild", Station: false, Gates: [new GateDef("home", -3000, 0)]),
        },
        Links: [new LinkDef("home", "wild", 10)]);

    /// <summary>Руда объёмная: на ней видно, что трюм считается нетто, а не «влезет ли всё разом».</summary>
    private static readonly LootRules Loot = new(
        Items: new Dictionary<string, LootItem>
        {
            ["metal"] = new("Металл", Volume: 1, Price: 10),
            ["ore"] = new("Руда", Volume: 4, Price: 8),
        });

    private readonly Galaxy _galaxy;
    private int _nextConnection;

    public TradeTests() : this(null) { }

    private TradeTests(TradeRules? trade)
    {
        _galaxy = new Galaxy(
            TestBalance.Create(new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), loot: Loot,
                shop: new ShopRules(StartCredits: 1000)) with
            {
                GalaxySet = Rules,
                TradeSet = trade ?? new TradeRules(Range: 1000, InviteSeconds: 2),
            },
            NullLogger.Instance, roll: () => 0, random: seed => new Random(seed));
    }

    private FakeConnection Guest(string name)
    {
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.Join(connection, null, name, null);
        _galaxy.Undock(connection);
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Room RoomOf(FakeConnection connection) => _galaxy.RoomOf(connection)!;

    private Player PlayerOf(FakeConnection connection) => RoomOf(connection).Pilot(IdOf(connection))!;

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private void Place(FakeConnection connection, double x, double y) =>
        PlayerOf(connection).Ship = new ShipState { X = x, Y = y };

    private void Trade(FakeConnection connection, string action, FakeConnection? other = null,
        int credits = 0, IReadOnlyDictionary<string, int>? items = null, int rev = 0) =>
        _galaxy.Trade(connection, action, other is null ? 0 : IdOf(other), credits, items, rev);

    /// <summary>a зовёт b, b соглашается: стол открыт.</summary>
    private void Open(FakeConnection a, FakeConnection b)
    {
        Trade(a, TradeCodes.InviteAction, b);
        Trade(b, TradeCodes.AcceptAction, a);
    }

    private static TradeStateMsg State(FakeConnection connection) => connection.Last<TradeStateMsg>();

    private static string LastCode(FakeConnection connection) => connection.Last<TradeEventMsg>().Code;

    private static int Credits(FakeConnection connection) => connection.Last<CargoMsg>().Credits;

    private void Give(FakeConnection connection, string item, int count) =>
        PlayerOf(connection).Cargo.Add(item, count);

    /// <summary>Двое рядом, у каждого свой груз и кредиты.</summary>
    private (FakeConnection A, FakeConnection B) Pair()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        Place(a, 0, 0);
        Place(b, 100, 0);
        Give(a, "metal", 5);
        Give(b, "ore", 2);
        return (a, b);
    }

    [Fact]
    public void Invite_ThenAccept_OpensTheTable_ForBoth()
    {
        var (a, b) = Pair();
        Trade(a, TradeCodes.InviteAction, b);
        Assert.Equal(new TradeInviteMsg(IdOf(a), "Alice", 2), b.Last<TradeInviteMsg>());
        Assert.Equal(TradeCodes.Invited, LastCode(a));

        Trade(b, TradeCodes.AcceptAction, a);
        foreach (var c in new[] { a, b })
        {
            Assert.Equal(TradeCodes.Opened, LastCode(c));
            Assert.True(State(c).Active);
            Assert.Equal(0, State(c).Rev);
        }
        Assert.Equal("Alice", State(b).Their!.Name);
        Assert.Equal("Bob", State(b).Own!.Name);
    }

    [Fact]
    public void ATrade_MovesCreditsAndCargo_BothWays_Once()
    {
        var (a, b) = Pair();
        Open(a, b);
        Trade(a, TradeCodes.OfferAction, credits: 300, items: new Dictionary<string, int> { ["metal"] = 3 });
        Trade(b, TradeCodes.OfferAction, items: new Dictionary<string, int> { ["ore"] = 1 });
        var rev = State(a).Rev;

        Trade(a, TradeCodes.ReadyAction, rev: rev);
        Assert.True(State(a).Own!.Ready);
        Assert.False(State(b).Own!.Ready);

        Trade(b, TradeCodes.ReadyAction, rev: rev);
        foreach (var c in new[] { a, b })
        {
            Assert.Equal(TradeCodes.Done, LastCode(c));
            Assert.False(State(c).Active); // окно закрывается только по этому
        }
        Assert.Equal(700, Credits(a));
        Assert.Equal(1300, Credits(b));
        Assert.Equal(2, PlayerOf(a).Cargo.Count("metal"));
        Assert.Equal(1, PlayerOf(a).Cargo.Count("ore"));
        Assert.Equal(3, PlayerOf(b).Cargo.Count("metal"));
        Assert.Equal(1, PlayerOf(b).Cargo.Count("ore"));

        // Второе подтверждение уже некуда девать: сделка снята со стола, и повтор её не проводит.
        Trade(b, TradeCodes.ReadyAction, rev: rev);
        Assert.Equal(700, Credits(a));
        Assert.Equal(2, PlayerOf(a).Cargo.Count("metal"));
    }

    [Fact]
    public void AnyEdit_ClearsBothConfirmations_AndAStaleOneIsRefused()
    {
        var (a, b) = Pair();
        Open(a, b);
        Trade(a, TradeCodes.OfferAction, items: new Dictionary<string, int> { ["metal"] = 1 });
        var stale = State(a).Rev;
        Trade(a, TradeCodes.ReadyAction, rev: stale);
        Assert.True(State(a).Own!.Ready);

        // Правка гасит подтверждения у обоих и двигает редакцию.
        Trade(b, TradeCodes.OfferAction, items: new Dictionary<string, int> { ["ore"] = 1 });
        Assert.False(State(a).Own!.Ready);
        Assert.False(State(a).Their!.Ready);
        Assert.NotEqual(stale, State(a).Rev);

        // Подтверждение по тому, чего игрок уже не видит, не принимается.
        Trade(a, TradeCodes.ReadyAction, rev: stale);
        Assert.Equal(TradeCodes.Stale, LastCode(a));
        Assert.False(State(a).Own!.Ready);
        Assert.Equal(5, PlayerOf(a).Cargo.Count("metal"));
    }

    [Fact]
    public void AnOfferKeepsOnlyWhatMakesSense()
    {
        var (a, b) = Pair();
        Open(a, b);
        Trade(a, TradeCodes.OfferAction, credits: -50, items: new Dictionary<string, int>
        {
            ["metal"] = 2,
            ["ore"] = -1,       // отрицательное количество — не предложение, а попытка забрать
            ["unobtanium"] = 1, // такого предмета игра не знает
        });
        var own = State(a).Own!;
        Assert.Equal(0, own.Credits);
        Assert.Equal(new Dictionary<string, int> { ["metal"] = 2 }, own.Items);
    }

    [Fact]
    public void NothingMoves_WhenTheGoodsOrTheCreditsAreNotThere()
    {
        var (a, b) = Pair();
        Open(a, b);
        // Обещано больше, чем лежит в трюме: на столе это разрешено, а в момент сделки — нет.
        Trade(a, TradeCodes.OfferAction, items: new Dictionary<string, int> { ["metal"] = 9 });
        var rev = State(a).Rev;
        Trade(a, TradeCodes.ReadyAction, rev: rev);
        Trade(b, TradeCodes.ReadyAction, rev: rev);
        Assert.Equal(TradeCodes.NoItems, LastCode(a));
        Assert.Equal(5, PlayerOf(a).Cargo.Count("metal"));
        Assert.Equal(1000, Credits(a));

        Open(a, b);
        Trade(a, TradeCodes.OfferAction, credits: 5000);
        rev = State(a).Rev;
        Trade(a, TradeCodes.ReadyAction, rev: rev);
        Trade(b, TradeCodes.ReadyAction, rev: rev);
        Assert.Equal(TradeCodes.NoCredits, LastCode(b));
        Assert.Equal(1000, Credits(a));
        Assert.Equal(1000, Credits(b));
    }

    [Fact]
    public void TheHoldIsCountedNet_SoASwapOfEqualBulkFits()
    {
        var (a, b) = Pair();
        // Трюм лёгкого корпуса — 20. У Алисы он забит почти под завязку.
        Give(a, "ore", 4); // 5 металла + 16 объёма руды = 21… нет, ровно 20 занято? — 5 + 16 = 21
        PlayerOf(a).Cargo.Remove("metal", 1); // 4 металла + 16 = 20, полный трюм
        Open(a, b);

        // Меняем руду на руду: объём не меняется, и это должно пройти при полном трюме.
        Trade(a, TradeCodes.OfferAction, items: new Dictionary<string, int> { ["ore"] = 1 });
        Trade(b, TradeCodes.OfferAction, items: new Dictionary<string, int> { ["ore"] = 1 });
        var rev = State(a).Rev;
        Trade(a, TradeCodes.ReadyAction, rev: rev);
        Trade(b, TradeCodes.ReadyAction, rev: rev);
        Assert.Equal(TradeCodes.Done, LastCode(a));
        Assert.Equal(4, PlayerOf(a).Cargo.Count("ore"));

        // А вот лишний металл в полный трюм уже не влезет, и тогда не двигается ничего.
        Open(a, b);
        Trade(b, TradeCodes.OfferAction, items: new Dictionary<string, int> { ["ore"] = 1 });
        rev = State(a).Rev;
        Trade(a, TradeCodes.ReadyAction, rev: rev);
        Trade(b, TradeCodes.ReadyAction, rev: rev);
        Assert.Equal(TradeCodes.NoRoom, LastCode(a));
        Assert.Equal(4, PlayerOf(a).Cargo.Count("ore"));
        Assert.Equal(2, PlayerOf(b).Cargo.Count("ore"));
    }

    [Fact]
    public void FlyingApart_BreaksTheDeal()
    {
        var (a, b) = Pair();
        Open(a, b);
        Place(b, 5000, 0);
        Steps(1);
        foreach (var c in new[] { a, b })
        {
            Assert.Equal(TradeCodes.TooFar, LastCode(c));
            Assert.False(State(c).Active);
        }
    }

    [Fact]
    public void Docking_BreaksTheDeal_AtOnce()
    {
        var (a, b) = Pair();
        Open(a, b);
        var station = RoomOf(a).StationPosition;
        Place(b, station.X, station.Y);
        _galaxy.With(b, r => r.Dock(b, true));
        // Без шага галактики: иначе второй успел бы подтвердить сделку с тем, кого уже нет в космосе.
        Assert.Equal(TradeCodes.Docked, LastCode(a));
        Assert.False(State(a).Active);
    }

    [Fact]
    public void Dying_BreaksTheDeal()
    {
        var (a, b) = Pair();
        Open(a, b);
        // Кто и чем сбил — дело боевых тестов; обмену важно только то, что корабль разбит.
        PlayerOf(b).Hp = 0;
        Steps(1);
        Assert.True(PlayerOf(b).IsDead);
        foreach (var c in new[] { a, b })
        {
            Assert.Equal(TradeCodes.Dead, LastCode(c));
            Assert.False(State(c).Active);
        }
    }

    [Fact]
    public void Jumping_BreaksTheDeal()
    {
        var (a, b) = Pair();
        // Оба у врат и рядом: сделку рвёт именно прыжок, а не разъехавшаяся дистанция.
        Place(a, 2900, 0);
        Place(b, 3000, 0);
        Open(a, b);
        _galaxy.With(b, r => r.Jump(b, "wild"));
        Steps(JumpTicks + 2);
        Assert.Equal(TradeCodes.Jumped, LastCode(a));
        Assert.False(State(a).Active);
    }

    [Fact]
    public void LosingTheConnection_BreaksTheDeal()
    {
        var (a, b) = Pair();
        Open(a, b);
        _galaxy.Disconnect(b);
        Assert.Equal(TradeCodes.Left, LastCode(a));
        Assert.False(State(a).Active);
    }

    [Fact]
    public void AnInvitationExpires_AndTheOneWhoAskedIsTold()
    {
        var (a, b) = Pair();
        Trade(a, TradeCodes.InviteAction, b);
        Steps(SimConfig.TickRate * 2 + 2);
        Assert.Equal(TradeCodes.Expired, LastCode(a));
        Trade(b, TradeCodes.AcceptAction, a);
        Assert.Equal(TradeCodes.Expired, LastCode(b));
    }

    [Fact]
    public void OneCannotTradeWithTwo_NorWithSomeoneTooFar()
    {
        var (a, b) = Pair();
        var c = Guest("Carol");
        Place(c, 200, 0);
        Open(a, b);
        Trade(c, TradeCodes.InviteAction, a);
        Assert.Equal(TradeCodes.Busy, LastCode(c));

        var (d, e) = (Guest("Dan"), Guest("Eve"));
        Place(d, 0, 0);
        Place(e, 9000, 0);
        Trade(d, TradeCodes.InviteAction, e);
        Assert.Equal(TradeCodes.TooFar, LastCode(d));
    }

    [Fact]
    public void Declining_TellsTheOneWhoAsked_AndCancelTellsTheOther()
    {
        var (a, b) = Pair();
        Trade(a, TradeCodes.InviteAction, b);
        Trade(b, TradeCodes.DeclineAction, a);
        Assert.Equal(TradeCodes.Declined, LastCode(a));

        Open(a, b);
        Trade(a, TradeCodes.CancelAction);
        Assert.Equal(TradeCodes.Left, LastCode(b));
        Assert.False(State(b).Active);
    }
}
