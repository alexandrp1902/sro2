using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>Станция и экономика (GDD §26, §50–51, §54): док, магазин, ангар, ремонт, сохранение пилота.</summary>
public sealed class StationTests : IDisposable
{
    private const string Password = "secret";

    private static readonly ShopRules Shop = new(
        StartCredits: 1000,
        RepairPrice: 1,
        Hulls: new Dictionary<string, int> { ["light"] = 0, ["heavy"] = 900 },
        Weapons: new Dictionary<string, int> { ["pulse"] = 0, ["laser"] = 300, ["plasma"] = 5000 });

    private static readonly LootRules Loot = new(
        Items: new Dictionary<string, LootItem> { ["metal"] = new("Металл", Volume: 1, Price: 10) });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-station-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly Room _room;
    private readonly Dictionary<FakeConnection, string> _ids = [];
    private int _nextConnection;

    public StationTests()
    {
        _accounts = new AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _room = new Room(
            TestBalance.Create(new CombatRules(ProtectionSeconds: 3, SpawnJitter: 0), loot: Loot, shop: Shop),
            NullLogger.Instance,
            accounts: _accounts);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private FakeConnection Pilot(string name = "Alice")
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _room.JoinAccount(connection, login.Id, login.Name);
        _ids[connection] = login.Id;
        return connection;
    }

