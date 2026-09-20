using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Ангар (M15.6, GDD §51): корабли стоят там, где их оставили. Пересесть можно только придя туда самому
/// или заказав перегон у буксира; тариф в доке обязан совпасть с тем, что спишет сервер.
/// </summary>
public sealed class HangarTests : IDisposable
{
    private const string Password = "secret";
    private const int JumpTicks = SimConfig.TickRate; // jumpSeconds: 1

    /// <summary>home и port — со станциями, между ними wild без станции: до port ровно два прыжка.</summary>
    private static readonly GalaxyRules Rules = new(
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new("Home", Gates: [new GateDef("wild", 3000, 0)]),
            ["wild"] = new("Wild", Station: false, Gates: [new GateDef("home", -3000, 0), new GateDef("port", 0, 3000)]),
            ["port"] = new("Port", Gates: [new GateDef("wild", 0, -3000)]),
            // Ни врат, ни маршрута: отсюда буксир не поедет никуда, и туда тоже.
            ["void"] = new("Void"),
        },
        Links: [new LinkDef("home", "wild", 30), new LinkDef("wild", "port", 20)]);

    private static readonly ShopRules Shop = new(
        StartCredits: 100_000,
        Hulls: new Dictionary<string, int> { ["light"] = 0, ["heavy"] = 8000 },
        Transport: new TransportDef(Base: 400, PerJump: 800, HullShare: 0.05));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-hangar-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private Galaxy _galaxy;
    private int _nextConnection;

    public HangarTests()
    {
        _accounts = new AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _galaxy = New();
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Galaxy New() =>
        new(TestBalance.Create(new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), shop: Shop) with
        {
            GalaxySet = Rules,
        }, NullLogger.Instance, _accounts, roll: () => 0);

    private FakeConnection Pilot(string name = "Alice")
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.JoinAccount(connection, login.Id, login.Name);
        return connection;
    }

    private string AccountId(string name = "Alice") => _accounts.Login(name, Password).Id;

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Room RoomOf(FakeConnection connection) => _galaxy.RoomOf(connection)!;

    private Player PlayerOf(FakeConnection connection) => RoomOf(connection).Pilot(IdOf(connection))!;

    private void Do(FakeConnection connection, Action<Room> command) => _galaxy.With(connection, command);

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private void Place(FakeConnection connection, double x, double y) =>
        PlayerOf(connection).Ship = new ShipState { X = x, Y = y };

    private void Dock(FakeConnection connection)
    {
        Place(connection, 0, 50);
        Do(connection, r => r.Dock(connection, true));
        Assert.True(connection.Last<HangarMsg>().Docked);
    }

    private void Undock(FakeConnection connection) => Do(connection, r => r.Dock(connection, false));

    /// <summary>Прыжок до конца: к вратам и ждём.</summary>
    private void JumpTo(FakeConnection connection, string to)
    {
        var gate = RoomOf(connection).Balance.SystemDef.GateTo(to)!;
        Place(connection, gate.X, gate.Y);
        Do(connection, r => r.Jump(connection, to));
        Steps(JumpTicks);
        Assert.Equal(to, RoomOf(connection).SystemId);
    }

    private static IReadOnlyDictionary<string, string> Ships(FakeConnection connection) =>
        connection.Last<HangarMsg>().Ships ?? new Dictionary<string, string>();

    [Fact]
    public void Buying_ParksTheOldHullWhereItWasBought()
    {
        var a = Pilot();
        Dock(a);
        Do(a, r => r.Buy(a, Protocol.HullItem, "heavy"));

        Assert.Equal("heavy", a.Last<HangarMsg>().Hull);
        // «Пчела» осталась стоять на станции home — она не исчезла и не поехала следом.
        Assert.Equal(new Dictionary<string, string> { ["light"] = "st:home" }, Ships(a));
    }

    [Fact]
    public void SwitchingBack_OnTheSpot_IsFreeAndMovesTheParkedShip()
    {
        var a = Pilot();
        Dock(a);
        Do(a, r => r.Buy(a, Protocol.HullItem, "heavy"));
        var credits = a.Last<CargoMsg>().Credits;

        Do(a, r => r.SetHull(a, "light"));

        Assert.Equal("light", a.Last<HangarMsg>().Hull);
        Assert.Equal(credits, a.Last<CargoMsg>().Credits); // пересадка на месте даром
        Assert.Equal(new Dictionary<string, string> { ["heavy"] = "st:home" }, Ships(a));
    }

    [Fact]
    public void SwitchingToAShipInAnotherDock_IsRefused()
    {
        var a = Pilot();
        Dock(a);
        Do(a, r => r.Buy(a, Protocol.HullItem, "heavy")); // «Пчела» осталась в home
        Undock(a);
        JumpTo(a, "wild");
        JumpTo(a, "port");
        Dock(a);

        Do(a, r => r.SetHull(a, "light"));

        Assert.Equal(Protocol.ShipElsewhereNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal("heavy", a.Last<HangarMsg>().Hull);
        Assert.Equal("st:home", Ships(a)["light"]);
    }

    [Fact]
    public void Transport_ChargesTheTariffAndBringsTheShipHere()
    {
        var a = Pilot();
        Dock(a);
        Do(a, r => r.Buy(a, Protocol.HullItem, "heavy"));
        Undock(a);
        JumpTo(a, "wild");
        JumpTo(a, "port");
        Dock(a);
        var credits = a.Last<CargoMsg>().Credits;

        Do(a, r => r.Transport(a, "light"));

        // «Пчела» даром, два прыжка: 400 + 2 × 800. Ровно то, что показал бы док.
        var expected = Shop.TransportCost(0, 2)!.Value;
        Assert.Equal(2000, expected);
        Assert.Equal(credits - expected, a.Last<CargoMsg>().Credits);
        Assert.Equal("st:port", Ships(a)["light"]);
        // И теперь в неё можно сесть здесь же.
        Do(a, r => r.SetHull(a, "light"));
        Assert.Equal("light", a.Last<HangarMsg>().Hull);
    }

    /// <summary>Цена зависит и от корпуса: тащить «Молот» за 8 000 дороже, чем «Пчелу».</summary>
    [Fact]
    public void Transport_CostsMoreForAnExpensiveHull()
    {
        var a = Pilot();
        Dock(a);
        Do(a, r => r.Buy(a, Protocol.HullItem, "heavy"));
        Do(a, r => r.SetHull(a, "light")); // «Молот» остаётся в home
        Undock(a);
        JumpTo(a, "wild");
        JumpTo(a, "port");
        Dock(a);
        var credits = a.Last<CargoMsg>().Credits;

        Do(a, r => r.Transport(a, "heavy"));

        var expected = Shop.TransportCost(8000, 2)!.Value; // 400 + 2 × (800 + 400)
        Assert.Equal(2800, expected);
        Assert.Equal(credits - expected, a.Last<CargoMsg>().Credits);
    }

    [Fact]
    public void Transport_RefusesWithoutCredits()
    {
        var a = Pilot();
        Dock(a);
        Do(a, r => r.Buy(a, Protocol.HullItem, "heavy"));
        Undock(a);
        JumpTo(a, "wild");
        JumpTo(a, "port");
        Dock(a);
        PlayerOf(a).Credits = 10;

        Do(a, r => r.Transport(a, "light"));

        Assert.Equal(Protocol.NoCreditsNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal("st:home", Ships(a)["light"]);
    }

    [Fact]
    public void Transport_RefusesTheShipYouAreSittingIn_AndOneThatIsNotYours()
    {
        var a = Pilot();
        Dock(a);
        var credits = a.Last<CargoMsg>().Credits;

        Do(a, r => r.Transport(a, "light")); // он под пилотом
        Do(a, r => r.Transport(a, "heavy")); // не куплен

        Assert.Equal(credits, a.Last<CargoMsg>().Credits);
        Assert.Empty(a.Messages.OfType<NoticeMsg>());
    }

    /// <summary>
    /// Профиль старше M15.6 не знает, где стоят корабли: считаем, что все ждут дома — там пилот
    /// и станет искать их первым делом.
    /// </summary>
    [Fact]
    public void OldProfile_PutsEveryShipAtHome()
    {
        var id = AccountId();
        _accounts.Save(id, new AccountProfile(
            500, "light", "pulse", ["light", "heavy"], [], new Dictionary<string, int>(), System: "port"));

        var a = Pilot();

        Assert.Equal("port", RoomOf(a).SystemId);
        Assert.Equal(new Dictionary<string, string> { ["heavy"] = "st:port" }, Ships(a));
    }

    /// <summary>Место могло пропасть из баланса, пока пилот отсутствовал: корабль тогда ждёт дома.</summary>
    [Fact]
    public void ProfileWithAVanishedPlace_FallsBackHome()
    {
        var id = AccountId();
        _accounts.Save(id, new AccountProfile(
            500, "light", "pulse", ["light", "heavy"], [], new Dictionary<string, int>(),
            System: "home", Ships: new Dictionary<string, string> { ["heavy"] = "st:atlantis" }));

        var a = Pilot();

        Assert.Equal("st:home", Ships(a)["heavy"]);
    }

    [Fact]
    public void WhereTheShipsStand_SurvivesARestart()
    {
        var a = Pilot();
        Dock(a);
        Do(a, r => r.Buy(a, Protocol.HullItem, "heavy"));
        Undock(a);
        JumpTo(a, "wild");
        JumpTo(a, "port");
        Dock(a); // дом теперь port, а «Пчела» так и стоит в home

        Assert.Equal(new Dictionary<string, string> { ["light"] = "st:home" }, _accounts.Profile(AccountId())!.Ships);

        _galaxy = New();
        var again = Pilot();

        Assert.Equal("port", RoomOf(again).SystemId);
        Assert.Equal(new Dictionary<string, string> { ["light"] = "st:home" }, Ships(again));
    }
}
