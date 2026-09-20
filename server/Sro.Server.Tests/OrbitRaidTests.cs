using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Система со звездой в центре: станция ходит по орбите (с ней — укрытие, стыковка, спаун и дроны), звезда жжёт,
/// пираты прилетают налётами через врата или с базы и уходят обратно.
/// </summary>
public class OrbitRaidTests
{
    private const double StationOrbit = 1000;
    /// <summary>Оборот станции — минута: за тест она заметно уходит по орбите.</summary>
    private const double PeriodMinutes = 1;

    private static readonly NpcType PirateType = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320, RetreatHp: 0.25);

    private static readonly SunDef Sun = new("yellow", Radius: 200, BurnRadius: 600, BurnDps: 100);

    private int _nextConnection;

    private static Room NewRoom(RaidRules? raids = null, IReadOnlyList<DroneSpec>? drones = null, double epoch = 0)
    {
        var system = new SystemDef(
            "Test",
            Pvp: GalaxyRules.PvpOff,
            Drones: drones,
            Gates: [new GateDef("other", 3000, 0)],
            Sun: Sun,
            StationOrbit: new OrbitDef(StationOrbit, PeriodMinutes),
            Pirates: raids);
        var galaxy = new GalaxyRules(
            StartSystem: "test",
            Systems: new Dictionary<string, SystemDef> { ["test"] = system, ["other"] = new("Other", Gates: [new GateDef("test", 0, 3000)]) },
            Links: [new LinkDef("test", "other", 10)]);
        var npcs = new NpcRules(RespawnSeconds: 1, Types: new Dictionary<string, NpcType> { ["pirate"] = PirateType });
        var balance = TestBalance.Create(new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), npcs) with { GalaxySet = galaxy };
        return new Room(balance.ForSystem("test"), NullLogger.Instance, () => 0.999, ai: new Random(3), orbitEpoch: epoch);
    }

    private FakeConnection Connect(Room room)
    {
        var connection = new FakeConnection(++_nextConnection);
        room.Join(connection, null, "Pilot", null);
        room.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private static List<Pirate> Pirates(Room room, FakeConnection observer) =>
        observer.Last<PlayersMsg>().Players.Where(p => p.Kind == Protocol.PirateKind).Select(p => (Pirate)room.Entity(p.Id)!).ToList();

    private static void Steps(Room room, int ticks)
    {
        for (var i = 0; i < ticks; i++) room.Step();
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    [Fact]
    public void Orbit_GoesAroundOncePerPeriod_AndItsLocalFrameLooksAwayFromTheSun()
    {
        var orbit = new OrbitDef(1000, PeriodMinutes: 60, Phase: 90);
        var (x, y) = orbit.At(0);
        Assert.Equal(0, x, 6);
        Assert.Equal(1000, y, 6);
        var quarter = orbit.At(15 * 60);
        Assert.Equal(-1000, quarter.X, 6);
        Assert.Equal(0, quarter.Y, 6);
        Assert.Equal(orbit.At(0).X, orbit.At(3600).X, 6);

        // +y в осях станции — прочь от звезды: спаун (0, 420) всегда дальше от неё, чем станция.
        foreach (var t in new[] { 0.0, 600, 1234, 3000 })
        {
            var spawn = orbit.ToWorld(t, 0, 420);
            Assert.Equal(1420, Math.Sqrt(spawn.X * spawn.X + spawn.Y * spawn.Y), 6);
            var back = orbit.ToLocal(t, spawn.X, spawn.Y);
            Assert.Equal(0, back.X, 6);
            Assert.Equal(420, back.Y, 6);
        }
    }

    [Fact]
    public void Orbit_UsesOnlyTheFractionOfTurns_SoUnixTimeKeepsItsPrecision()
    {
        var orbit = new OrbitDef(1000, PeriodMinutes: 60);
        var now = 1_789_000_000.0;
        var a = orbit.At(now);
        var b = orbit.At(now + 3600 * 1000);
        Assert.Equal(a.X, b.X, 3);
        Assert.Equal(a.Y, b.Y, 3);
    }

    [Fact]
    public void Station_MovesAlongItsOrbit_AndShipsSpawnOnItsOuterSide()
    {
        var room = NewRoom();
        var pilot = Connect(room);
        var station = room.StationPosition;
        Assert.Equal(StationOrbit, Math.Sqrt(station.X * station.X + station.Y * station.Y), 6);
        var ship = room.Entity(IdOf(pilot))!.Ship;
        Assert.Equal(420, Distance((ship.X, ship.Y), station), 6);
        Assert.Equal(StationOrbit + 420, Math.Sqrt(ship.X * ship.X + ship.Y * ship.Y), 6);

        Steps(room, 15 * SimConfig.TickRate); // четверть оборота
        Assert.Equal(StationOrbit * Math.Sqrt(2), Distance(room.StationPosition, station), 3);
    }

    [Fact]
    public void Docking_FollowsTheStation_AndUndockingHappensOnTheSameSideOfIt()
    {
        var room = NewRoom();
        var pilot = Connect(room);
        // Подлетел к станции снаружи орбиты, на 150 от неё.
        var (sx, sy) = room.StationPosition;
        var k = (StationOrbit + 150) / StationOrbit;
        room.Entity(IdOf(pilot))!.Ship = new ShipState { X = sx * k, Y = sy * k };
        room.Dock(pilot, true);
        Assert.True(pilot.Last<HangarMsg>().Docked);

        Steps(room, 20 * SimConfig.TickRate);
        room.Dock(pilot, false);
        var ship = room.Entity(IdOf(pilot))!.Ship;
        // Станция ушла на треть оборота, а корабль вылетел у неё, всё так же снаружи орбиты.
        Assert.Equal(150, Distance((ship.X, ship.Y), room.StationPosition), 6);
        Assert.Equal(StationOrbit + 150, Math.Sqrt(ship.X * ship.X + ship.Y * ship.Y), 6);
    }

    [Fact]
    public void Drones_AreCarriedAlongWithTheStation()
    {
        var room = NewRoom(drones: [new DroneSpec("Мишень", "light", 0, 300)]);
        var pilot = Connect(room);
        var drone = pilot.Last<PlayersMsg>().Players.Single(p => p.Kind == Protocol.DroneKind);
        Steps(room, 20 * SimConfig.TickRate);
        var ship = room.Entity(drone.Id)!.Ship;
        Assert.Equal(300, Distance((ship.X, ship.Y), room.StationPosition), 0);
    }

    [Fact]
    public void Sun_BurnsShieldThenHull_AndKillsWithoutAKiller()
    {
        var room = NewRoom();
        var pilot = Connect(room);
        var ship = room.Entity(IdOf(pilot))!;
        ship.Ship = new ShipState { X = 100, Y = 0 };

        Steps(room, 1);
        Assert.Equal(150 - 100 * SimConfig.Dt, ship.Shield, 6); // у диска — полный жар, сначала щит

        Steps(room, 10 * SimConfig.TickRate);
        var kill = pilot.Messages.OfType<SnapshotMsg>().SelectMany(s => s.Kills ?? []).Single();
        Assert.Equal(new KillDto(IdOf(pilot), 0), kill);
    }

    [Fact]
    public void Sun_DoesNotBurnOutsideItsHeat()
    {
        var sun = new SunDef(Radius: 200, BurnRadius: 600, BurnDps: 100);
        Assert.Equal(100, sun.BurnAt(150));
        Assert.Equal(50, sun.BurnAt(400), 6);
        Assert.Equal(0, sun.BurnAt(600));
        Assert.Equal(0, sun.BurnAt(2000));
    }

    [Fact]
    public void Raiders_StartOnSite_ThenNewOnesArriveThroughAGate()
    {
        var raids = new RaidRules(MaxGroups: 1, IntervalSeconds: 5, PatrolMinSeconds: 2, PatrolMaxSeconds: 2, Groups: [new RaidGroup("pirate", Count: 2)]);
        var room = NewRoom(raids);
        var pilot = Connect(room);
        var first = Pirates(room, pilot);
        Assert.Equal(2, first.Count);
        Assert.All(first, p => Assert.True(p.IsRaider));
        // Точка патруля — вне укрытия станции на всей её орбите и вне жара звезды.
        var home = Math.Sqrt(first[0].HomeX * first[0].HomeX + first[0].HomeY * first[0].HomeY);
        Assert.True(home >= StationOrbit + 900, $"patrol point {home} from the sun");

        // Отпатрулировали, долетели до врат, прыгнули — и исчезли.
        var gone = false;
        for (var i = 0; i < 120 * SimConfig.TickRate && !gone; i++)
        {
            room.Step();
            gone = first.All(p => room.Entity(p.Id) is null);
        }
        Assert.True(gone, "the first raid never left");
        Assert.All(first, p => Assert.True(p.State == PirateState.Leave));

        // Новый налёт появляется у врат и летит к своей точке.
        List<Pirate> next = [];
        for (var i = 0; i < 20 * SimConfig.TickRate && next.Count == 0; i++)
        {
            room.Step();
            next = Pirates(room, pilot);
        }
        Assert.Equal(2, next.Count);
        Assert.All(next, p => Assert.True(Distance((p.Ship.X, p.Ship.Y), (3000, 0)) < 200, $"arrived at {p.Ship.X:0}, {p.Ship.Y:0}"));
        Assert.All(next, p => Assert.Equal(PirateState.Return, p.State));
    }

    [Fact]
    public void LeavingRaider_ChargesAJumpThatClientsSee()
    {
        var raids = new RaidRules(MaxGroups: 1, IntervalSeconds: 60, PatrolMinSeconds: 1, PatrolMaxSeconds: 1, Groups: [new RaidGroup("pirate")]);
        var room = NewRoom(raids);
        var pilot = Connect(room);
        var raider = Pirates(room, pilot).Single();
        for (var i = 0; i < 120 * SimConfig.TickRate && raider.LeaveAtTick == 0; i++) room.Step();
        Assert.True(raider.LeaveAtTick > 0);
        Assert.Equal(raider.LeaveAtTick, pilot.Last<SnapshotMsg>().Ships.Single(s => s.Id == raider.Id).J);
        Assert.Equal("leave", pilot.Last<SnapshotMsg>().Ships.Single(s => s.Id == raider.Id).Ai);
    }

    [Fact]
    public void PirateSystem_RaidersComeFromTheBase()
    {
        var raids = new RaidRules(MaxGroups: 1, IntervalSeconds: 1, PatrolMinSeconds: 1, PatrolMaxSeconds: 1,
            Base: new PirateBase("База", -3000, 0), Groups: [new RaidGroup("pirate")]);
        var room = NewRoom(raids);
        var pilot = Connect(room);
        var first = Pirates(room, pilot).Single();
        Assert.False(first.ExitIsGate);
        Assert.Equal((-3000.0, 0.0), (first.ExitX, first.ExitY));
    }

    [Fact]
    public void DeadRaider_DoesNotRespawn()
    {
        var raids = new RaidRules(MaxGroups: 1, IntervalSeconds: 600, PatrolMinSeconds: 600, PatrolMaxSeconds: 600, Groups: [new RaidGroup("pirate")]);
        var room = NewRoom(raids);
        var pilot = Connect(room);
        var raider = Pirates(room, pilot).Single();
        raider.Hp = 0;
        Steps(room, 3 * SimConfig.TickRate);
        Assert.Null(room.Entity(raider.Id));
        Assert.Empty(Pirates(room, pilot));
    }

    /// <summary>Налётчик в пути: ставит у врат, будто он только прилетел или уже уходит, — и пилот в упор под носом.</summary>
    private (Room Room, FakeConnection Pilot, Pirate Raider) RaiderOnTheWay(Func<Pirate, PirateState> setup)
    {
        var raids = new RaidRules(MaxGroups: 1, IntervalSeconds: 600, PatrolMinSeconds: 600, PatrolMaxSeconds: 600, Groups: [new RaidGroup("pirate")]);
        var room = NewRoom(raids);
        var pilot = Connect(room);
        var raider = Pirates(room, pilot).Single();
        raider.Ship = new ShipState { X = 2600, Y = 0 };
        raider.State = setup(raider);
        var player = room.Pilot(IdOf(pilot))!;
        player.Ship = new ShipState { X = 2600, Y = 300 };
        room.SetTarget(pilot, raider.Id);
        room.SetFire(pilot, true);
        return (room, pilot, raider);
    }

    /// <summary>Плейтест: налётчик, летевший к точке патруля, не отвечал на огонь.</summary>
    [Fact]
    public void ArrivingRaider_FightsBack_ThenFliesOn()
    {
        var (room, pilot, raider) = RaiderOnTheWay(r =>
        {
            r.PatrolUntilTick = 0;
            return PirateState.Return;
        });
        Steps(room, 3);
        Assert.Equal((PirateState.Attack, IdOf(pilot)), (raider.State, raider.TargetId));

        // Пилот ушёл — налётчик летит дальше к своей точке.
        room.SetFire(pilot, false);
        room.Pilot(IdOf(pilot))!.Ship = new ShipState { X = -3500, Y = -3500 };
        Steps(room, 3);
        Assert.Equal(PirateState.Return, raider.State);
    }

    /// <summary>Плейтест: уходящий налётчик не отвечал на огонь.</summary>
    [Fact]
    public void LeavingRaider_FightsBack()
    {
        var (room, pilot, raider) = RaiderOnTheWay(r =>
        {
            r.PatrolUntilTick = 1;
            return PirateState.Leave;
        });
        Steps(room, 3);
        Assert.Equal((PirateState.Attack, IdOf(pilot)), (raider.State, raider.TargetId));
    }

    [Fact]
    public void RetreatingRaider_KeepsFleeing_ButFiresBack()
    {
        var (room, pilot, raider) = RaiderOnTheWay(r =>
        {
            r.PatrolUntilTick = 1;
            r.Hp = 10;
            return PirateState.Leave;
        });
        Steps(room, 3);
        Assert.Equal(PirateState.Leave, raider.State);
        Assert.Equal(IdOf(pilot), raider.TargetId);
        Assert.True(raider.FireHeld);
    }
}