    private FakeConnection Guest()
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, null, "Guest", null);
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Player PlayerOf(FakeConnection connection) => _room.Pilot(IdOf(connection))!;

    private string AccountOf(FakeConnection connection) => _ids[connection];

    /// <summary>Ставит корабль в центр станции и стыкует.</summary>
    private Player Docked(FakeConnection connection)
    {
        var player = PlayerOf(connection);
        player.Ship = new ShipState { X = 0, Y = 50, Vx = 30 };
        _room.Dock(connection, true);
        Assert.True(connection.Last<HangarMsg>().Docked);
        return player;
    }

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    [Fact]
    public void NewPilot_GetsStartCredits_AndTheStarterShip()
    {
        var a = Pilot();

        Assert.Equal(1000, a.Last<CargoMsg>().Credits);
        var hangar = a.Last<HangarMsg>();
        Assert.Equal(("light", "pulse", false), (hangar.Hull, hangar.Weapon, hangar.Docked));
        Assert.Equal(["light"], hangar.Hulls);
        Assert.Equal(["pulse"], hangar.Weapons);
        Assert.NotNull(a.Last<WelcomeMsg>().Shop);
    }

    [Fact]
    public void DockingFarFromTheStation_SaysSo()
    {
        var a = Pilot();
        PlayerOf(a).Ship = new ShipState { X = 0, Y = 400 };

        _room.Dock(a, true);

        Assert.Equal(Protocol.TooFarNotice, a.Last<NoticeMsg>().Code);
        Assert.False(PlayerOf(a).Docked);
    }

    [Fact]
    public void DockedShip_LeavesSpace_ButStaysInTheRoster()
    {
        var a = Pilot();
        var b = Guest();
        var id = IdOf(a);
        _room.SetTarget(b, id);

        var player = Docked(a);
        Steps(1);

        Assert.Null(_room.Entity(id));
        Assert.DoesNotContain(b.Last<SnapshotMsg>().Ships, s => s.Id == id);
        Assert.Contains(b.Last<PlayersMsg>().Players, p => p.Id == id && p.Online);
        Assert.Equal(0, ((Player)_room.Entity(IdOf(b))!).TargetId); // прицел на ушедшего в док снят
        Assert.Equal(0, player.Ship.Vx); // в доке стоят

        _room.SetTarget(b, id);
        Assert.Equal(0, ((Player)_room.Entity(IdOf(b))!).TargetId); // и снова навестись нельзя
    }

    [Fact]
    public void Undocking_ReturnsTheShipWhereItDocked_WithProtection()
    {
        var a = Pilot();
        var player = Docked(a);
        Steps(10);

        _room.Dock(a, false);

        Assert.Same(player, _room.Entity(IdOf(a)));
        Assert.False(a.Last<HangarMsg>().Docked);
        Assert.Equal((0, 50), (player.Ship.X, player.Ship.Y));
        Assert.True(player.IsProtected(_room.Tick));

        // Входы после вылета нумеруются заново — сервер их принимает.
        _room.Input(a, 1, new MoveInput(0, -1, 1));
        _room.Input(a, 2, new MoveInput(0, -1, 1));
        Steps(3);
        Assert.True(player.Ship.Y < 50);
    }

    [Fact]
    public void Buying_WithoutEnoughCredits_SaysSo()
    {
        var a = Pilot();
        var player = Docked(a);

        _room.Buy(a, Protocol.WeaponItem, "plasma");

        Assert.Equal(Protocol.NoCreditsNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal(("pulse", 1000), (player.WeaponId, player.Credits));
    }

    [Fact]
    public void Buying_PaysEquipsAndSaves()
    {
        var a = Pilot();
        var player = Docked(a);

        _room.Buy(a, Protocol.WeaponItem, "laser");

        Assert.Equal(("laser", 700), (player.WeaponId, player.Credits));
        Assert.Equal(700, a.Last<CargoMsg>().Credits);
        Assert.Equal(["laser", "pulse"], a.Last<HangarMsg>().Weapons);
        var profile = _accounts.Profile(AccountOf(a))!;
        Assert.Equal(("laser", 700), (profile.Weapon, profile.Credits));

        _room.Buy(a, Protocol.WeaponItem, "laser"); // уже куплена — второй раз не списывается
        Assert.Equal(700, player.Credits);
    }

    [Fact]
    public void Buying_OutsideTheDock_DoesNothing()
    {
        var a = Pilot();

        _room.Buy(a, Protocol.WeaponItem, "laser");

        Assert.Equal(("pulse", 1000), (PlayerOf(a).WeaponId, PlayerOf(a).Credits));
    }

    [Fact]
    public void NotForSale_CannotBeBought()
    {
        var a = Pilot();
        var player = Docked(a);

        _room.Buy(a, Protocol.WeaponItem, "doom"); // есть в weapons, нет в прайсе
        _room.Buy(a, "cloak", "laser");

        Assert.Equal(("pulse", 1000), (player.WeaponId, player.Credits));
    }

    [Fact]
    public void Pilot_SwitchesOnlyOwnedShips_AndOnlyInTheDock()
    {
        var a = Pilot();
        _room.SetHull(a, "heavy"); // не куплен
        Assert.Equal("light", PlayerOf(a).HullId);

        var player = Docked(a);
        _room.Buy(a, Protocol.HullItem, "heavy");
        Assert.Equal("heavy", player.HullId);
        _room.Dock(a, false);

        _room.SetHull(a, "light"); // свой, но в космосе
        Assert.Equal("heavy", player.HullId);

        Docked(a);
        _room.SetHull(a, "light");
        Assert.Equal("light", player.HullId);
        Assert.Equal("light", _accounts.Profile(AccountOf(a))!.Hull);
    }

    [Fact]
    public void Guest_SwitchesAnything_Anywhere()
    {
        var g = Guest();

        _room.SetHull(g, "heavy");
        _room.SetWeapon(g, "plasma");

        Assert.Equal(("heavy", "plasma"), (PlayerOf(g).HullId, PlayerOf(g).WeaponId));
    }

    [Fact]
    public void Repair_ChargesPerPointOfHull()
    {
        var a = Pilot();
        var player = PlayerOf(a);
        player.Hp -= 100.5;
        player.Shield = 0;
        Docked(a);

        _room.Repair(a);

        Assert.Equal(899, player.Credits); // 100.5 × 1, округлено вверх
        Assert.Equal((400, 150), (player.Hp, player.Shield));
        Assert.Equal(400, a.Last<HangarMsg>().Hp);
    }

    [Fact]
    public void Repair_WithoutCredits_SaysSo()
    {
        var a = Pilot();
        var player = PlayerOf(a);
        player.Credits = 10;
        player.Hp = 1;
        Docked(a);

        _room.Repair(a);

        Assert.Equal(Protocol.NoCreditsNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal(1, player.Hp);
    }

    [Fact]
    public void Pilot_ComesBackWithCreditsShipsAndCargo()
    {
        var a = Pilot();
        var player = Docked(a);
        _room.Buy(a, Protocol.HullItem, "heavy");
        player.Cargo.Add("metal", 4);
        _room.Sell(a, null);
        player.Cargo.Add("metal", 2);
        _room.Dock(a, false);
        _room.Disconnect(a);
        Steps(Room.ReconnectGraceTicks + 1); // корабль ждал, не дождался и ушёл — состояние записано в аккаунт

        var back = Pilot();

        var again = PlayerOf(back);
        Assert.NotSame(player, again);
        Assert.Equal(("heavy", 140), (again.HullId, again.Credits));
        Assert.Equal(2, again.Cargo.Items["metal"]);
        Assert.Equal(["heavy", "light"], back.Last<HangarMsg>().Hulls);
    }

    [Fact]
    public void SecondDevice_TakesTheShip_AndTheFirstIsClosed()
    {
        var phone = Pilot();
        var id = IdOf(phone);

        var pc = Pilot();

        Assert.Equal(Room.ReplacedCloseCode, phone.ClosedWith);
        Assert.Equal(id, IdOf(pc));
        Assert.True(pc.Last<WelcomeMsg>().Resumed);
        Assert.Equal(1, _room.Count);
    }

    [Fact]
    public void ComingBackToADockedShip_OpensTheDock()
    {
        var a = Pilot();
        Docked(a);
        _room.Disconnect(a);
        Steps(20);

        var back = Pilot();

        Assert.True(back.Last<HangarMsg>().Docked);
        Assert.Null(_room.Entity(IdOf(back)));
    }

    [Fact]
    public void Guest_CannotTakeAnAccountShip_ByItsId()
    {
        var a = Pilot();
        var intruder = new FakeConnection(++_nextConnection);

        _room.Join(intruder, AccountOf(a), "Mallory", null);

        Assert.NotEqual(IdOf(a), IdOf(intruder));
        Assert.Null(a.ClosedWith);
    }

    [Fact]
    public void ShipRemovedFromBalance_FallsBackToTheStarter()
    {
        var id = _accounts.Login("Alice", Password).Id;
        _accounts.Save(id, new AccountProfile(50, "ghost", "laser", ["ghost"], ["laser"], new Dictionary<string, int>()));

        var a = Pilot();

        Assert.Equal(("light", "laser", 50), (PlayerOf(a).HullId, PlayerOf(a).WeaponId, PlayerOf(a).Credits));
        Assert.Equal(["light"], a.Last<HangarMsg>().Hulls);
    }
}
