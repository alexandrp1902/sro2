using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Урон по площади в комнате (M15.5): взрыв в точке попадания задевает соседей, но только врагов.
/// Мирные, свои и камни целы, промах не взрывается, и осколки не делают стрелка обидчиком.
/// </summary>
public class BlastRoomTests
{
    private const double Hit = 0;
    private const double Miss = 0.999;

    private static readonly CombatRules Rules = new(RespawnSeconds: 2, ProtectionSeconds: 0, ShieldRegenDelay: 1, SpawnJitter: 0);

    /// <summary>Радиус 200, доля 0.5: сосед в эпицентре получает половину урона пушки.</summary>
    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["blaster"] = new("Площадная", 200, 100, 1.0, 500, 700, 0, Arc: 180, BlastRadius: 200, BlastShare: 0.5),
        ["plain"] = new("Обычная", 200, 100, 1.0, 500, 700, 0, Arc: 180),
        // Та же площадная, но мажет почти всегда: на ней проверяется, что промах не взрывается.
        ["dud"] = new("Косая", 200, 5, 1.0, 500, 700, 0, Arc: 180, BlastRadius: 200, BlastShare: 0.5),
        ["torpedo"] = new(
            "Торпедный аппарат", 200, 100, 8.0, 900, 900, 0, Arc: 180, Kind: WeaponParams.MissileKind,
            Missile: new MissileParams(Speed: 600, TurnRate: 180, Lifetime: 9, HitRadius: 16),
            BlastRadius: 200, BlastShare: 0.5),
    };

    /// <summary>Корпуса без уклонения: попадёт или нет, решает бросок, а не случайность.</summary>
    private static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams>
    {
        ["light"] = TestBalance.Hulls["light"] with { Evasion = 0, MoveEvasion = 0, Size = 16 },
        ["heavy"] = TestBalance.Hulls["heavy"] with { Evasion = 0, MoveEvasion = 0, Size = 16 },
    };

    /// <summary>NPC не стреляют и живучи: в этих тестах важен только урон от взрыва.</summary>
    private static readonly NpcType PirateType = new("Пират", "light", "plain", Hp: 4000, Shield: 0, Damage: 0);

    private static readonly NpcType TraderType = new(
        "Торговец", "heavy", Hp: 4000, Shield: 0, Damage: 0, Faction: NpcType.TraderFaction);

    private static readonly NpcType RangerType = new(
        "Рейнджер", "light", Hp: 4000, Shield: 0, Damage: 0, Faction: NpcType.RangerFaction);

    private double _roll = Hit;
    private int _nextConnection;
    private Room _room;

    public BlastRoomTests() => _room = NewRoom();

    /// <param name="traders">Поднять систему с торговцем: ему нужны врата и маршрут, а не просто спаун.</param>
    private Room NewRoom(MeteorRules? meteors = null, bool traders = false)
    {
        NpcSpawn[] spawns = [new NpcSpawn("pirate", 1, 3000, 3000, 4), new NpcSpawn("ranger", 1, 3400, 3400)];
        var npcs = new NpcRules(
            StationSafeRadius: 0,
            Types: new Dictionary<string, NpcType> { ["pirate"] = PirateType, ["trader"] = TraderType, ["ranger"] = RangerType },
            Spawns: spawns);
        var balance = new Balance(Hulls, Weapons, Rules, npcs, MeteorSet: meteors);

        if (traders)
        {
            var galaxy = new GalaxyRules(
                StartSystem: "test",
                Systems: new Dictionary<string, SystemDef>
                {
                    // Торговцу нужны две точки маршрута: без станции это двое врат.
                    ["test"] = new("Test", Station: false, Gates: [new GateDef("other", 3000, 0), new GateDef("far", -3000, 0)], Spawns: spawns, Traders: new TraderRules(Count: 1)),
                    ["other"] = new("Other", Gates: [new GateDef("test", 0, 3000)]),
                    ["far"] = new("Far", Gates: [new GateDef("test", 0, 3000)]),
                },
                Links: [new LinkDef("test", "other", 10), new LinkDef("test", "far", 10)]);
            balance = (balance with { GalaxySet = galaxy }).ForSystem("test");
        }

        return new Room(balance, NullLogger.Instance, () => _roll, ai: new Random(1), loot: new Random(1), meteors: new Random(1));
    }

    private FakeConnection Connect(string name = "Pilot", string? weapon = null)
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, null, name, null, weapon);
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private ShipEntity Ship(FakeConnection connection) => _room.Entity(IdOf(connection))!;

    private static T At<T>(T ship, double x, double y) where T : ShipEntity
    {
        ship.Ship = new ShipState { X = x, Y = y };
        return ship;
    }

    private void Place(FakeConnection connection, double x, double y) => At(Ship(connection), x, y);

    /// <summary>Пират номер index, поставленный в точку.</summary>
    private Pirate Raider(int index, double x, double y) =>
        At(_room.Pirates.Where(p => p.Type.IsPirate).ElementAt(index), x, y);

    private Pirate Ranger(double x, double y) => At(_room.Pirates.First(p => p.Type.IsRanger), x, y);

    private Trader Merchant(double x, double y) => At(_room.Traders[0], x, y);

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private void Attack(FakeConnection attacker, ShipEntity target)
    {
        _room.SetTarget(attacker, target.Id);
        _room.SetFire(attacker, true);
    }

    private static List<ShotDto> Shots(FakeConnection observer) =>
        [.. observer.Messages.OfType<SnapshotMsg>().SelectMany(s => s.Shots ?? [])];

    private static double Life(ShipEntity ship) => ship.Hp + ship.Shield;

    /// <summary>Стрелок в начале координат, цель в 300 впереди, сосед — рядом с целью.</summary>
    private (FakeConnection Shooter, ShipEntity Target, ShipEntity Neighbour) Crowd(double gap = 100, string weapon = "blaster")
    {
        var shooter = Connect("A", weapon);
        Place(shooter, 0, 0);
        return (shooter, Raider(0, 0, -300), Raider(1, gap, -300));
    }

    [Fact]
    public void AHit_AlsoBurnsTheNeighbour()
    {
        var (shooter, target, neighbour) = Crowd();
        var before = Life(neighbour);
        Attack(shooter, target);
        Steps(1);

        Assert.True(Life(neighbour) < before, "сосед в радиусе взрыва должен получить осколки");
        Assert.Contains(Shots(shooter), s => s.W == Combat.SplashWeapon && s.To == neighbour.Id && s.From == IdOf(shooter));
    }

    [Fact]
    public void TheTargetItselfTakesOnlyTheDirectDamage()
    {
        var (shooter, target, _) = Crowd();
        var before = Life(target);
        Attack(shooter, target);
        Steps(1);

        // Осколки по самой цели не складываются с попаданием: ровно урон пушки, ни единицей больше.
        Assert.Equal(before - 200, Life(target), 6);
        Assert.DoesNotContain(Shots(shooter), s => s.W == Combat.SplashWeapon && s.To == target.Id);
    }

    [Fact]
    public void AMissDoesNotExplode()
    {
        _roll = Miss;
        var (shooter, target, neighbour) = Crowd(weapon: "dud");
        var before = Life(neighbour);
        Attack(shooter, target);
        Steps(1);

        // Площадь считается от попадания по цели: промахом мимо кучи по ней не ударишь.
        Assert.Contains(Shots(shooter), s => !s.Hit);
        Assert.Equal(before, Life(neighbour));
        Assert.DoesNotContain(Shots(shooter), s => s.W == Combat.SplashWeapon);
    }

    [Fact]
    public void TheFartherNeighbourTakesLess()
    {
        var shooter = Connect("A", "blaster");
        Place(shooter, 0, 0);
        var target = Raider(0, 0, -300);
        var near = Raider(1, 60, -300);
        var far = Raider(2, 150, -300);
        var (nearBefore, farBefore) = (Life(near), Life(far));

        Attack(shooter, target);
        Steps(1);

        var nearLost = nearBefore - Life(near);
        var farLost = farBefore - Life(far);
        Assert.True(nearLost > farLost, $"ближний потерял {nearLost}, дальний {farLost}");
        Assert.True(farLost > 0);
    }

    [Fact]
    public void BeyondTheRadiusNobodyIsTouched()
    {
        var (shooter, target, neighbour) = Crowd(gap: 500);
        var before = Life(neighbour);
        Attack(shooter, target);
        Steps(1);
        Assert.Equal(before, Life(neighbour));
    }

    [Fact]
    public void AWeaponWithoutARadiusNeverSplashes()
    {
        var (shooter, target, neighbour) = Crowd(weapon: "plain");
        var before = Life(neighbour);
        Attack(shooter, target);
        Steps(1);
        Assert.Equal(before, Life(neighbour));
        Assert.DoesNotContain(Shots(shooter), s => s.W == Combat.SplashWeapon);
    }

    [Fact]
    public void ATraderNextToThePirateIsUnharmed_AndTakesNoOffence()
    {
        _room = NewRoom(traders: true);
        Steps(1); // торговец появляется на первом шаге, а не при сборке комнаты
        var shooter = Connect("A", "blaster");
        Place(shooter, 0, 0);
        var pirate = Raider(0, 0, -300);
        var merchant = Merchant(40, -300);
        var before = Life(merchant);

        Attack(shooter, pirate);
        Steps(1);

        Assert.Equal(before, Life(merchant));
        // Главное: осколки не делают стрелка обидчиком — иначе за соседа сняли бы репутацию
        // и на пилота пошли бы рейнджеры. (Сами пираты на торговца охотятся, это не наше дело.)
        Assert.NotEqual(IdOf(shooter), merchant.LastAttackerId);
        Assert.DoesNotContain(Shots(shooter), s => s.From == IdOf(shooter) && s.To == merchant.Id);
    }

    [Fact]
    public void ARangerNextToThePirateIsUnharmed()
    {
        var shooter = Connect("A", "blaster");
        Place(shooter, 0, 0);
        var pirate = Raider(0, 0, -300);
        var ranger = Ranger(40, -300);
        var before = Life(ranger);

        Attack(shooter, pirate);
        Steps(1);

        Assert.Equal(before, Life(ranger));
        Assert.NotEqual(IdOf(shooter), ranger.LastAttackerId);
        Assert.DoesNotContain(Shots(shooter), s => s.From == IdOf(shooter) && s.To == ranger.Id);
    }

    [Fact]
    public void AMeteorNextToThePirateIsUnharmed()
    {
        _room = NewRoom(meteors: new MeteorRules(
            SpawnIntervalSeconds: 999,
            MaxAlive: 0,
            Sizes: new Dictionary<string, MeteorSize> { ["small"] = new("Мелкий метеорит", 14, 600, 0, 0, 110, 1, "rock") }));
        var shooter = Connect("A", "blaster");
        Place(shooter, 0, 0);
        var pirate = Raider(0, 0, -300);
        var rock = _room.LaunchMeteor("small", 40, -300, 0, 0)!;
        var before = Life(rock);

        Attack(shooter, pirate);
        Steps(1);

        // Камни осколками не бьём: иначе площадь стала бы лучшей киркой, а «охота» M14 требует расстрела.
        Assert.Equal(before, Life(rock));
    }

    [Fact]
    public void ThePilotWhoSplashedTheKill_IsCreditedForIt()
    {
        var shooter = Connect("A", "blaster");
        Place(shooter, 0, 0);
        var target = Raider(0, 0, -300);
        var neighbour = Raider(1, 20, -300);
        neighbour.Hp = 1;
        neighbour.Shield = 0;

        Attack(shooter, target);
        Steps(1);

        Assert.True(neighbour.Hp <= 0, "сосед с одним хитом должен погибнуть от осколков");
        Assert.Equal(IdOf(shooter), neighbour.KilledBy);
    }

    [Fact]
    public void ATorpedoExplodesToo_WhenItArrives()
    {
        var shooter = Connect("A", "torpedo");
        Place(shooter, 0, 0);
        var target = Raider(0, 0, -300);
        var neighbour = Raider(1, 60, -300);
        var before = Life(neighbour);

        Attack(shooter, target);
        Steps(2 * SimConfig.TickRate); // торпеде надо долететь

        Assert.True(Life(neighbour) < before, "взрыв торпеды должен задеть соседа");
        Assert.Contains(Shots(shooter), s => s.W == Combat.SplashWeapon && s.To == neighbour.Id);
    }

    [Fact]
    public void AnotherPilotWithPvpOffIsNotSplashed()
    {
        var shooter = Connect("A", "blaster");
        var bystander = Connect("B");
        Place(shooter, 0, 0);
        var pirate = Raider(0, 0, -300);
        Place(bystander, 40, -300);
        _room.SetPvp(bystander, false);
        var before = Life(Ship(bystander));

        Attack(shooter, pirate);
        Steps(1);

        // Строже прямого огня и намеренно: прицельно по нему попасть можно, осколками — нет.
        Assert.Equal(before, Life(Ship(bystander)));
    }
}
