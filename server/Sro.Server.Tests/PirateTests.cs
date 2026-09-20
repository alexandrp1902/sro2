using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

public class PirateTests
{
    private const double Hit = 0;
    private const double Miss = 0.999;
    private const double LairX = 0;
    private const double LairY = -2500;

    /// <summary>Без защиты после появления: пираты не трогают защищённых, а тестам нужен бой сразу.</summary>
    private static readonly CombatRules Rules = new(RespawnSeconds: 2, ProtectionSeconds: 0, SpawnJitter: 0);

    private static readonly NpcType PirateType = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320, RetreatHp: 0.25);
    private static readonly NpcType HeavyType = new("Тяжёлый пират", "heavy", "plasma", Hp: 900, Shield: 300, Damage: 0.5, HoldRange: 420);

    private double _roll = Miss;
    private Room _room;
    private int _nextConnection;

    public PirateTests() => _room = NewRoom(Npcs(Lair()));

    private static NpcRules Npcs(params NpcSpawn[] spawns) => Npcs(PirateType, spawns);

    private static NpcRules Npcs(NpcType pirate, params NpcSpawn[] spawns) => new(
        RespawnSeconds: 5,
        Types: new Dictionary<string, NpcType> { ["pirate"] = pirate, ["heavy"] = HeavyType },
        Spawns: spawns);

    private static NpcSpawn Lair(int level = 1, int count = 1) => new("pirate", level, LairX, LairY, count);

    private Room NewRoom(NpcRules npcs, CombatRules? rules = null) =>
        new(TestBalance.Create(rules ?? Rules, npcs), NullLogger.Instance, () => _roll, ai: new Random(1));

    private FakeConnection Connect(string? token = null, string name = "Pilot", string? weapon = null)
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, token, name, null, weapon);
        _room.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private List<Pirate> Pirates(FakeConnection observer) =>
        observer.Last<PlayersMsg>().Players.Where(p => p.Kind == Protocol.PirateKind).Select(p => (Pirate)_room.Entity(p.Id)!).ToList();

    private void Place(int id, double x, double y, double rotDeg = 0) =>
        _room.Entity(id)!.Ship = new ShipState { X = x, Y = y, Rot = rotDeg * Math.PI / 180 };

    private void Place(FakeConnection connection, double x, double y, double rotDeg = 0) => Place(IdOf(connection), x, y, rotDeg);

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private static ShipDto Dto(FakeConnection observer, int id) => observer.Last<SnapshotMsg>().Ships.Single(s => s.Id == id);

    private static List<(long Tick, ShotDto Shot)> ShotsFrom(FakeConnection observer, int from) =>
        observer.Messages.OfType<SnapshotMsg>()
            .SelectMany(s => (s.Shots ?? []).Where(shot => shot.From == from).Select(shot => (s.Tick, shot)))
            .ToList();

    private static double Distance(ShipEntity ship, double x, double y) => Math.Sqrt(Sq(ship.Ship.X - x) + Sq(ship.Ship.Y - y));

    private static double Sq(double v) => v * v;

    [Fact]
    public void Roster_ShowsPiratesWithLevelNamesAndScaledMaximums()
    {
        _room = NewRoom(Npcs(Lair(level: 2)));
        var a = Connect();
        var dto = Assert.Single(a.Last<PlayersMsg>().Players, p => p.Npc);
        Assert.Equal(new PlayerDto(dto.Id, "Пират Ур.2", true, true, 360, 120, Protocol.PirateKind), dto);
    }

    [Fact]
    public void Patrol_StaysNearTheLair()
    {
        var a = Connect(); // у станции, в укрытии
        var pirate = Pirates(a).Single();
        double farthest = 0, fastest = 0;
        for (var i = 0; i < 60 * SimConfig.TickRate; i++)
        {
            _room.Step();
            farthest = Math.Max(farthest, Distance(pirate, LairX, LairY));
            fastest = Math.Max(fastest, Math.Sqrt(Sq(pirate.Ship.Vx) + Sq(pirate.Ship.Vy)));
        }

        Assert.InRange(farthest, 0, NpcRules.None.PatrolRadius + 100);
        Assert.True(fastest > 20, $"pirate barely moves: {fastest}");
        Assert.Equal("patrol", Dto(a, pirate.Id).Ai);
    }

    [Fact]
    public void PlayerInAggroRange_IsAttacked()
    {
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(pirate.Id, LairX, LairY, 180);
        Place(a, LairX, LairY + 600);
        Steps(1);

        Assert.Equal((PirateState.Attack, IdOf(a)), (pirate.State, pirate.TargetId));
        Assert.Equal((IdOf(a), "attack"), (Dto(a, pirate.Id).Tg, Dto(a, pirate.Id).Ai));
    }

    [Fact]
    public void StationaryTarget_BehindThePirate_IsShotWithinFourSeconds()
    {
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(pirate.Id, LairX, LairY); // носом вверх, от игрока
        Place(a, LairX, LairY + 650);
        Steps(80);

        Assert.NotEmpty(ShotsFrom(a, pirate.Id));
    }

    [Fact]
    public void PlayerFleeingAtFullSpeed_KeepsBeingShot()
    {
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(pirate.Id, LairX, LairY, 90);
        Place(a, LairX + 400, LairY, 90);

        for (var seq = 1; seq <= 200; seq++)
        {
            _room.Input(a, seq, new MoveInput(1, 0, 1));
            _room.Step();
        }

        var shots = ShotsFrom(a, pirate.Id).Where(s => s.Tick > 40).ToList();
        Assert.True(shots.Count >= 5, $"only {shots.Count} shots at the fleeing player");
        var player = _room.Entity(IdOf(a))!;
        Assert.InRange(Distance(pirate, player.Ship.X, player.Ship.Y), 0, 700);
    }

    [Fact]
    public void WithAnAllAroundGun_ThePirateCirclesTheTargetAndKeepsFiring()
    {
        _room = NewRoom(Npcs(PirateType with { Weapon = "turret" }, Lair()));
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(pirate.Id, LairX, LairY);
        Place(a, LairX, LairY + 500);
        var player = _room.Entity(IdOf(a))!;

        double nearest = double.MaxValue, farthest = 0, speedSum = 0, turned = 0;
        var lastAngle = double.NaN;
        const int settle = 100, ticks = 500;
        for (var i = 0; i < ticks; i++)
        {
            _room.Step();
            if (i < settle) continue;
            var dx = pirate.Ship.X - player.Ship.X;
            var dy = pirate.Ship.Y - player.Ship.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            nearest = Math.Min(nearest, d);
            farthest = Math.Max(farthest, d);
            speedSum += Math.Sqrt(Sq(pirate.Ship.Vx) + Sq(pirate.Ship.Vy));
            var angle = Math.Atan2(dy, dx);
            if (!double.IsNaN(lastAngle)) turned += Math.Abs(Movement.WrapAngle(angle - lastAngle));
            lastAngle = angle;
        }

        Assert.InRange(nearest, 150, farthest);
        Assert.InRange(farthest, nearest, 700); // всегда в дальности пушки
        Assert.True(speedSum / (ticks - settle) > 50, "the pirate should keep moving");
        Assert.True(turned > Math.PI, $"the pirate should circle the target, turned {turned:0.00} rad");
        Assert.True(ShotsFrom(a, pirate.Id).Count(s => s.Tick > settle) >= 15);
    }

    [Fact]
    public void PlayerInTheShelter_IsLeftAlone()
    {
        _room = NewRoom(Npcs(new NpcSpawn("pirate", 1, 0, -1400))); // ближе, чем пропустила бы проверка файла
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(a, 0, -850); // в укрытии, до логова 550
        Steps(40);

        Assert.Equal((PirateState.Patrol, 0), (pirate.State, pirate.TargetId));
    }

    [Fact]
    public void ProtectedPlayer_IsAttackedOnlyWhenTheProtectionEnds()
    {
        var rules = Rules with { ProtectionSeconds = 2 };
        _room = NewRoom(Npcs(Lair()), rules);
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(a, LairX, LairY + 400);

        Steps(rules.ProtectionTicks - 1);
        Assert.Equal(PirateState.Patrol, pirate.State);
        Steps(2);
        Assert.Equal((PirateState.Attack, IdOf(a)), (pirate.State, pirate.TargetId));
    }

    [Fact]
    public void ShipWithoutConnection_IsLeftAlone()
    {
        var a = Connect("token-aaaaaaaaaaaaaaaa");
        var observer = Connect(name: "Observer");
        var pirate = Pirates(observer).Single();
        Place(a, LairX, LairY + 400);
        _room.Disconnect(a);
        Steps(40);

        Assert.Equal(PirateState.Patrol, pirate.State);
    }

    [Fact]
    public void Drones_AreLeftAlone()
    {
        _room = NewRoom(Npcs(Lair()), Rules with { Drones = [new DroneSpec("Дрон", "light", LairX, LairY + 300)] });
        var a = Connect();
        var pirate = Pirates(a).Single();
        Steps(40);

        Assert.Equal(PirateState.Patrol, pirate.State);
        Assert.Empty(ShotsFrom(a, pirate.Id));
    }

    [Fact]
    public void ShotFromOutsideAggroRange_StartsAFight_AndTheOtherPirateJoins()
    {
        _room = NewRoom(Npcs(Lair(count: 2)) with { AggroRange = 400 });
        var a = Connect();
        var pirates = Pirates(a);
        Place(pirates[0].Id, LairX, LairY);
        Place(pirates[1].Id, LairX - 200, LairY);
        Place(a, LairX, LairY + 650); // носом вверх — на пиратов; до обоих дальше 400

        Steps(5);
        Assert.All(pirates, p => Assert.Equal(PirateState.Patrol, p.State));

        _room.SetTarget(a, pirates[0].Id);
        _room.SetFire(a, true);
        Steps(3);

        Assert.All(pirates, p => Assert.Equal((PirateState.Attack, IdOf(a)), (p.State, p.TargetId)));
        Assert.All(ShotsFrom(a, IdOf(a)), s => Assert.False(s.Shot.Hit)); // хватило промаха
    }

    [Fact]
    public void LowHull_SendsThePirateHomeToRepair()
    {
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(pirate.Id, LairX, LairY + 800, 180);
        Place(a, LairX + 400, LairY + 800); // до логова 894 — дальше радиуса агро
        pirate.Hp = 60; // 20% из 300

        Steps(1);
        Assert.Equal((PirateState.Return, "return"), (pirate.State, Dto(a, pirate.Id).Ai));

        var start = Distance(pirate, LairX, LairY);
        for (var i = 0; i < 600 && pirate.State != PirateState.Patrol; i++) _room.Step();

        Assert.Equal(PirateState.Patrol, pirate.State);
        Assert.True(Distance(pirate, LairX, LairY) < start);
        Assert.Equal((300.0, 100.0), (pirate.Hp, pirate.Shield));
        Assert.Empty(ShotsFrom(a, pirate.Id));
    }

    [Fact]
    public void TargetHidingInTheShelter_SendsThePirateHome_DeafToFire()
    {
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(pirate.Id, 0, -1400, 180);
        Place(a, 0, -1100);
        Steps(1);
        Assert.Equal(PirateState.Attack, pirate.State);

        Place(a, 0, -850); // в укрытие
        Steps(1);
        Assert.Equal(PirateState.Return, pirate.State);
        var returnedAt = a.Last<SnapshotMsg>().Tick;

        _room.SetTarget(a, pirate.Id);
        _room.SetFire(a, true);
        Steps(20);
        Assert.NotEmpty(ShotsFrom(a, IdOf(a)));
        Assert.Equal(PirateState.Return, pirate.State);
        Assert.DoesNotContain(ShotsFrom(a, pirate.Id), s => s.Tick >= returnedAt); // до укрытия стрелял — это честно

        for (var i = 0; i < 600 && pirate.State != PirateState.Patrol; i++) _room.Step();
        Assert.Equal(PirateState.Patrol, pirate.State);
    }

    [Fact]
    public void TooFarFromTheLair_ThePirateGoesBack()
    {
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(pirate.Id, LairX + 1900, LairY, 90);
        Place(a, LairX + 2100, LairY);

        Steps(2);
        Assert.Equal(PirateState.Return, pirate.State);
    }

    [Fact]
    public void DestroyedTarget_BackToPatrolNotHome()
    {
        _roll = Hit;
        _room = NewRoom(Npcs(PirateType with { Weapon = "doom", Damage = 1 }, Lair()));
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(pirate.Id, LairX, LairY, 180);
        Place(a, LairX, LairY + 300);

        Steps(2);
        Assert.Contains(new KillDto(IdOf(a), pirate.Id), a.Messages.OfType<SnapshotMsg>().SelectMany(s => s.Kills ?? []));
        Assert.Equal((PirateState.Patrol, 0), (pirate.State, pirate.TargetId));
    }

    [Fact]
    public void DestroyedPirate_RespawnsAtTheLairWithTheLevelHull_WithoutProtection()
    {
        _roll = Hit;
        _room = NewRoom(Npcs(Lair(level: 3)));
        var a = Connect(weapon: "doom");
        var pirate = Pirates(a).Single();
        Place(pirate.Id, LairX, LairY);
        Place(a, LairX, LairY + 300);
        _room.SetTarget(a, pirate.Id);
        _room.SetFire(a, true);

        Steps(1);
        Assert.True(Dto(a, pirate.Id).Rt > 0);
        _room.SetFire(a, false);

        Steps(_room.Balance.Npc.RespawnTicks);
        var back = Dto(a, pirate.Id);
        Assert.Equal((0L, 0L, 420, 140), (back.Rt, back.Pu, back.Hp, back.Sh));
        Assert.InRange(Math.Sqrt(Sq(back.X - LairX) + Sq(back.Y - LairY)), 0, 61);
        Assert.Equal("patrol", back.Ai);
    }

    [Theory]
    [InlineData(1, 45)]
    [InlineData(2, 50)] // 49.5
    [InlineData(3, 54)]
    public void PirateDamage_ScalesWithLevel(int level, int damage)
    {
        _roll = Hit;
        _room = NewRoom(Npcs(Lair(level)));
        var a = Connect();
        var pirate = Pirates(a).Single();
        Place(pirate.Id, LairX, LairY, 180);
        Place(a, LairX, LairY + 300);

        Steps(1);
        var shot = ShotsFrom(a, pirate.Id).Single().Shot;
        Assert.Equal((true, damage), (shot.Hit, shot.Dmg));
    }

    [Fact]
    public void BalanceChange_KeepsTheHullShare_NewLairsReplaceThePirates()
    {
        var a = Connect();
        var pirate = Pirates(a).Single();
        pirate.Hp = 150; // половина из 300

        _room.ApplyBalance(TestBalance.Create(Rules, Npcs(PirateType with { Hp = 600 }, Lair())));
        Assert.Same(pirate, _room.Entity(pirate.Id));
        Assert.Equal(300, pirate.Hp, 9);
        Assert.Equal(600, a.Last<PlayersMsg>().Players.Single(p => p.Id == pirate.Id).MaxHp);
        Assert.Equal(600, a.Last<ConfigMsg>().Npcs!.TypeMap["pirate"].Hp);

        _room.ApplyBalance(TestBalance.Create(Rules, Npcs(Lair(), new NpcSpawn("pirate", 2, 2500, 0))));
        var fresh = Pirates(a);
        Assert.Equal(2, fresh.Count);
        Assert.Null(_room.Entity(pirate.Id));
        Assert.Contains(fresh, p => p.Name == "Пират Ур.2");
    }

    [Fact]
    public void Player_CannotTakeAPirateName()
    {
        var a = Connect(name: "пират ур.1");
        Assert.Equal("пират ур.1 2", a.Last<PlayersMsg>().Players.Single(p => p.Id == IdOf(a)).Name);
    }
}
