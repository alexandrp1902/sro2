using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Флот M19 в комнате: дробовик стреляет веером за одно нажатие, залп поднимает несколько ракет и зенитка
/// снимает их по одной, «Тягач» берёт груз с двойного расстояния и не бьётся о камни.
/// </summary>
public class M19RoomTests
{
    private const double Hit = 0;
    private const double Miss = 0.999;

    private static readonly CombatRules Rules = new(RespawnSeconds: 2, ProtectionSeconds: 0, ShieldRegenDelay: 1, SpawnJitter: 0, RepairDelay: 1);

    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["pulse"] = new("Импульсная пушка", 100, 75, 1.0, 500, 700, 10, Arc: 180, Power: 15),
        // Дробовик: пять дробин по 20 за нажатие. Урона нарочно мало — тесту важно число бросков, а не убийство.
        ["shotgun"] = new("Дробовик", 20, 75, 1.0, 500, 700, 10, Arc: 180, Power: 15, Pellets: 5, Spread: 14),
        ["salvo"] = new(
            "Ракетный залп", 80, 100, 6.0, 900, 900, 0, Arc: 180, Kind: WeaponParams.MissileKind, Power: 40,
            Salvo: 4, Missile: new MissileParams(Speed: 300, TurnRate: 120, Lifetime: 5, HitRadius: 10)),
        ["flak"] = new(
            "Зенитная автопушка", 60, 70, 0.6, 350, 450, 30, Arc: 180, Kind: "flak", Power: 12,
            Intercept: new InterceptParams(Range: 400, Chance: 55)),
    };

    private static readonly IReadOnlyDictionary<string, ModuleParams> Modules = new Dictionary<string, ModuleParams>
    {
        ["engineS"] = new("Двигатель", Fitting.EngineSlot, EquipClass.S, Power: 5),
        ["shieldS"] = new("Щит", Fitting.ShieldSlot, EquipClass.S, Power: 10, Shield: 150, ShieldRegen: 20),
        ["radarS"] = new("Радар", Fitting.RadarSlot, EquipClass.S, Power: 5, Radar: TestBalance.Radar),
        ["generatorS"] = new("Генератор", Fitting.GeneratorSlot, EquipClass.S, Output: 300),
        ["grapple"] = new("Грузовой захват", Fitting.UtilityKind, EquipClass.S, Power: 8, Grab: 1.6),
    };

    private static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams>
    {
        ["light"] = new(
            "Лёгкий", 165, 180, 220, 150, 0.65, 0, 16, Hp: 4000, Shield: 150, ShieldRegen: 20, Evasion: 0, MoveEvasion: 0,
            Cargo: 20, Radar: TestBalance.Radar, Class: EquipClass.S, WeaponSlots: ["S", "S"], UtilitySlots: 2),
        // Тестовый «Тягач»: та же машина, но с особенностью корпуса.
        ["tug"] = new(
            "Тягач", 118, 85, 110, 70, 1.3, 0, 16, Hp: 4000, Shield: 150, ShieldRegen: 20, Evasion: 0, MoveEvasion: 0,
            Cargo: 60, Radar: TestBalance.Radar, Class: EquipClass.S, WeaponSlots: ["S"], UtilitySlots: 2,
            Perk: new HullPerk(Grab: 2, Ram: true)),
    };

    private static readonly LootRules Loot = new(PickupRange: 100, Items: new Dictionary<string, LootItem>
    {
        ["metal"] = new("Металл", Volume: 1, Price: 10),
    });

    private double _roll = Hit;
    private int _nextConnection;
    private readonly Room _room;

    public M19RoomTests() => _room = new Room(
        new Balance(Hulls, Weapons, Rules, Loots: Loot, Modules: Modules),
        NullLogger.Instance,
        () => _roll,
        loot: new Random(1));

    private FakeConnection Connect(string name = "Pilot", string? weapon = null)
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, null, name, null, weapon);
        _room.Undock(connection);
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Player PlayerOf(FakeConnection connection) => _room.Pilot(IdOf(connection))!;

    private void Place(FakeConnection connection, double x, double y) =>
        _room.Entity(IdOf(connection))!.Ship = new ShipState { X = x, Y = y };

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private void Attack(FakeConnection attacker, FakeConnection target)
    {
        _room.SetTarget(attacker, IdOf(target));
        _room.SetFire(attacker, true);
    }

    private List<ShotDto> ShotsOf(FakeConnection watcher, string weapon) =>
        [.. watcher.Messages.OfType<SnapshotMsg>().SelectMany(s => s.Shots ?? []).Where(s => s.W == weapon)];

    // --- дробовик ---

    /// <summary>Пять дробин — пять строк в ленте за одно нажатие, и перезарядка при этом одна.</summary>
    [Fact]
    public void AShotgun_FiresEveryPelletInOneTick()
    {
        var a = Connect("A", "shotgun");
        var b = Connect("B", "pulse");
        Place(a, 0, 0);
        Place(b, 0, -300);
        _room.SetFire(b, false);
        Attack(a, b);
        Steps(1);

        Assert.Equal(5, ShotsOf(a, "shotgun").Count);
    }

    /// <summary>Промахи считаются подробно: часть дробин попала, часть нет — это и есть веер.</summary>
    [Fact]
    public void AShotgun_RollsForEveryPelletSeparately()
    {
        var a = Connect("A", "shotgun");
        var b = Connect("B", "pulse");
        Place(a, 0, 0);
        Place(b, 0, -300);
        _room.SetFire(b, false);
        Attack(a, b);

        _roll = Miss;
        Steps(1);
        var shots = ShotsOf(a, "shotgun");
        Assert.Equal(5, shots.Count);
        Assert.All(shots, s => Assert.False(s.Hit)); // бросок один на всех — значит мимо все пять
        Assert.Equal(PlayerOf(b).MaxShield(Hulls["light"]), PlayerOf(b).Shield);
    }

    /// <summary>Урон складывается: пять дробин по 20 снимают 100 за нажатие, а не 20.</summary>
    [Fact]
    public void AShotgun_AddsUpItsPellets()
    {
        var a = Connect("A", "shotgun");
        var b = Connect("B", "pulse");
        Place(a, 0, 0);
        Place(b, 0, -300);
        _room.SetFire(b, false);
        Attack(a, b);
        Steps(1);

        var dealt = ShotsOf(a, "shotgun").Sum(s => s.Dmg);
        Assert.Equal(100, dealt);
    }

    // --- залп ---

    [Fact]
    public void ASalvo_LaunchesEveryRocket()
    {
        var a = Connect("A", "salvo");
        var b = Connect("B", "pulse");
        Place(a, 0, 0);
        Place(b, 0, -800);
        _room.SetFire(b, false);
        Attack(a, b);
        Steps(1);

        Assert.Equal(4, _room.Missiles.Count);
    }

    /// <summary>Четыре ракеты уходят веером, а не одной точкой: иначе их и не разглядеть.</summary>
    [Fact]
    public void ASalvo_FansItsRocketsOut()
    {
        var a = Connect("A", "salvo");
        var b = Connect("B", "pulse");
        Place(a, 0, 0);
        Place(b, 0, -800);
        _room.SetFire(b, false);
        Attack(a, b);
        Steps(1);

        var angles = _room.Missiles.Select(m => Math.Round(m.State.Rot, 4)).Distinct().ToList();
        Assert.Equal(4, angles.Count);
    }

    /// <summary>Зенитка снимает залп по частям: одну сбила — три летят дальше.</summary>
    [Fact]
    public void FlakTakesTheSalvoApart_OneRocketAtATime()
    {
        var a = Connect("A", "salvo");
        var b = Connect("B", "flak");
        Place(a, 0, 0);
        Place(b, 0, -800);
        Attack(a, b);
        Steps(1);
        Assert.Equal(4, _room.Missiles.Count);

        // Ракетам 800 − 400 = 400 единиц до радиуса зенитки, на 300 в секунду это чуть больше 26 тиков.
        Steps(40);
        Assert.True(_room.Missiles.Count < 4, "хотя бы одну зенитка должна была снять");
        Assert.NotEmpty(ShotsOf(b, "flak"));
    }

    // --- захват и таран ---

    [Fact]
    public void ATug_GrabsFromTwiceAsFar()
    {
        var tug = Connect("Tug");
        var light = Connect("Light");
        PlayerOf(tug).HullId = "tug";

        Assert.Equal(Loot.PickupRange * 2, PlayerOf(tug).GrabRange(_room.Balance), 3);
        Assert.Equal(Loot.PickupRange, PlayerOf(light).GrabRange(_room.Balance), 3);
    }

    [Fact]
    public void AGrapple_MultipliesTheTugsReachFurther()
    {
        var tug = Connect("Tug");
        var player = PlayerOf(tug);
        player.HullId = "tug";
        player.Fit = player.Fit with { Utility = ["grapple"] };

        Assert.Equal(Loot.PickupRange * 2 * 1.6, player.GrabRange(_room.Balance), 3);
    }

    /// <summary>Бампер «Тягача» держит камень; обычному корпусу тот же камень стоит прочности.</summary>
    [Fact]
    public void ATug_ShrugsOffAMeteor()
    {
        var hulls = _room.Balance.Hulls;
        Assert.True(hulls["tug"].Perk!.Ram);
        Assert.Null(hulls["light"].Perk);
    }
}
