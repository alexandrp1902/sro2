using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>Метеориты (M5b): полёт, таран, минералы, трасса мимо укрытия и место в протоколе.</summary>
public class MeteorTests
{
    private const double Hit = 0;
    private const double LairX = 0;
    private const double LairY = -2500;
    /// <summary>Мелкий метеорит (14) плюс лёгкий корпус (16).</summary>
    private const double Reach = 30;

    private static readonly CombatRules Rules = new(RespawnSeconds: 2, ProtectionSeconds: 0, SpawnJitter: 0);

    private static readonly NpcType PirateType =
        new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320);

    private static readonly LootRules Loot = new(
        FadeSeconds: 0,
        Items: new Dictionary<string, LootItem>
        {
            ["ore"] = new("Руда", Volume: 1, Price: 8),
            ["metal"] = new("Металл", Volume: 1, Price: 10),
        },
        Tables: new Dictionary<string, LootTable>
        {
            ["rock"] = new([new LootRoll("ore", 1, 3, 3)]),
            ["pirate"] = new([new LootRoll("metal", 1, 2, 2)]),
        });

    private double _roll = Hit;
    private Room _room;
    private int _nextConnection;

    public MeteorTests() => _room = NewRoom(Meteors());

    /// <summary>
    /// По умолчанию метеориты сами не появляются и летят по прямой: проверки тарана и стрельбы не должны
    /// зависеть от кривизны. Тяготение включается там, где его и проверяют.
    /// </summary>
    private static MeteorRules Meteors(
        int maxAlive = 0,
        double spawnIntervalSeconds = 7,
        double lifetimeSeconds = 90,
        double gravity = 0) => new(
        SpawnIntervalSeconds: spawnIntervalSeconds,
        MaxAlive: maxAlive,
        LifetimeSeconds: lifetimeSeconds,
        Gravity: gravity,
        Sizes: new Dictionary<string, MeteorSize>
        {
            ["small"] = new("Мелкий метеорит", 14, 60, 240, 300, 110, 1, "rock"),
            ["large"] = new("Крупный метеорит", 34, 320, 180, 220, 300, 1, "rock"),
            // Убивает кого угодно с одного удара — для проверок гибели от тарана.
            ["boulder"] = new("Глыба", 34, 320, 180, 220, 5000, 0, "rock"),
        });

    private static NpcRules Npcs() => new(
        RespawnSeconds: 5,
        Types: new Dictionary<string, NpcType> { ["pirate"] = PirateType },
        Spawns: [new NpcSpawn("pirate", 1, LairX, LairY)]);

    private Room NewRoom(MeteorRules meteors, CombatRules? rules = null, NpcRules? npcs = null, int seed = 1) => new(
        TestBalance.Create(rules ?? Rules, npcs, Loot, meteors),
        NullLogger.Instance,
        () => _roll,
        ai: new Random(1),
        loot: new Random(1),
        meteors: new Random(seed));

    private FakeConnection Connect(string? weapon = null, string? token = null)
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, token, "Pilot", null, weapon);
        _room.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private ShipEntity ShipOf(FakeConnection connection) => _room.Entity(IdOf(connection))!;

    private void Place(int id, double x, double y, double vx = 0, double vy = 0) =>
        _room.Entity(id)!.Ship = new ShipState { X = x, Y = y, Vx = vx, Vy = vy };

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private Meteor Launch(string size, double x, double y, double vx, double vy) => _room.LaunchMeteor(size, x, y, vx, vy)!;

    private static double Sq(double v) => v * v;

    [Fact]
    public void Gravity_BendsTheTrackAndSpeedsTheRockUp()
    {
        _room = NewRoom(Meteors(gravity: 15_000_000));
        Connect();
        // Идёт мимо центра системы: тяготение должно и загнуть курс внутрь, и разогнать камень.
        var meteor = Launch("small", 1500, -3000, 0, 250);

        // 30 с: кривизна копится по всей дуге, а не за пару секунд. Скорость смотрим наибольшую за полёт —
        // у самой близкой точки; дальше камень уходит от центра и снова замедляется.
        var top = 0.0;
        for (var i = 0; i < 600; i++)
        {
            _room.Step();
            top = Math.Max(top, meteor.Speed);
        }

        Assert.True(meteor.Ship.X < 1500 - 100, $"the track did not bend: x = {meteor.Ship.X}");
        Assert.True(meteor.Ship.Vx < 0, $"no inward pull: vx = {meteor.Ship.Vx}");
        Assert.True(top > 250, $"gravity did not speed it up: {top}");
    }

    [Fact]
    public void WithoutGravity_TheRockFliesStraight()
    {
        Connect();
        var meteor = Launch("small", 100, 3000, 0, 250);

        _room.Step();
        Assert.Equal(100, meteor.Ship.X, 9);
        Assert.Equal(3000 + 250 * SimConfig.Dt, meteor.Ship.Y, 9);

        // За границей мира камня ничто не держит: он уходит за черту исчезновения и пропадает.
        Steps(140);
        Assert.Empty(_room.Meteors);
        Assert.Null(_room.Entity(meteor.Id));
    }

    [Fact]
    public void Meteor_ExpiresAfterItsLifetime()
    {
        _room = NewRoom(Meteors(lifetimeSeconds: 1));
        var meteor = Launch("small", 0, -2000, 0, 0);

        Steps(20);
        Assert.Null(_room.Entity(meteor.Id));
    }

    [Fact]
    public void Meteor_IsInTheSnapshotButNotInTheRosterOrShips()
    {
        var a = Connect();
        var meteor = Launch("large", 1000, 1000, 200, 0);
        _room.Step();

        var snapshot = a.Last<SnapshotMsg>();
        Assert.DoesNotContain(snapshot.Ships, s => s.Id == meteor.Id);
        var dto = Assert.Single(snapshot.Meteors!);
        Assert.Equal((meteor.Id, "large", 320), (dto.Id, dto.S, dto.Hp));
        Assert.Equal(200, dto.Vx);

        _room.Rename(a, "Другой"); // рассылает ростер
        Assert.DoesNotContain(a.Last<PlayersMsg>().Players, p => p.Id == meteor.Id);
    }

    [Fact]
    public void Snapshot_OmitsMeteorsWhenThereAreNone()
    {
        var a = Connect();
        _room.Step();
        Assert.Null(a.Last<SnapshotMsg>().Meteors);
    }

    [Fact]
    public void Welcome_CarriesMeteorRules()
    {
        var a = Connect();
        Assert.Equal(3, a.Last<WelcomeMsg>().Meteors!.SizeMap.Count);
    }

    [Fact]
    public void Spawn_IsAChancePerTick_NotASchedule()
    {
        // Интервал 1 с — это среднее: за секунду камней то ноль, то несколько. Расписание давало бы ровно
        // по одному в каждой секунде.
        var uneven = 0;
        _room = NewRoom(Meteors(maxAlive: 60, spawnIntervalSeconds: 1), seed: 7);
        Connect();
        for (var second = 0; second < 20; second++)
        {
            var before = _room.Meteors.Count;
            Steps(20);
            if (_room.Meteors.Count - before != 1) uneven++;
        }

        Assert.True(uneven > 3, $"spawns look scheduled: only {uneven} uneven seconds of 20");
        Assert.InRange(_room.Meteors.Count, 8, 34); // около 20 за 20 секунд, но не ровно
    }

    [Fact]
    public void Tracks_GiveDifferentArcsAndSpeeds()
    {
        var tracks = new Dictionary<string, MeteorTrack>
        {
            ["flyby"] = new("Пролёт", 1, 1, 0.5),
            ["grazer"] = new("Ныряющий", 0.3, 0.6, 0.5),
        };
        _room = NewRoom(Meteors(maxAlive: 200, spawnIntervalSeconds: 0.05, gravity: 15_000_000) with { Tracks = tracks });
        Connect();
        Steps(400);

        var speeds = _room.Meteors.Select(m => m.Speed).ToList();
        Assert.NotEmpty(speeds);
        // Ныряющие идут заметно медленнее пролётных: в небе действительно разные траектории.
        Assert.True(speeds.Max() - speeds.Min() > 60, $"all rocks fly alike: {speeds.Min():0}..{speeds.Max():0}");
    }

    [Fact]
    public void Ram_HitsTheShieldFirstAndReportsARamShot()
    {
        var a = Connect();
        var ship = ShipOf(a);
        Place(ship.Id, 0, -2000);
        var meteor = Launch("small", 0, -2000 - 40, 0, 250);

        _room.Step();

        // Сближение равно скорости камня — множитель 1: весь урон 110 ушёл в щит 150.
        var shot = Assert.Single(a.Last<SnapshotMsg>().Shots!);
        Assert.Equal((meteor.Id, ship.Id, MeteorRules.RamWeapon, true, 110, 110), (shot.From, shot.To, shot.W, shot.Hit, shot.Dmg, shot.Sh));
        Assert.Equal(40, ship.Shield, 9);
        Assert.Equal(400, ship.Hp, 9);

        // Камень разбился: ни в полёте, ни среди кораблей его больше нет.
        Assert.Empty(_room.Meteors);
        Assert.Null(_room.Entity(meteor.Id));
        Assert.Contains(a.Last<SnapshotMsg>().Kills!, k => k.Id == meteor.Id);
    }

    [Theory]
    [InlineData(-165, 176)] // лоб в лоб: 415 / 250 = 1.66 → потолок 1.6
    [InlineData(0, 110)]    // корабль стоит: множитель 1
    [InlineData(200, 55)]   // камень догоняет: 50 / 250 = 0.2 → пол 0.5
    public void RamDamage_ScalesWithClosingSpeed(double shipVy, int expected)
    {
        var a = Connect();
        var ship = ShipOf(a);
        // Корабль без входов стоит на месте, но его скорость в расчёт сближения входит.
        Place(ship.Id, 0, -2000, 0, shipVy);
        Launch("small", 0, -2000 - 25, 0, 250);

        _room.Step();

        Assert.Equal(expected, Assert.Single(a.Last<SnapshotMsg>().Shots!).Dmg);
    }

    [Fact]
    public void HeadOn_AtTheHighestRelativeSpeed_IsNeverSkipped()
    {
        // 465 — наибольшее сближение по meteors.json (300 + 165): шаг за тик 23.25 меньше досягаемости 30.
        for (var gap = Reach + 0.5; gap <= Reach + 465 * SimConfig.Dt + 0.5; gap += 0.5)
        {
            _room = NewRoom(Meteors());
            var a = Connect();
            var ship = ShipOf(a);
            Place(ship.Id, 0, -2000);
            Launch("small", 0, -2000 - gap, 0, 465);

            Steps(3);
            Assert.True(ship.Shield < 150, $"gap {gap}: the meteor passed through");
        }
    }

    [Fact]
    public void GlancingPass_AtNinetyPercentOfReach_IsNeverSkipped()
    {
        for (var start = 0.0; start < 465 * SimConfig.Dt; start += 0.5)
        {
            _room = NewRoom(Meteors());
            var a = Connect();
            var ship = ShipOf(a);
            Place(ship.Id, 0, -2000);
            Launch("small", 0.9 * Reach, -2000 - 60 - start, 0, 465);

            Steps(6);
            Assert.True(ship.Shield < 150, $"start {start}: the glancing hit was skipped");
        }
    }

    [Fact]
    public void PassJustOutsideReach_DoesNotHit()
    {
        var a = Connect();
        var ship = ShipOf(a);
        Place(ship.Id, 0, -2000);
        var meteor = Launch("small", Reach + 1, -2100, 0, 250);

        Steps(20);

        Assert.Equal(150, ship.Shield, 9);
        Assert.Contains(meteor, _room.Meteors);
    }

    [Fact]
    public void ProtectedShip_IsNotRammed()
    {
        _room = NewRoom(Meteors(), new CombatRules(RespawnSeconds: 2, ProtectionSeconds: 10, SpawnJitter: 0));
        var a = Connect();
        var ship = ShipOf(a);
        Place(ship.Id, 0, -2000);
        var meteor = Launch("small", 0, -2000 - 25, 0, 250);

        _room.Step();

        Assert.Equal(150, ship.Shield, 9);
        Assert.Contains(meteor, _room.Meteors); // пролетел насквозь
    }

    [Fact]
    public void DisconnectedShip_IsNotRammed()
    {
        var a = Connect(token: "session-token-0001");
        var ship = ShipOf(a);
        _room.Disconnect(a);
        Place(ship.Id, 0, -2000);
        Launch("small", 0, -2000 - 25, 0, 250);

        _room.Step();

        Assert.Equal(150, ship.Shield, 9);
    }

    [Fact]
    public void Ram_ThatKills_CreditsTheMeteor()
    {
        var a = Connect();
        var ship = ShipOf(a);
        Place(ship.Id, 0, -2000);
        var meteor = Launch("boulder", 0, -2000 - 40, 0, 200);

        _room.Step();

        Assert.True(ship.IsDead);
        Assert.Contains(new KillDto(ship.Id, meteor.Id), a.Last<SnapshotMsg>().Kills!);
    }

    /// <summary>
    /// Камень, разбившийся о корабль пилота, считается сбитым им (M16a): в своде уничтоженных он идёт
    /// с его id, и дальше по общему пути попадает в задание «охота».
    /// </summary>
    [Fact]
    public void Ram_ByAPilot_CreditsTheRockToHim()
    {
        var a = Connect();
        var ship = ShipOf(a);
        Place(ship.Id, 0, -2000);
        var meteor = Launch("small", 0, -2000 - 25, 0, 250);

        _room.Step();

        Assert.False(ship.IsDead); // мелкий камень пилота не убивает — важно, что засчитан он и живому
        Assert.Equal(ship.Id, meteor.KilledBy);
        Assert.Contains(new KillDto(meteor.Id, ship.Id), a.Last<SnapshotMsg>().Kills!);
    }

    /// <summary>И тот таран, в котором погиб сам пилот: камень уничтожен, и это его заслуга.</summary>
    [Fact]
    public void Ram_ThatKillsThePilot_StillCreditsHimTheRock()
    {
        var a = Connect();
        var ship = ShipOf(a);
        Place(ship.Id, 0, -2000);
        var meteor = Launch("boulder", 0, -2000 - 40, 0, 200);

        _room.Step();

        var kills = a.Last<SnapshotMsg>().Kills!;
        Assert.True(ship.IsDead);
        Assert.Contains(new KillDto(ship.Id, meteor.Id), kills);
        Assert.Contains(new KillDto(meteor.Id, ship.Id), kills);
    }

    /// <summary>
    /// Один камень — один счёт. Выстрел в тот же тик, что и таран, не переписывает убийцу:
    /// разбитая цель из боя уже выбыла, и стрелять по ней нечем.
    /// </summary>
    [Fact]
    public void RammedRock_IsNotCreditedTwice()
    {
        var a = Connect(weapon: "doom");
        var b = Connect();
        var shooter = ShipOf(a);
        var rammer = ShipOf(b);
        Place(shooter.Id, 0, -2100);
        Place(rammer.Id, 0, -2000);
        var meteor = Launch("small", 0, -2000 - 25, 0, 250);
        _room.SetTarget(a, meteor.Id);
        _room.SetFire(a, true);

        _room.Step();

        Assert.Equal(rammer.Id, meteor.KilledBy);
        Assert.Single(a.Last<SnapshotMsg>().Kills!, kill => kill.Id == meteor.Id);
    }

    [Fact]
    public void Ram_GivesNoMinerals()
    {
        var a = Connect();
        Place(IdOf(a), 0, -2000);
        Launch("small", 0, -2000 - 25, 0, 250);

        _room.Step();

        Assert.Null(a.Last<SnapshotMsg>().Loot);
    }

    [Fact]
    public void ShotDownMeteor_DropsItsTableAndDoesNotComeBack()
    {
        var a = Connect(weapon: "doom");
        Place(IdOf(a), 0, -2000);
        var meteor = Launch("small", 0, -2300, 10, 0);
        _room.SetTarget(a, meteor.Id);
        _room.SetFire(a, true);

        _room.Step();

        Assert.Contains(new KillDto(meteor.Id, IdOf(a)), a.Last<SnapshotMsg>().Kills!);
        var drop = Assert.Single(a.Last<SnapshotMsg>().Loot!);
        Assert.Equal(("ore", 3), (drop.I, drop.N));
        Assert.Null(_room.Entity(meteor.Id));
        // Цель, указывавшая на разбитый камень, снята.
        Assert.Equal(0, ShipOf(a).TargetId);

        Steps(200); // дольше респауна
        Assert.Null(_room.Entity(meteor.Id));
        Assert.Empty(_room.Meteors);
    }

    [Fact]
    public void ShotAtAMeteor_HasNoEvasion()
    {
        var a = Connect(weapon: "pulse");
        Place(IdOf(a), 0, -2000);
        // Быстрый камень: будь у него корпус по умолчанию, уклонение срезало бы 33%.
        var meteor = Launch("large", 0, -2300, 220, 0);
        _room.SetTarget(a, meteor.Id);
        _room.SetFire(a, true);

        _room.Step();

        var shot = Assert.Single(a.Last<SnapshotMsg>().Shots!);
        Assert.Equal(TestBalance.Weapons["pulse"].Accuracy, shot.Ch); // 75: без уклонения и без штрафа на 300
    }

    [Fact]
    public void Meteor_RamsPirates_AndTheWreckDropsPirateLoot()
    {
        _room = NewRoom(Meteors(), npcs: Npcs());
        var a = Connect();
        var pirate = a.Last<PlayersMsg>().Players.Where(p => p.Kind == Protocol.PirateKind).Select(p => (Pirate)_room.Entity(p.Id)!).Single();
        Place(pirate.Id, LairX, LairY);
        var meteor = Launch("boulder", LairX, LairY - 30, 0, 200);

        _room.Step();

        Assert.True(pirate.IsDead);
        Assert.Contains(new KillDto(pirate.Id, meteor.Id), a.Last<SnapshotMsg>().Kills!);
        Assert.Contains(a.Last<SnapshotMsg>().Loot!, d => d.I == "metal");
    }

    [Fact]
    public void EmptyRoom_LaunchesNothing()
    {
        _room = NewRoom(Meteors(maxAlive: 5, spawnIntervalSeconds: 0.05));
        Steps(200);
        Assert.Empty(_room.Meteors);
    }

    [Fact]
    public void FirstMeteor_DoesNotArriveInstantly()
    {
        // Монета на тик со средним 7 с: в первые полсекунды камень почти невозможен.
        _room = NewRoom(Meteors(maxAlive: 5));
        Connect();
        Steps(10);
        Assert.Empty(_room.Meteors);
    }

    [Fact]
    public void MaxAlive_CapsTheSky()
    {
        _room = NewRoom(Meteors(maxAlive: 2, spawnIntervalSeconds: 0.05));
        Connect();
        Steps(100);
        Assert.Equal(2, _room.Meteors.Count);
    }

    [Fact]
    public void LaunchedTracks_NeverCrossTheStationShelter()
    {
        var shelter = new NpcRules().StationSafeRadius;
        // Живут три тика: каждая трасса проверяется в тик появления, а снапшот не раздувается тысячами камней.
        // Тяготение включено: с ним прямая линия уже ничего не доказывает — проигрываем настоящую дугу.
        var rules = Meteors(maxAlive: 50, spawnIntervalSeconds: 0.05, lifetimeSeconds: 0.15, gravity: 15_000_000) with
        {
            Tracks = new Dictionary<string, MeteorTrack>
            {
                ["flyby"] = new("Пролёт", 1, 1, 0.5),
                ["grazer"] = new("Ныряющий", 0.3, 0.6, 0.5),
            },
        };
        _room = NewRoom(rules, npcs: new NpcRules());
        Connect();
        var seen = new HashSet<int>();
        var entry = Movement.WorldHalfSize + rules.DespawnMargin * 0.8;

        for (var i = 0; i < 2000; i++)
        {
            _room.Step();
            foreach (var m in _room.Meteors)
            {
                if (!seen.Add(m.Id)) continue;
                var s = m.Ship;
                // Появился на краю — за границей мира, а не посреди экрана.
                Assert.True(Math.Max(Math.Abs(s.X), Math.Abs(s.Y)) >= entry - 1e-6, $"{m.Id} appeared inside at ({s.X}, {s.Y})");
                var pass = ClosestApproach(rules, s.X, s.Y, s.Vx, s.Vy);
                Assert.True(pass >= shelter + m.Size.Radius, $"{m.Id} passes {pass:0} from the station");
                // И летит через обитаемую часть, а не по касательной к краю мира.
                Assert.True(pass <= rules.AimRadius, $"{m.Id} misses the inhabited part: {pass:0}");
            }
        }
        Assert.True(seen.Count > 600, $"only {seen.Count} launched");
    }

    /// <summary>Ближайший подход дуги к центру системы — тем же шагом и той же схемой, что и сама симуляция.</summary>
    private static double ClosestApproach(MeteorRules rules, double x, double y, double vx, double vy)
    {
        var limit = Movement.WorldHalfSize + rules.DespawnMargin;
        var closest = double.MaxValue;
        for (var i = 0; i < SimConfig.TickRate * 240; i++)
        {
            rules.Step(ref x, ref y, ref vx, ref vy, SimConfig.Dt);
            closest = Math.Min(closest, Math.Sqrt(Sq(x) + Sq(y)));
            if ((Math.Abs(x) > limit || Math.Abs(y) > limit) && x * vx + y * vy > 0) break;
        }
        return closest;
    }

    [Fact]
    public void ApplyBalance_LeavesFlyingMeteorsAlone()
    {
        var a = Connect();
        var meteor = Launch("small", 1000, 1000, 250, 0);
        var sizes = new Dictionary<string, MeteorSize>(Meteors().SizeMap)
        {
            ["small"] = new("Мелкий метеорит", 14, 999, 240, 300, 110, 1, "rock"),
        };

        _room.ApplyBalance(TestBalance.Create(Rules, null, Loot, Meteors() with { Sizes = sizes }));
        _room.Step();

        Assert.Equal(60, meteor.Hp, 9);
        Assert.Equal(250, meteor.Ship.Vx, 9);
        Assert.Contains(meteor, _room.Meteors);
        Assert.Equal(999, a.Last<ConfigMsg>().Meteors!.SizeMap["small"].Hp);
    }

    [Fact]
    public void Meteor_CanBeTargeted()
    {
        var a = Connect();
        var meteor = Launch("small", 1000, 1000, 250, 0);
        _room.SetTarget(a, meteor.Id);
        Assert.Equal(meteor.Id, ShipOf(a).TargetId);
    }
}
