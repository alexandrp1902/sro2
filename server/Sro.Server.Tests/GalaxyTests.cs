using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Галактика (GDD §4–6, §24, §33–34, §61): гиперпрыжок, топливо, дом пилота, возврат к кораблю в любой системе,
/// PvP по правилам системы, радар.
/// </summary>
public sealed class GalaxyTests : IDisposable
{
    private const string Password = "secret";
    private const string Token = "token-aaaaaaaaaaaaaaaa";
    private const int JumpTicks = SimConfig.TickRate; // jumpSeconds: 1

    /// <summary>home (станция, без PvP) — wild (без станции, PvP везде) — port (станция, PvP вне укрытия).</summary>
    private static readonly GalaxyRules Rules = new(
        FuelPerDistance: 1,
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new("Home", Pvp: GalaxyRules.PvpOff, Gates: [new GateDef("wild", 3000, 0)]),
            ["wild"] = new("Wild", Danger: 4, Pvp: GalaxyRules.PvpFree, Station: false, Seed: 7,
                Gates: [new GateDef("home", -3000, 0), new GateDef("port", 0, 3000)]),
            ["port"] = new("Port", Danger: 2, Pvp: GalaxyRules.PvpBorder, Gates: [new GateDef("wild", 0, -3000)]),
        },
        Links: [new LinkDef("home", "wild", 30), new LinkDef("wild", "port", 20)]);

    private static readonly ShopRules Shop = new(StartCredits: 1000, FuelPrice: 2);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-galaxy-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private Galaxy _galaxy;
    private int _nextConnection;

    public GalaxyTests()
    {
        _accounts = new AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _galaxy = New();
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Galaxy New(GalaxyRules? rules = null, IReadOnlyDictionary<string, HullParams>? hulls = null) =>
        new(TestBalance.Create(new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), shop: Shop) with
        {
            GalaxySet = rules ?? Rules,
            Hulls = hulls ?? TestBalance.Hulls,
        }, NullLogger.Instance, _accounts, roll: () => 0);

    private FakeConnection Guest(string name = "Guest", string? token = null, string? weapon = null)
    {
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.Join(connection, token, name, null, weapon);
        return connection;
    }

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

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private void Do(FakeConnection connection, Action<Room> command) => _galaxy.With(connection, command);

    private void Place(FakeConnection connection, double x, double y) =>
        PlayerOf(connection).Ship = new ShipState { X = x, Y = y };

    /// <summary>Ставит корабль у врат и прыгает до конца.</summary>
    private void JumpTo(FakeConnection connection, string to)
    {
        var gate = RoomOf(connection).Balance.SystemDef.GateTo(to)!;
        Place(connection, gate.X, gate.Y);
        Do(connection, r => r.Jump(connection, to));
        Steps(JumpTicks);
        Assert.Equal(to, RoomOf(connection).SystemId);
    }

    [Fact]
    public void Guest_StartsInTheStartSystem_WithAFullTank()
    {
        var a = Guest();
        var welcome = a.Last<WelcomeMsg>();
        Assert.Equal("home", welcome.System!.Id);
        Assert.Equal("Home", welcome.System.Name);
        Assert.True(welcome.System.Station);
        Assert.Equal(GalaxyRules.PvpOff, welcome.System.Pvp);
        Assert.Equal([new GateDto("wild", "Wild", 3000, 0, 30)], welcome.System.Gates);
        Assert.Equal(3, welcome.Galaxy!.Systems.Count);
        Assert.Contains(new LinkDto("wild", "port", 20), welcome.Galaxy.Links);

        var hangar = a.Last<HangarMsg>();
        Assert.Equal((100, 100, "home"), (hangar.Fuel, hangar.MaxFuel, hangar.Home));
    }

    [Fact]
    public void Jump_ChargesThenMovesTheShipThroughTheGate()
    {
        var a = Guest();
        var b = Guest("Watcher");
        var id = IdOf(a);
        Place(a, 3000, 100);
        Do(a, r => r.Jump(a, "wild"));

        Steps(JumpTicks - 1);
        // Подготовка видна всем: корабль уйдёт в этот тик.
        var charging = b.Last<SnapshotMsg>().Ships.Single(s => s.Id == id);
        Assert.Equal(_galaxy.Tick + 1, charging.J);
        Assert.Equal("home", RoomOf(a).SystemId);

        _galaxy.Step();
        Assert.Equal("wild", RoomOf(a).SystemId);
        var welcome = a.Last<WelcomeMsg>();
        Assert.True(welcome.Resumed);
        Assert.Equal(id, welcome.Id); // id не меняется: он общий на всю галактику
        Assert.Equal("wild", welcome.System!.Id);
        Assert.Equal(70, a.Last<HangarMsg>().Fuel);

        // У ответных врат, на arrivalOffset ближе к центру, стоя на месте и под защитой.
        var ship = PlayerOf(a);
        Assert.Equal((-2750.0, 0.0), (ship.Ship.X, ship.Ship.Y));
        Assert.Equal(0, ship.Ship.Vx);
        Assert.Null(ship.JumpTo);

        // Ростер старой системы его больше не знает, новой — знает; в «онлайн» галактики он есть и в полёте между ними.
        Assert.DoesNotContain(b.Last<PlayersMsg>().Players, p => p.Id == id);
        Assert.All(b.Messages.OfType<PlayersMsg>(), m => Assert.Equal(2, m.Total));
        Assert.Contains(a.Last<PlayersMsg>().Players, p => p.Id == id);
        _galaxy.Step();
        Assert.DoesNotContain(b.Last<SnapshotMsg>().Ships, s => s.Id == id);
        Assert.Contains(a.Last<SnapshotMsg>().Ships, s => s.Id == id);
    }

    /// <summary>
    /// Плейтест M8: после прыжка корабль не слушался, пока клиент не досчитал до старого номера входа. Входы,
    /// отправленные до прыжка, доходят уже в новую систему — поэтому клиент нумерует входы дальше, а не с 1,
    /// и управление работает сразу.
    /// </summary>
    [Fact]
    public void Jump_InputsKeepCounting_AndAStaleOneInFlightDoesNotBlockThem()
    {
        var a = Guest();
        var seq = 0;
        void Fly(double dx, double dy, int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                var input = new MoveInput(dx, dy, 1);
                var n = ++seq;
                Do(a, r => r.Input(a, n, input));
                _galaxy.Step();
            }
        }
        var gate = RoomOf(a).Balance.SystemDef.GateTo("wild")!;
        Place(a, gate.X, gate.Y);
        Do(a, r => r.Jump(a, "wild"));
        Fly(0, -1, JumpTicks); // жмёт вверх, пока идёт подготовка
        Assert.Equal("wild", RoomOf(a).SystemId);
        var arrived = PlayerOf(a).Ship;

        // Вход, отправленный ещё до welcome новой системы, — тоже вверх.
        var stale = ++seq;
        Do(a, r => r.Input(a, stale, new MoveInput(0, -1, 1)));
        // Дальше пилот жмёт вправо.
        Fly(1, 0, 40);

        var ship = PlayerOf(a).Ship;
        Assert.True(ship.X - arrived.X > 100, $"ship did not follow the new inputs: {arrived.X} → {ship.X}");
        Assert.True(Math.Abs(ship.Rot - Math.PI / 2) < 0.1, $"nose should point right, rot {ship.Rot}");
    }

    [Fact]
    public void Jump_FarFromTheGate_SaysSo()
    {
        var a = Guest();
        Place(a, 2500, 0);
        Do(a, r => r.Jump(a, "wild"));
        Assert.Equal(Protocol.GateFarNotice, a.Last<NoticeMsg>().Code);
        Steps(JumpTicks);
        Assert.Equal("home", RoomOf(a).SystemId);
    }

    [Fact]
    public void Jump_WithoutFuel_SaysSo()
    {
        var a = Guest();
        Place(a, 3000, 0);
        PlayerOf(a).Fuel = 29;
        Do(a, r => r.Jump(a, "wild"));
        Assert.Equal(Protocol.NoFuelNotice, a.Last<NoticeMsg>().Code);
        Steps(JumpTicks);
        Assert.Equal("home", RoomOf(a).SystemId);
        Assert.Equal(29, PlayerOf(a).Fuel);
    }

    [Fact]
    public void Jump_WithoutAGate_IsIgnored()
    {
        var a = Guest();
        Place(a, 3000, 0);
        Do(a, r => r.Jump(a, "port")); // в port из home врат нет
        Steps(JumpTicks);
        Assert.Equal("home", RoomOf(a).SystemId);
        Assert.Empty(a.Messages.OfType<NoticeMsg>());
    }

    [Fact]
    public void Jump_CanBeCancelled()
    {
        var a = Guest();
        Place(a, 3000, 0);
        Do(a, r => r.Jump(a, "wild"));
        Steps(5);
        Do(a, r => r.Jump(a, null));
        Steps(JumpTicks);
        Assert.Equal("home", RoomOf(a).SystemId);
        Assert.Equal(100, PlayerOf(a).Fuel);
        Assert.Equal(0, a.Last<SnapshotMsg>().Ships.Single(s => s.Id == IdOf(a)).J);
    }

    [Fact]
    public void Jump_BreaksWhenTheShipLeavesTheGate()
    {
        var a = Guest();
        Place(a, 3000, 0);
        Do(a, r => r.Jump(a, "wild"));
        Steps(5);
        Place(a, 2000, 0);
        _galaxy.Step();
        Assert.Equal(Protocol.JumpCancelledNotice, a.Last<NoticeMsg>().Code);
        Steps(JumpTicks);
        Assert.Equal("home", RoomOf(a).SystemId);
    }

    [Fact]
    public void Death_ReturnsTheShipToTheLastStation()
    {
        var a = Guest();
        JumpTo(a, "wild");
        var player = PlayerOf(a);
        player.Hp = 0;
        Steps(2 * SimConfig.TickRate); // respawnSeconds: 1

        Assert.Equal("home", RoomOf(a).SystemId);
        Assert.Equal("home", a.Last<WelcomeMsg>().System!.Id);
        Assert.False(player.IsDead);
        Assert.Equal(400, player.Hp);
        Assert.Equal((SimConfig.SpawnX, SimConfig.SpawnY), (player.Ship.X, player.Ship.Y));
    }

    [Fact]
    public void Docking_MakesTheStationHome_AndLoginReturnsThere()
    {
        var a = Pilot();
        JumpTo(a, "wild");
        JumpTo(a, "port");
        Place(a, 0, 50);
        Do(a, r => r.Dock(a, true));
        Assert.Equal("port", a.Last<HangarMsg>().Home);

        var profile = _accounts.Profile(AccountId())!;
        Assert.Equal(("port", 50), (profile.System, profile.Fuel));

        // Сервер перезапустили: пилот входит у станции port, с тем же баком.
        _galaxy = New();
        var again = Pilot();
        Assert.Equal("port", RoomOf(again).SystemId);
        Assert.Equal(50, again.Last<HangarMsg>().Fuel);
        Assert.False(again.Last<WelcomeMsg>().Resumed);
    }

    [Fact]
    public void Death_WithoutDocking_ReturnsToTheSystemWhereThePilotEntered()
    {
        var a = Pilot();
        JumpTo(a, "wild");
        JumpTo(a, "port");
        PlayerOf(a).Hp = 0;
        Steps(2 * SimConfig.TickRate);
        Assert.Equal("home", RoomOf(a).SystemId);
    }

    [Fact]
    public void ProfileWithoutFuel_GetsAFullTank()
    {
        var id = AccountId();
        _accounts.Save(id, new AccountProfile(500, "light", "pulse", ["light"], ["pulse"], new Dictionary<string, int>()));
        var a = Pilot();
        Assert.Equal((100, "home"), (a.Last<HangarMsg>().Fuel, a.Last<HangarMsg>().Home));
    }

    [Fact]
    public void ProfileWithAStationlessHome_StartsInTheStartSystem()
    {
        var id = AccountId();
        _accounts.Save(id, new AccountProfile(500, "light", "pulse", ["light"], ["pulse"], new Dictionary<string, int>(), 40, "wild"));
        var a = Pilot();
        Assert.Equal("home", RoomOf(a).SystemId);
        Assert.Equal(40, a.Last<HangarMsg>().Fuel);
    }

    [Fact]
    public void Reconnect_FindsTheShipInAnotherSystem()
    {
        var a = Pilot();
        var id = IdOf(a);
        JumpTo(a, "wild");
        _galaxy.Disconnect(a);
        Steps(10);

        var again = Pilot();
        Assert.Equal("wild", RoomOf(again).SystemId);
        var welcome = again.Last<WelcomeMsg>();
        Assert.True(welcome.Resumed);
        Assert.Equal((id, "wild"), (welcome.Id, welcome.System!.Id));
    }

    [Fact]
    public void SecondDevice_TakesTheShipInAnotherSystem()
    {
        var pc = Pilot();
        JumpTo(pc, "wild");
        var phone = Pilot();
        Assert.Equal(Room.ReplacedCloseCode, pc.ClosedWith);
        Assert.Equal("wild", RoomOf(phone).SystemId);

        // Старое соединение закрывается позже — корабль остаётся у телефона.
        _galaxy.Disconnect(pc);
        _galaxy.Step();
        Assert.NotNull(PlayerOf(phone).Connection);
    }

    [Fact]
    public void GuestSession_ReturnsToItsShipInAnotherSystem()
    {
        var a = Guest(token: Token);
        var id = IdOf(a);
        JumpTo(a, "wild");
        _galaxy.Disconnect(a);
        var again = Guest(token: Token);
        Assert.Equal((id, "wild"), (IdOf(again), RoomOf(again).SystemId));
    }

    [Fact]
    public void Refuel_FillsTheTankForCredits()
    {
        var a = Guest();
        Place(a, 0, 50);
        Do(a, r => r.Dock(a, true));
        PlayerOf(a).Fuel = 40;

        Do(a, r => r.Refuel(a));
        Assert.Equal(100, a.Last<HangarMsg>().Fuel);
        Assert.Equal(1000 - 60 * 2, a.Last<CargoMsg>().Credits);

        PlayerOf(a).Fuel = 0;
        PlayerOf(a).Credits = 10;
        Do(a, r => r.Refuel(a));
        Assert.Equal(Protocol.NoCreditsNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal(0, PlayerOf(a).Fuel);
    }

    [Fact]
    public void Refuel_OnlyInTheDock()
    {
        var a = Guest();
        PlayerOf(a).Fuel = 40;
        Do(a, r => r.Refuel(a));
        Assert.Equal(40, PlayerOf(a).Fuel);
    }

    [Fact]
    public void SmallerTank_CutsTheFuel()
    {
        var a = Guest();
        Place(a, 0, 50);
        Do(a, r => r.Dock(a, true));
        Do(a, r => r.SetHull(a, "heavy"));
        Assert.Equal((100, 300), (a.Last<HangarMsg>().Fuel, a.Last<HangarMsg>().MaxFuel)); // больший бак сам не наполняется
        Do(a, r => r.Refuel(a));
        Do(a, r => r.SetHull(a, "light"));
        Assert.Equal((100, 100), (a.Last<HangarMsg>().Fuel, a.Last<HangarMsg>().MaxFuel));
    }

    [Fact]
    public void Names_AreUniqueAcrossTheGalaxy()
    {
        var a = Guest("Alice");
        JumpTo(a, "wild");
        var b = Guest("Alice");
        Assert.Equal("Alice 2", b.Last<PlayersMsg>().Players.Single(p => p.Id == IdOf(b)).Name);
    }

    [Fact]
    public void Roster_ListsTheSystem_AndCountsTheWholeGalaxy()
    {
        var a = Guest("Alice");
        JumpTo(a, "wild");
        var b = Guest("Bob");
        _galaxy.Step();
        var roster = b.Last<PlayersMsg>();
        Assert.Equal(2, roster.Total);
        Assert.DoesNotContain(roster.Players, p => p.Id == IdOf(a));
        Assert.Equal(2, a.Last<PlayersMsg>().Total); // и в другой системе узнали, что кто-то вошёл
    }

    /// <summary>Два пилота с турелями: a держит огонь по b. Сколько выстрелов за секунду.</summary>
    private int Duel(string pvp, double ax, double ay, double bx, double by)
    {
        var rules = Rules with
        {
            Systems = new Dictionary<string, SystemDef> { ["home"] = new("Home", Pvp: pvp) },
            Links = [],
        };
        _galaxy = New(rules);
        var a = Guest("A", weapon: "turret");
        var b = Guest("B", weapon: "turret");
        Place(a, ax, ay);
        Place(b, bx, by);
        Do(a, r => r.SetTarget(a, IdOf(b)));
        Do(a, r => r.SetFire(a, true));
        var shots = 0;
        for (var i = 0; i < SimConfig.TickRate; i++)
        {
            _galaxy.Step();
            shots += a.Last<SnapshotMsg>().Shots?.Count(s => s.From == IdOf(a)) ?? 0;
        }
        return shots;
    }

    [Fact]
    public void Pvp_Off_NobodyShootsPilots()
    {
        Assert.Equal(0, Duel(GalaxyRules.PvpOff, 0, -2000, 0, -1700));
    }

    [Fact]
    public void Pvp_Free_PilotsShootEvenAtTheStation()
    {
        Assert.True(Duel(GalaxyRules.PvpFree, 0, -2000, 0, -1700) > 0);
        Assert.True(Duel(GalaxyRules.PvpFree, 0, 100, 0, 400) > 0);
    }

    [Fact]
    public void Pvp_Border_NotInsideTheStationShelter()
    {
        var shelter = new NpcRules().StationSafeRadius;
        Assert.True(Duel(GalaxyRules.PvpBorder, 0, -2000, 0, -1700) > 0);
        Assert.Equal(0, Duel(GalaxyRules.PvpBorder, 0, 100, 0, 400));
        // Из укрытия наружу — тоже нет: иначе в укрытии сидели бы снайперы.
        Assert.Equal(0, Duel(GalaxyRules.PvpBorder, 0, -(shelter - 100), 0, -(shelter + 200)));
    }

    [Fact]
    public void Radar_HidesShipsBeyondItsRange()
    {
        var hulls = TestBalance.Hulls.ToDictionary(kv => kv.Key, kv => kv.Value with { Radar = 1000 });
        _galaxy = New(hulls: hulls);
        var a = Guest("A");
        var b = Guest("B");
        Place(a, 0, -2000);
        Place(b, 0, -2000 + 1000 + Room.RadarMargin + 50);
        _galaxy.Step();
        Assert.DoesNotContain(a.Last<SnapshotMsg>().Ships, s => s.Id == IdOf(b));
        Assert.Contains(a.Last<SnapshotMsg>().Ships, s => s.Id == IdOf(a)); // свой — всегда

        Place(b, 0, -2000 + 900);
        _galaxy.Step();
        var seen = a.Last<SnapshotMsg>().Ships.Single(s => s.Id == IdOf(b));
        Assert.Equal(-1100, seen.Y, 3);
        Assert.Equal("light", seen.Hull); // появился целиком, а не одной дельтой

        Place(b, 0, 2000);
        _galaxy.Step();
        Assert.DoesNotContain(a.Last<SnapshotMsg>().Ships, s => s.Id == IdOf(b));
        Assert.Equal(0, a.Snapshots.Desyncs);
    }

    [Fact]
    public void BalanceWithAnotherSetOfSystems_IsNotApplied()
    {
        var a = Guest();
        var before = _galaxy.Balance;
        _galaxy.ApplyBalance(before with { GalaxySet = Rules with { Systems = new Dictionary<string, SystemDef> { ["home"] = new("Home") }, Links = [] } });
        Assert.Same(before, _galaxy.Balance);
        Assert.Equal("home", RoomOf(a).SystemId);
    }

    /// <summary>Под огнём в портал не уйти: попадание сбивает подготовку прыжка.</summary>
    [Fact]
    public void Hit_InterruptsAJump()
    {
        var a = Guest("Runner");
        var b = Guest("Hunter");
        JumpTo(a, "wild");
        JumpTo(b, "wild");
        var gate = RoomOf(a).Balance.SystemDef.GateTo("home")!;
        Place(a, gate.X, gate.Y);
        Place(b, gate.X, gate.Y + 300);
        Do(a, r => r.Jump(a, "home"));
        Assert.NotNull(PlayerOf(a).JumpTo);

        Do(b, r => r.SetTarget(b, IdOf(a)));
        Do(b, r => r.SetFire(b, true));
        Steps(JumpTicks + 2);

        Assert.Equal("wild", RoomOf(a).SystemId);
        Assert.Null(PlayerOf(a).JumpTo);
        Assert.Contains(a.Messages.OfType<NoticeMsg>(), n => n.Code == Protocol.JumpHitNotice);
    }
}
