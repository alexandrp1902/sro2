using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

public class CombatRoomTests
{
    private const double Hit = 0;
    private const double Miss = 0.999;

    /// <summary>Респаун 2 с, без защиты после появления, щит восстанавливается через 1 с.</summary>
    private static readonly CombatRules Rules = new(RespawnSeconds: 2, ProtectionSeconds: 0, ShieldRegenDelay: 1, SpawnJitter: 0);

    private double _roll = Hit;
    private Room _room;
    private int _nextConnection;

    public CombatRoomTests() => _room = NewRoom(Rules);

    private Room NewRoom(CombatRules rules) => new(TestBalance.Create(rules), NullLogger.Instance, () => _roll);

    private FakeConnection Connect(string? token = null, string name = "Pilot", string? weapon = null)
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, token, name, null, weapon);
        _room.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private ShipEntity Ship(FakeConnection connection) => _room.Entity(IdOf(connection))!;

    private void Place(FakeConnection connection, double x, double y, double rotDeg = 0) =>
        Ship(connection).Ship = new ShipState { X = x, Y = y, Rot = rotDeg * Math.PI / 180 };

    /// <summary>A в начале координат носом вверх, B в 300 впереди: в секторе и в оптимальной дальности.</summary>
    private (FakeConnection A, FakeConnection B) Duel(string? weapon = null)
    {
        var a = Connect(name: "A", weapon: weapon);
        var b = Connect(name: "B");
        Place(a, 0, 0);
        Place(b, 0, -300);
        return (a, b);
    }

    private void Attack(FakeConnection attacker, FakeConnection target)
    {
        _room.SetTarget(attacker, IdOf(target));
        _room.SetFire(attacker, true);
    }

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private static List<(long Tick, ShotDto Shot)> Shots(FakeConnection observer) =>
        observer.Messages.OfType<SnapshotMsg>().SelectMany(s => (s.Shots ?? []).Select(shot => (s.Tick, shot))).ToList();

    private static ShipDto Dto(FakeConnection observer, int id) => observer.Last<SnapshotMsg>().Ships.Single(s => s.Id == id);

    private static ShipDto Dto(FakeConnection observer, FakeConnection ship) => Dto(observer, IdOf(ship));

    [Fact]
    public void HoldingFire_ShootsAtTheWeaponCooldown()
    {
        var (a, b) = Duel();
        Attack(a, b);
        Steps(60);

        var shots = Shots(b);
        Assert.Equal(new long[] { 1, 21, 41 }, shots.Select(s => s.Tick));
        Assert.All(shots, s => Assert.Equal((IdOf(a), IdOf(b), "pulse"), (s.Shot.From, s.Shot.To, s.Shot.W)));
    }

    [Fact]
    public void Laser_ShootsTwiceAsOften()
    {
        var (a, b) = Duel("laser");
        Attack(a, b);
        Steps(40);
        Assert.Equal(4, Shots(b).Count);
    }

    [Theory]
    [InlineData(0, 300)] // позади
    [InlineData(300, 0)] // сбоку, 90° от носа
    [InlineData(0, -701)] // дальше maxRange
    public void TargetOutsideArcOrRange_IsNotShot(double x, double y)
    {
        var (a, b) = Duel();
        Place(b, x, y);
        Attack(a, b);
        Steps(30);
        Assert.Empty(Shots(a));
    }

    [Theory]
    [InlineData(0, 300)] // позади
    [InlineData(300, 0)] // сбоку
    public void AllAroundWeapon_ShootsBehindAndSideways(double x, double y)
    {
        var (a, b) = Duel("turret");
        Place(b, x, y);
        Attack(a, b);
        Steps(1);
        Assert.Single(Shots(a));
    }

    [Fact]
    public void Damage_TakesShieldFirstThenHull()
    {
        var (a, b) = Duel();
        Attack(a, b);

        Steps(1);
        var first = Shots(b).Single().Shot;
        Assert.Equal((true, 100, 100, 50.0), (first.Hit, first.Dmg, first.Sh, first.Ch));
        Assert.Equal((400, 50), (Dto(a, b).Hp, Dto(a, b).Sh));

        Steps(20);
        var second = Shots(b).Last().Shot;
        Assert.Equal((100, 50), (second.Dmg, second.Sh));
        Assert.Equal((350, 0), (Dto(a, b).Hp, Dto(a, b).Sh));
    }

    [Fact]
    public void Miss_DealsNoDamage()
    {
        _roll = Miss;
        var (a, b) = Duel();
        Attack(a, b);
        Steps(1);

        var shot = Shots(b).Single().Shot;
        Assert.Equal((false, 0), (shot.Hit, shot.Dmg));
        Assert.Equal((400, 150), (Dto(a, b).Hp, Dto(a, b).Sh));
    }

    [Fact]
    public void Shield_RegeneratesOnlyAfterTheDelay()
    {
        var (a, b) = Duel();
        Attack(a, b);
        Steps(1);
        _room.SetFire(a, false);

        Steps(19); // с попадания прошло 19 тиков из 20
        Assert.Equal(50, Dto(a, b).Sh);
        Steps(1);
        Assert.Equal(51, Dto(a, b).Sh); // 20 ед/с — единица за тик
        Steps(200);
        Assert.Equal(150, Dto(a, b).Sh);
    }

    [Fact]
    public void Kill_MarksTheShipDestroyed_ShooterKeepsTheTarget()
    {
        var (a, b) = Duel("doom");
        Attack(a, b);
        Steps(1);

        var snapshot = a.Last<SnapshotMsg>();
        Assert.Equal(new KillDto(IdOf(b), IdOf(a)), Assert.Single(snapshot.Kills!));
        var wreck = Dto(a, b);
        Assert.Equal(0, wreck.Hp);
        Assert.Equal(snapshot.Tick + Rules.RespawnTicks, wreck.Rt);
        Assert.Equal(IdOf(b), Ship(a).TargetId);

        Steps(30);
        Assert.Single(Shots(a)); // по уничтоженному не стреляют
    }

    [Fact]
    public void BothShipsCanDestroyEachOtherInOneTick()
    {
        var a = Connect(name: "A", weapon: "doom");
        var b = Connect(name: "B", weapon: "doom");
        Place(a, 0, 0);
        Place(b, 0, -300, 180); // носом к A
        Attack(a, b);
        Attack(b, a);
        Steps(1);

        Assert.Equal(2, a.Last<SnapshotMsg>().Kills!.Count);
        Assert.True(Dto(a, a).Rt > 0 && Dto(a, b).Rt > 0);
    }

    [Fact]
    public void DestroyedShip_ConsumesInputsWithoutMoving()
    {
        var (a, b) = Duel("doom");
        Attack(a, b);
        Steps(1);
        _room.SetFire(a, false);
        var wreck = Dto(a, b);

        for (var seq = 1; seq <= 20; seq++)
        {
            _room.Input(b, seq, new MoveInput(0, -1, 1));
            _room.Step();
        }

        var still = Dto(a, b);
        Assert.Equal((wreck.X, wreck.Y), (still.X, still.Y));
        Assert.True(still.Ack > 10);
        Assert.Equal(0, still.Th);
    }

    [Fact]
    public void Respawn_RestoresTheShipAtSpawnWithProtection()
    {
        var rules = Rules with { ProtectionSeconds = 10 };
        _room = NewRoom(rules);
        var (a, b) = Duel("doom");
        Steps(rules.ProtectionTicks); // защита после входа кончилась
        Attack(a, b);
        Steps(1);
        _room.SetFire(a, false);
        Assert.True(Dto(a, b).Rt > 0);

        Steps(rules.RespawnTicks - 1);
        Assert.True(Dto(a, b).Rt > 0);
        Steps(1);

        var back = Dto(a, b);
        Assert.Equal(0, back.Rt);
        Assert.Equal((SimConfig.SpawnX, SimConfig.SpawnY), (back.X, back.Y));
        Assert.Equal((400, 150), (back.Hp, back.Sh));
        Assert.Equal(a.Last<SnapshotMsg>().Tick + rules.ProtectionTicks, back.Pu);
    }

    [Fact]
    public void Protection_BlocksShotsUntilItEnds()
    {
        var rules = Rules with { ProtectionSeconds = 1 };
        _room = NewRoom(rules);
        var (a, b) = Duel();
        Attack(a, b);

        Steps(rules.ProtectionTicks - 1);
        Assert.Empty(Shots(a));
        Steps(2);
        Assert.Single(Shots(a));
    }

    [Fact]
    public void Firing_RemovesOwnProtection()
    {
        var rules = Rules with { ProtectionSeconds = 1 };
        _room = NewRoom(rules);
        var (a, b) = Duel();
        Steps(rules.ProtectionTicks); // у A и B защиты больше нет

        var c = Connect(name: "C");
        Place(c, 0, 0);
        Steps(1);
        Assert.True(Dto(a, c).Pu > 0);

        Attack(c, b);
        Steps(1);
        Assert.Single(Shots(a));
        Assert.Equal(0, Dto(a, c).Pu);
    }

    [Fact]
    public void LostConnection_StopsFiring_ButTheShipCanStillBeShot()
    {
        var a = Connect("token-aaaaaaaaaaaaaaaa", "A");
        var b = Connect(name: "B");
        Place(a, 0, 0);
        Place(b, 0, -300, 180);
        Attack(a, b);
        Attack(b, a);

        _room.Disconnect(a);
        Assert.False(Ship(a).FireHeld);
        Steps(1);

        Assert.Equal(IdOf(b), Assert.Single(Shots(b)).Shot.From);
        Assert.Equal(50, Dto(b, a).Sh);
    }

    [Fact]
    public void HullSwitch_KeepsHullAndShieldShares()
    {
        var (a, b) = Duel();
        Attack(a, b);
        Steps(21); // два попадания: щит 0, корпус 350 из 400
        _room.SetFire(a, false);

        _room.SetHull(b, "heavy");
        Assert.Equal(1800 * 350 / 400.0, Ship(b).Hp, 9);
        Assert.Equal(0, Ship(b).Shield);
    }

    [Fact]
    public void Weapon_CanBeSwitched()
    {
        var (a, b) = Duel();
        _room.SetWeapon(a, "laser");
        _room.SetWeapon(a, "nope");
        Attack(a, b);
        Steps(1);
        Assert.Equal("laser", Shots(b).Single().Shot.W);
    }

    [Fact]
    public void Target_CannotBeSelfOrMissing_AndIsClearedWhenTheShipLeaves()
    {
        var a = Connect(name: "A");
        var b = Connect(name: "B"); // без сессии — при обрыве корабль исчезает сразу

        _room.SetTarget(a, IdOf(a));
        Assert.Equal(0, Ship(a).TargetId);
        _room.SetTarget(a, 999);
        Assert.Equal(0, Ship(a).TargetId);
        _room.SetTarget(a, IdOf(b));
        Assert.Equal(IdOf(b), Ship(a).TargetId);

        _room.Disconnect(b);
        Assert.Equal(0, Ship(a).TargetId);
    }

    [Fact]
    public void Drone_IsInRosterAndSnapshot_AndRespawnsAtHome()
    {
        _room = NewRoom(Rules with { Drones = [new DroneSpec("Дрон", "light", 0, -300, Hp: 100, Shield: 0)] });
        var a = Connect(name: "A");
        var drone = Assert.Single(a.Last<PlayersMsg>().Players, p => p.Npc);
        Assert.Equal("Дрон", drone.Name);
        Place(a, 0, 0);

        _room.SetTarget(a, drone.Id);
        _room.SetFire(a, true);
        Steps(1);
        Assert.Equal(new KillDto(drone.Id, IdOf(a)), Assert.Single(a.Last<SnapshotMsg>().Kills!));
        _room.SetFire(a, false);

        Steps(Rules.RespawnTicks);
        var back = Dto(a, drone.Id);
        Assert.Equal((0.0, -300.0, 100, 0L, 0L), (back.X, back.Y, back.Hp, back.Rt, back.Pu));
    }

    [Fact]
    public void OrbitingDrone_CirclesAroundHome()
    {
        _room = NewRoom(Rules with { Drones = [new DroneSpec("Мишень", "light", 1000, 1000, OrbitRadius: 250)] });
        var a = Connect();
        var id = a.Last<PlayersMsg>().Players.Single(p => p.Npc).Id;

        var distances = new List<double>();
        var speeds = new List<double>();
        for (var i = 0; i < 600; i++)
        {
            _room.Step();
            if (i < 200) continue;
            var s = Dto(a, id);
            distances.Add(Math.Sqrt((s.X - 1000) * (s.X - 1000) + (s.Y - 1000) * (s.Y - 1000)));
            speeds.Add(Math.Sqrt(s.Vx * s.Vx + s.Vy * s.Vy));
        }

        Assert.InRange(distances.Min(), 150, 350);
        Assert.InRange(distances.Max(), 150, 350);
        Assert.True(speeds.Average() > 120);
    }
}
