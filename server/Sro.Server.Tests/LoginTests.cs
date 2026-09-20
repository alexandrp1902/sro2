using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Вход в игру (M15.6): корабль появляется в доке того места, где пилот стыковался последний раз, —
/// а не в космосе рядом с ним. Возврат после обрыва связи и появление после гибели этим не затронуты.
/// </summary>
public sealed class LoginTests : IDisposable
{
    private const string Password = "secret";
    private const int JumpTicks = SimConfig.TickRate; // jumpSeconds: 1

    /// <summary>home и port — со станциями, wild между ними — без станции: в ней проснуться негде.</summary>
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
        },
        Links: [new LinkDef("home", "wild", 30), new LinkDef("wild", "port", 20)]);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-login-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private Galaxy _galaxy;
    private int _nextConnection;

    public LoginTests()
    {
        _accounts = new AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _galaxy = New();
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Galaxy New(ReputationRules? reputation = null) =>
        new(TestBalance.Create(
            new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0),
            shop: new ShopRules(StartCredits: 1000),
            reputation: reputation) with { GalaxySet = Rules },
            NullLogger.Instance, _accounts, roll: () => 0);

    private FakeConnection Pilot(string name = "Alice")
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.JoinAccount(connection, login.Id, login.Name);
        return connection;
    }

    private FakeConnection Guest(string name = "Guest")
    {
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.Join(connection, null, name, null);
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

    private void JumpTo(FakeConnection connection, string to)
    {
        var gate = RoomOf(connection).Balance.SystemDef.GateTo(to)!;
        Place(connection, gate.X, gate.Y);
        Do(connection, r => r.Jump(connection, to));
        Steps(JumpTicks);
        Assert.Equal(to, RoomOf(connection).SystemId);
    }

    [Fact]
    public void NewPilot_StartsInTheDock()
    {
        var a = Pilot();

        var hangar = a.Last<HangarMsg>();
        Assert.True(hangar.Docked);
        Assert.Equal("st:home", hangar.Place?.Key);
        // В доке корабля в космосе нет: ни в снапшоте, ни в прицеле.
        Assert.Null(RoomOf(a).Entity(IdOf(a)));
    }

    [Fact]
    public void Guest_StartsInTheDockToo()
    {
        var a = Guest();

        Assert.True(a.Last<HangarMsg>().Docked);
        Assert.Null(RoomOf(a).Entity(IdOf(a)));
    }

    /// <summary>
    /// Главная просьба M15.6: вышел из игры, вернулся — стоишь в доке того места, где стыковался,
    /// а не висишь рядом с ним в космосе.
    /// </summary>
    [Fact]
    public void LoggingBackIn_LandsInTheDockOfTheLastPlace()
    {
        var a = Pilot();
        Do(a, r => r.Dock(a, false));
        JumpTo(a, "wild");
        JumpTo(a, "port");
        Place(a, 0, 50);
        Do(a, r => r.Dock(a, true));
        Assert.Equal("st:port", a.Last<HangarMsg>().Place?.Key);
        _galaxy.Disconnect(a);
        Steps(2 * SimConfig.TickRate); // корабль ждал и был убран

        _galaxy = New();
        var again = Pilot();

        Assert.Equal("port", RoomOf(again).SystemId);
        var hangar = again.Last<HangarMsg>();
        Assert.True(hangar.Docked);
        Assert.Equal("st:port", hangar.Place?.Key);
        Assert.False(again.Last<WelcomeMsg>().Resumed);
        // Витрина и рынок этого места приходят сразу: иначе вкладки дока открылись бы пустыми.
        Assert.Equal("st:port", again.Last<ShopMsg>().Place);
        Assert.Contains(again.Messages, m => m is MarketMsg);
    }

    /// <summary>
    /// Возврат после обрыва связи — не вход: корабль всё это время висел в космосе, там его и надо найти,
    /// с тем же положением и тем же состоянием.
    /// </summary>
    [Fact]
    public void Reconnect_DoesNotPutAFlyingShipIntoTheDock()
    {
        var a = Pilot();
        Do(a, r => r.Dock(a, false));
        Place(a, 700, -400);
        _galaxy.Disconnect(a);
        Steps(10);

        var again = Pilot();

        Assert.True(again.Last<WelcomeMsg>().Resumed);
        Assert.False(again.Last<HangarMsg>().Docked);
        Assert.Equal((700.0, -400.0), (PlayerOf(again).Ship.X, PlayerOf(again).Ship.Y));
    }

    /// <summary>Гибель — по-прежнему появление в космосе у дома: смерть не должна выглядеть удобнее выхода.</summary>
    [Fact]
    public void Death_StillRespawnsInSpace()
    {
        var a = Pilot();
        Do(a, r => r.Dock(a, false));
        PlayerOf(a).Hp = 0;
        Steps(3 * SimConfig.TickRate);

        Assert.False(a.Last<HangarMsg>().Docked);
        Assert.NotNull(RoomOf(a).Entity(IdOf(a)));
        Assert.Equal((SimConfig.SpawnX, SimConfig.SpawnY), (PlayerOf(a).Ship.X, PlayerOf(a).Ship.Y));
    }

    /// <summary>
    /// Док, закрытый для тебя (M13), не должен быть местом, где ты просыпаешься: враг входит в полёте
    /// и слышит, почему.
    /// </summary>
    [Fact]
    public void AnEnemy_EntersFlying_AndIsToldWhy()
    {
        _galaxy = New(TestBalance.Reputation());
        // Профиль врага властей Sol пишем прямо: путь до этой репутации в игре длинный, а предмет теста — вход.
        _accounts.Save(AccountId(), new AccountProfile(
            500, "light", "pulse", ["light"], [], new Dictionary<string, int>(),
            System: "home",
            Reputation: new Dictionary<string, double> { [PlaceKey.System("home")] = -100 },
            RepAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        var a = Pilot();

        Assert.False(a.Last<HangarMsg>().Docked);
        Assert.NotNull(RoomOf(a).Entity(IdOf(a)));
        Assert.Equal(Protocol.DockClosedNotice, a.Last<NoticeMsg>().Code);
    }
}
