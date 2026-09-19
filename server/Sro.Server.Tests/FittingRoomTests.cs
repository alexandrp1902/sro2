using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// M9 в комнате: оснащение и склад в доке (GDD §13, §18–20), несколько пушек, ракеты (боевой документ §37),
/// торговцы (GDD §31).
/// </summary>
public sealed class FittingRoomTests : IDisposable
{
    private const string Password = "secret";
    private const double Hit = 0;

    private static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams>
    {
        ["light"] = TestBalance.Hulls["light"] with { Class = EquipClass.S, WeaponSlots = [EquipClass.S, EquipClass.S] },
        ["heavy"] = TestBalance.Hulls["heavy"] with { Class = EquipClass.L, WeaponSlots = [EquipClass.M, EquipClass.S] },
    };

    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["pulse"] = TestBalance.Weapons["turret"] with { Name = "Пульсар", Power = 15 },
        ["plasma"] = TestBalance.Weapons["plasma"] with { Class = EquipClass.M, Power = 30, Arc = 180 },
        ["missiles"] = new("Ракетница", 220, 100, 5, 700, 700, 0, Arc: 90, Kind: WeaponParams.MissileKind, Class: EquipClass.M, Power: 25,
            Missile: new MissileParams(Speed: 330, TurnRate: 120, Lifetime: 3, HitRadius: 10)),
        ["hungry"] = TestBalance.Weapons["turret"] with { Name = "Прожорливая", Power = 60 },
    };

    private static readonly IReadOnlyDictionary<string, ModuleParams> Modules = new Dictionary<string, ModuleParams>
    {
        ["engineS"] = new("Двигатель S", Fitting.EngineSlot, Power: 5),
        ["shieldS"] = new("Щит S", Fitting.ShieldSlot, Power: 10, Shield: 150, ShieldRegen: 20),
        ["shieldL"] = new("Щит L", Fitting.ShieldSlot, EquipClass.L, Power: 30, Shield: 500, ShieldRegen: 50),
        ["radarS"] = new("Радар S", Fitting.RadarSlot, Power: 5, Radar: TestBalance.Radar),
        ["tankS"] = new("Бак S", Fitting.TankSlot, Fuel: 100),
        ["generatorS"] = new("Генератор S", Fitting.GeneratorSlot, Output: 60),
        ["generatorL"] = new("Генератор L", Fitting.GeneratorSlot, EquipClass.L, Output: 200),
    };

    private static readonly ShopRules Shop = new(
        StartCredits: 3000,
        Hulls: new Dictionary<string, int> { ["light"] = 0, ["heavy"] = 900 },
        Items: new Dictionary<string, int> { ["pulse"] = 100, ["plasma"] = 500, ["missiles"] = 700, ["shieldL"] = 400, ["generatorL"] = 600, ["hungry"] = 50 },
        SellShare: 0.5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-fitting-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private Room _room;
    private int _nextConnection;

    public FittingRoomTests()
    {
        _accounts = new AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _room = NewRoom();
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Balance NewBalance(CombatRules? rules = null) =>
        new(Hulls, Weapons, rules ?? new CombatRules(ProtectionSeconds: 0, SpawnJitter: 0), ShopSet: Shop, Modules: Modules);

    private Room NewRoom(Balance? balance = null) => new(balance ?? NewBalance(), NullLogger.Instance, () => Hit, accounts: _accounts);

    private FakeConnection Pilot(string name = "Alice")
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _room.JoinAccount(connection, login.Id, login.Name);
        return connection;
    }

    private FakeConnection Guest(string name = "Guest")
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, null, name, null);
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Player PlayerOf(FakeConnection connection) => _room.Pilot(IdOf(connection))!;

    private Player Docked(FakeConnection connection)
    {
        var player = PlayerOf(connection);
        player.Ship = new ShipState { X = 0, Y = 50 };
        _room.Dock(connection, true);
        Assert.True(connection.Last<HangarMsg>().Docked);
        return player;
    }

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private static List<ShotDto> Shots(FakeConnection observer) =>
        [.. observer.Messages.OfType<SnapshotMsg>().SelectMany(s => s.Shots ?? [])];

    [Fact]
    public void NewPilot_FliesTheStarterKit_WithShieldRadarAndTankFromModules()
    {
        var a = Pilot();
        var hangar = a.Last<HangarMsg>();

        Assert.Equal(["pulse", null], hangar.Fit.Weapons);
        Assert.Equal(("engineS", "shieldS", "radarS", "tankS", "generatorS"), (hangar.Fit.Engine, hangar.Fit.Shield, hangar.Fit.Radar, hangar.Fit.Tank, hangar.Fit.Generator));
        Assert.Equal((35, 60), (hangar.Power, hangar.PowerMax));
        Assert.Equal((100, 100), (hangar.Fuel, hangar.MaxFuel));
        Assert.Equal(150, PlayerOf(a).Shield);
        Assert.NotNull(a.Last<WelcomeMsg>().Modules);
    }

    [Fact]
    public void BuyingWithoutASlot_FillsAFreeSlot_OrGoesToStorage()
    {
        var a = Pilot();
        var player = Docked(a);

        _room.Buy(a, Protocol.ItemKind, "pulse"); // второй слот свободен
        Assert.Equal(["pulse", "pulse"], player.Fit.Weapons);
        _room.Buy(a, Protocol.ItemKind, "pulse"); // слотов больше нет — на склад
        Assert.Equal(1, player.Storage["pulse"]);
        _room.Buy(a, Protocol.ItemKind, "shieldL"); // класс L на лёгкий не встаёт — на склад
        Assert.Equal(("shieldS", 1), (player.Fit.Shield, player.Storage["shieldL"]));
        Assert.Equal(3000 - 100 - 100 - 400, player.Credits);
        Assert.Equal(new Dictionary<string, int> { ["pulse"] = 1, ["shieldL"] = 1 }, a.Last<HangarMsg>().Storage);
    }

    [Fact]
    public void BuyingIntoASlotThatDoesNotFit_TakesNoMoney()
    {
        var a = Pilot();
        var player = Docked(a);

        _room.Buy(a, Protocol.ItemKind, "plasma", "w0"); // M в слот S
        Assert.Equal(Protocol.BadClassNotice, a.Last<NoticeMsg>().Code);
        _room.Buy(a, Protocol.ItemKind, "hungry", "w1"); // 35 + 60 > 60
        Assert.Equal(Protocol.NoPowerNotice, a.Last<NoticeMsg>().Code);

        Assert.Equal(3000, player.Credits);
        Assert.Empty(player.Storage);
    }

    [Fact]
    public void Fitting_SwapsWithTheStorage_AndOnlyInTheDock()
    {
        var a = Pilot();
        var player = Docked(a);
        _room.Buy(a, Protocol.ItemKind, "pulse");
        _room.Buy(a, Protocol.ItemKind, "pulse"); // на склад

        _room.Fit(a, "w1", null); // снять
        Assert.Equal(["pulse", null], player.Fit.Weapons);
        Assert.Equal(2, player.Storage["pulse"]);
        _room.Fit(a, Fitting.EngineSlot, null); // двигатель не снять
        Assert.Equal("engineS", player.Fit.Engine);
        _room.Fit(a, "w1", "plasma"); // на складе нет
        Assert.Null(player.Fit.Weapons[1]);

        _room.Dock(a, false);
        _room.Fit(a, "w1", "pulse"); // в космосе — нельзя
        Assert.Null(player.Fit.Weapons[1]);

        var profile = _accounts.Profile(_accounts.Login("Alice", Password).Id)!;
        Assert.Equal(["pulse", null], profile.Fit!.Weapons);
        Assert.Equal(2, profile.Storage!["pulse"]);
    }

    [Fact]
    public void Selling_FromTheStorage_PaysAShare()
    {
        var a = Pilot();
        var player = Docked(a);
        _room.Buy(a, Protocol.ItemKind, "shieldL");

        _room.SellItem(a, "shieldL");
        _room.SellItem(a, "shieldL"); // больше нет

        Assert.Equal(3000 - 400 + 200, player.Credits);
        Assert.Empty(player.Storage);
    }

    [Fact]
    public void AHeavierHull_TakesTheFit_AndABiggerShieldThenFits()
    {
        var a = Pilot();
        var player = Docked(a);
        _room.Buy(a, Protocol.ItemKind, "shieldL");

        _room.Buy(a, Protocol.HullItem, "heavy");
        _room.Fit(a, Fitting.GeneratorSlot, null); // генератор не снять
        _room.Buy(a, Protocol.ItemKind, "generatorL", Fitting.GeneratorSlot);
        _room.Fit(a, Fitting.ShieldSlot, "shieldL");

        Assert.Equal(("heavy", "shieldL", "generatorL"), (player.HullId, player.Fit.Shield, player.Fit.Generator));
        Assert.Equal(new Dictionary<string, int> { ["shieldS"] = 1, ["generatorS"] = 1 }, player.Storage);
        _room.Repair(a);
        Assert.Equal(500, player.Shield);

        // Обратно на лёгкий: щит и генератор L не встают — на склад, взамен генератора — стартовый.
        _room.SetHull(a, "light");
        Assert.Equal(("light", null, "generatorS"), (player.HullId, player.Fit.Shield, player.Fit.Generator));
        Assert.Equal(1, player.Storage["shieldL"]);
        Assert.Equal(1, player.Storage["generatorL"]);
        Assert.Equal(0, player.Shield);
    }

    [Fact]
    public void OldProfile_GetsItsGunInTheFirstSlot_AndTheRestInStorage()
    {
        var id = _accounts.Login("Alice", Password).Id;
        _accounts.Save(id, new AccountProfile(50, "light", "plasma", ["light"], ["pulse", "plasma", "ghost"], new Dictionary<string, int>()));

        var a = Pilot();
        var player = PlayerOf(a);

        // Плазма M в слот S не встаёт — на склад, как и остальные купленные; неизвестная пропала.
        Assert.Equal([null, null], player.Fit.Weapons);
        Assert.Equal(new Dictionary<string, int> { ["pulse"] = 1, ["plasma"] = 1 }, player.Storage);
        Assert.Equal("engineS", player.Fit.Engine);
        Assert.NotNull(_accounts.Profile(id)!.Fit); // профиль сразу переписан в новом виде
    }

    [Fact]
    public void TwoGuns_FireEachOnItsOwnCooldown()
    {
        var a = Guest("A");
        var b = Guest("B");
        _room.Fit(a, "w1", "pulse");
        PlayerOf(a).Ship = new ShipState { X = 0, Y = 0 };
        PlayerOf(b).Ship = new ShipState { X = 0, Y = -300 };
        _room.SetTarget(a, IdOf(b));
        _room.SetFire(a, true);

        Steps(21);

        var shots = Shots(b).Where(s => s.From == IdOf(a)).ToList();
        Assert.Equal(4, shots.Count); // по два в тиках 1 и 21
    }

    [Fact]
    public void Missile_FliesToTheTarget_AndHitsIt()
    {
        _room = NewRoom(NewBalance() with { Hulls = new Dictionary<string, HullParams>(Hulls) { ["light"] = Hulls["heavy"] } });
        var a = Guest("A");
        var b = Guest("B");
        _room.Fit(a, "w0", "missiles");
        PlayerOf(a).Ship = new ShipState { X = 0, Y = 0 };
        PlayerOf(b).Ship = new ShipState { X = 0, Y = -600 };
        _room.SetTarget(a, IdOf(b));
        _room.SetFire(a, true);

        Steps(1);
        Assert.Single(_room.Missiles);
        Assert.Contains(b.Last<SnapshotMsg>().Missiles!, m => m.O == IdOf(a) && m.T == IdOf(b) && m.W == "missiles");
        Assert.Empty(Shots(b)); // ракета не бросает кубик при пуске

        Steps(40); // 600 на скорости 330 — меньше двух секунд
        var hit = Assert.Single(Shots(b).Where(s => s.W == "missiles"));
        Assert.True(hit.Hit);
        Assert.Equal(220, hit.Dmg);
        Assert.Empty(_room.Missiles);
    }

    [Fact]
    public void Missile_Fizzles_WhenTheTargetLeaves()
    {
        _room = NewRoom(NewBalance() with { Hulls = new Dictionary<string, HullParams>(Hulls) { ["light"] = Hulls["heavy"] } });
        var a = Guest("A");
        var b = Guest("B");
        _room.Fit(a, "w0", "missiles");
        PlayerOf(a).Ship = new ShipState { X = 0, Y = 0 };
        PlayerOf(b).Ship = new ShipState { X = 0, Y = -690 };
        _room.SetTarget(a, IdOf(b));
        _room.SetFire(a, true);
        Steps(1);
        _room.SetFire(a, false);
        Assert.Single(_room.Missiles);

        _room.Disconnect(b); // гость без сессии уходит из системы сразу
        Steps(1);

        Assert.Empty(_room.Missiles);
        Assert.DoesNotContain(Shots(a), s => s.W == "missiles");
    }

    [Fact]
    public void Missile_Expires_AfterItsLifetime()
    {
        var p = Weapons["missiles"].Missile!;
        var missiles = new MissileSystem(() => 99);
        var shooter = new Drone(1, new DroneSpec("A", "light", 0, 0));
        var target = new Drone(2, new DroneSpec("B", "light", 0, -5000)) { Ship = new ShipState { Y = -5000 } };
        var ships = new Dictionary<int, ShipEntity> { [1] = shooter, [2] = target };
        shooter.WeaponIds = ["missiles"];
        var shots = new List<ShotDto>();

        missiles.Launch(shooter, target, 0, Weapons["missiles"], tick: 0);
        for (var tick = 1; tick < p.LifetimeTicks; tick++) missiles.Step(tick, ships, NewBalance(), shots);
        Assert.Single(missiles.Alive);
        missiles.Step(p.LifetimeTicks, ships, NewBalance(), shots);

        Assert.Empty(missiles.Alive);
        Assert.Empty(shots);
    }

    [Fact]
    public void Traders_FlyBetweenTheStationAndAGate_AndDropCargo()
    {
        var trader = new NpcType("Торговец", "heavy", Hp: 50, Shield: 0, Table: "trader");
        var loot = new LootRules(
            Items: new Dictionary<string, LootItem> { ["metal"] = new("Металл", Volume: 1, Price: 10) },
            Tables: new Dictionary<string, LootTable> { ["trader"] = new([new LootRoll("metal", 1, 3, 3)]) });
        var system = new SystemDef("Test", Gates: [new GateDef("other", 2500, 0)], Traders: new TraderRules(Count: 1, RespawnSeconds: 1));
        var galaxy = new GalaxyRules(
            StartSystem: "test",
            Systems: new Dictionary<string, SystemDef> { ["test"] = system, ["other"] = new("Other", Gates: [new GateDef("test", 0, 3000)]) },
            Links: [new LinkDef("test", "other", 10)]);
        var npcs = new NpcRules(Types: new Dictionary<string, NpcType> { ["trader"] = trader });
        _room = NewRoom((NewBalance() with { Npcs = npcs, Loots = loot, GalaxySet = galaxy }).ForSystem("test"));
        var a = Guest();

        var merchant = Assert.Single(_room.Traders);
        Assert.Contains(a.Last<PlayersMsg>().Players, p => p.Id == merchant.Id && p.Kind == Protocol.TraderKind && p.MaxHp == 50);
        var start = (merchant.Ship.X, merchant.Ship.Y);
        Steps(40);
        Assert.NotEqual(start, (merchant.Ship.X, merchant.Ship.Y)); // летит

        // Грабёж: торговец без пушек, пилот бьёт его даже в системе без PvP.
        PlayerOf(a).Ship = new ShipState { X = merchant.Ship.X, Y = merchant.Ship.Y + 300 };
        _room.SetTarget(a, merchant.Id);
        _room.SetFire(a, true);
        Steps(2);

        Assert.Contains(a.Messages.OfType<SnapshotMsg>(), s => (s.Kills ?? []).Any(k => k.Id == merchant.Id && k.By == IdOf(a)));
        Assert.Empty(_room.Traders);
        Steps(1);
        Assert.Contains(a.Last<SnapshotMsg>().Loot ?? [], l => l.I == "metal" && l.N == 3);

        Steps(SimConfig.TickRate + 1); // через respawnSeconds — новый
        Assert.Single(_room.Traders);
    }

    [Fact]
    public void Pirates_HuntTraders()
    {
        var trader = new NpcType("Торговец", "heavy", Hp: 500, Shield: 0);
        var pirate = new NpcType("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.5);
        var system = new SystemDef(
            "Test",
            Gates: [new GateDef("other", 3000, 0)],
            Spawns: [new NpcSpawn("pirate", 1, -2500, -2500)],
            Traders: new TraderRules(Count: 1));
        var galaxy = new GalaxyRules(
            StartSystem: "test",
            Systems: new Dictionary<string, SystemDef> { ["test"] = system, ["other"] = new("Other", Gates: [new GateDef("test", 0, 3000)]) },
            Links: [new LinkDef("test", "other", 10)]);
        var npcs = new NpcRules(Types: new Dictionary<string, NpcType> { ["pirate"] = pirate, ["trader"] = trader });
        _room = NewRoom((NewBalance() with { Npcs = npcs, GalaxySet = galaxy }).ForSystem("test"));
        var observer = Guest();
        var merchant = Assert.Single(_room.Traders);
        merchant.Ship = new ShipState { X = -2500, Y = -2100 }; // рядом с логовом

        Steps(60);

        var raider = (Pirate)_room.Entity(observer.Last<PlayersMsg>().Players.Single(p => p.Kind == Protocol.PirateKind).Id)!;
        Assert.Equal(merchant.Id, raider.TargetId);
        Assert.Contains(Shots(observer), s => s.From == raider.Id && s.To == merchant.Id);
        Assert.True(merchant.Fleeing); // под огнём — на полной тяге
    }
}
