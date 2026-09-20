using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// M11 в комнате: зенитка сбивает ракеты и торпеды, ремонтный блок чинит корпус вне боя, охлаждение ускоряет
/// перезарядку, снаряжение из лута ложится на склад, а купить можно только то, что продают в этой системе.
/// </summary>
public class M11RoomTests
{
    private const double Hit = 0;
    private const double Miss = 0.999;

    private static readonly CombatRules Rules = new(RespawnSeconds: 2, ProtectionSeconds: 0, ShieldRegenDelay: 1, SpawnJitter: 0, RepairDelay: 1);

    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["pulse"] = new("Импульсная пушка", 100, 75, 1.0, 500, 700, 10, Arc: 180, Power: 15),
        ["rockets"] = new(
            "Ракетница", 200, 100, 1.0, 900, 900, 0, Arc: 180, Kind: WeaponParams.MissileKind, Power: 20,
            Missile: new MissileParams(Speed: 300, TurnRate: 120, Lifetime: 5, HitRadius: 10)),
        ["torpedoes"] = new(
            "Торпедный аппарат", 600, 100, 8.0, 900, 900, 0, Arc: 180, Kind: WeaponParams.MissileKind, Power: 40,
            Missile: new MissileParams(Speed: 200, TurnRate: 60, Lifetime: 9, HitRadius: 16, Hp: 150, Sprite: "torpedo")),
        ["flak"] = new(
            "Зенитная автопушка", 60, 70, 0.6, 350, 450, 30, Arc: 180, Kind: "flak", Power: 12,
            Intercept: new InterceptParams(Range: 400, Chance: 55)),
        // Убивает с одного попадания: тестовому дрону надо только упасть и оставить лут.
        ["doom"] = new("Тестовая пушка", 100_000, 100, 1.0, 500, 700, 0, Arc: 180),
    };

    private static readonly IReadOnlyDictionary<string, ModuleParams> Modules = new Dictionary<string, ModuleParams>
    {
        ["engineS"] = new("Двигатель", Fitting.EngineSlot, EquipClass.S, Power: 5),
        ["shieldS"] = new("Щит", Fitting.ShieldSlot, EquipClass.S, Power: 10, Shield: 150, ShieldRegen: 20),
        ["radarS"] = new("Радар", Fitting.RadarSlot, EquipClass.S, Power: 5, Radar: TestBalance.Radar),
        ["generatorS"] = new("Генератор", Fitting.GeneratorSlot, EquipClass.S, Output: 300),
        ["repair"] = new("Ремонтный блок", Fitting.UtilityKind, EquipClass.S, Power: 10, Repair: 20),
        ["cooling"] = new("Охлаждение", Fitting.UtilityKind, EquipClass.S, Power: 12, Cooling: 0.5),
    };

    private static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams>
    {
        ["light"] = new(
            "Лёгкий", 165, 180, 220, 150, 0.65, 0, 16, Hp: 400, Shield: 150, ShieldRegen: 20, Evasion: 0, MoveEvasion: 0,
            Cargo: 20, Radar: TestBalance.Radar, Class: EquipClass.S, WeaponSlots: ["S", "S"], UtilitySlots: 2),
    };

    private double _roll = Hit;
    private int _nextConnection;
    private Room _room;

    public M11RoomTests() => _room = NewRoom();

    private Room NewRoom(ShopRules? shop = null, LootRules? loot = null) => new(
        new Balance(Hulls, Weapons, Rules, Loots: loot, ShopSet: shop, Modules: Modules),
        NullLogger.Instance,
        () => _roll,
        loot: new Random(1));

    private FakeConnection Connect(string name = "Pilot", string? weapon = null, string? token = null)
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, token, name, null, weapon);
        _room.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
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

    /// <summary>Ракетчик A в 500 от цели B: ракета летит секунду с небольшим.</summary>
    private (FakeConnection A, FakeConnection B) Duel(string attackerWeapon, string defenderWeapon)
    {
        var a = Connect("A", attackerWeapon);
        var b = Connect("B", defenderWeapon);
        Place(a, 0, 0);
        Place(b, 0, -500);
        return (a, b);
    }

    [Fact]
    public void PointDefense_ShootsDownAnIncomingMissile()
    {
        var (a, b) = Duel("rockets", "flak");
        Attack(a, b);
        Steps(40);
        Assert.Empty(_room.Missiles);
        Assert.Equal(PlayerOf(b).MaxHp(Hulls["light"]), PlayerOf(b).Hp); // до корпуса не долетело
        Assert.Equal(PlayerOf(b).MaxShield(Hulls["light"]), PlayerOf(b).Shield);
        // Зенитка отчиталась выстрелом по ракете: цель выстрела — id ракеты, а не корабля.
        var shots = b.Messages.OfType<SnapshotMsg>().SelectMany(s => s.Shots ?? []).Where(s => s.W == "flak").ToList();
        Assert.NotEmpty(shots);
        Assert.All(shots, s => Assert.NotEqual(IdOf(a), s.To));
    }

    [Fact]
    public void WithoutPointDefense_TheMissileGetsThrough()
    {
        var (a, b) = Duel("rockets", "pulse");
        _room.SetFire(b, false);
        Attack(a, b);
        Steps(40);
        Assert.True(PlayerOf(b).Shield < PlayerOf(b).MaxShield(Hulls["light"]));
    }

    [Fact]
    public void AMissedInterceptLetsTheMissileFly()
    {
        var (a, b) = Duel("rockets", "flak");
        Attack(a, b);
        _roll = Miss;
        Steps(40);
        Assert.True(PlayerOf(b).Shield < PlayerOf(b).MaxShield(Hulls["light"]));
    }

    [Fact]
    public void ATorpedoTakesSeveralHitsToShootDown()
    {
        var (a, b) = Duel("torpedoes", "flak");
        Attack(a, b);
        Steps(40); // торпеда пущена, дошла до радиуса зенитки и получила своё
        var shots = b.Messages.OfType<SnapshotMsg>().SelectMany(s => s.Shots ?? []).Count(s => s.W == "flak" && s.Hit);
        Assert.Equal(3, shots); // 150 прочности по 60 за попадание
        Assert.Empty(_room.Missiles);
        Assert.Equal(PlayerOf(b).MaxShield(Hulls["light"]), PlayerOf(b).Shield);
    }

    [Fact]
    public void RepairModule_HealsTheHullOutOfCombat()
    {
        var a = Connect("A", "pulse");
        var player = PlayerOf(a);
        player.Fit = player.Fit.With("u0", "repair");
        player.Hp = 100;
        Steps(60); // задержка ремонта 1 с, дальше 20 hp/с
        Assert.InRange(player.Hp, 140, 200);
    }

    [Fact]
    public void WithoutARepairModule_TheHullStaysBroken()
    {
        var a = Connect("A", "pulse");
        var player = PlayerOf(a);
        player.Hp = 100;
        Steps(60);
        Assert.Equal(100, player.Hp);
    }

    [Fact]
    public void Cooling_MakesTheGunFireMoreOften()
    {
        var (a, b) = Duel("pulse", "pulse");
        _room.SetFire(b, false);
        PlayerOf(a).Fit = PlayerOf(a).Fit.With("u0", "cooling"); // −50 %: перезарядка 1 с → 0.5 с
        Attack(a, b);
        Steps(41);
        var shots = b.Messages.OfType<SnapshotMsg>().SelectMany(s => s.Shots ?? []).Count(s => s.From == IdOf(a));
        Assert.InRange(shots, 4, 5); // без охлаждения было бы 2–3
    }

    /// <summary>Учебный дрон роняет зенитку: снаряжение в таблице лута — это M11.</summary>
    private static LootRules GearLoot() => new(
        LifetimeSeconds: 60,
        FadeSeconds: 0,
        Items: new Dictionary<string, LootItem> { ["metal"] = new("Металл", Volume: 1, Price: 10) },
        Tables: new Dictionary<string, LootTable> { ["gear"] = new([new LootRoll("flak", 1, 1, 1)]) })
    {
        Gear = new HashSet<string> { "flak" },
    };

    [Fact]
    public void GrabbedGear_GoesToTheStorageNotTheHold()
    {
        var rules = Rules with { Drones = [new DroneSpec("Мишень", "light", 0, -300, Table: "gear")] };
        _room = new Room(
            new Balance(Hulls, Weapons, rules, Loots: GearLoot(), Modules: Modules),
            NullLogger.Instance, () => _roll, loot: new Random(1));

        var a = Connect("A", "doom");
        Place(a, 0, 0);
        var drone = a.Last<PlayersMsg>().Players.Single(p => p.Kind == Protocol.DroneKind).Id;
        _room.SetTarget(a, drone);
        _room.SetFire(a, true);
        Steps(3);

        var drop = a.Last<SnapshotMsg>().Loot!.Single();
        Assert.Equal("flak", drop.I);
        _room.Entity(IdOf(a))!.Ship = new ShipState { X = drop.X, Y = drop.Y };
        _room.SetLootTarget(a, drop.Id);
        _room.Grab(a);
        _room.Step();

        var player = PlayerOf(a);
        Assert.Equal(1, player.Storage.GetValueOrDefault("flak"));
        Assert.True(player.Cargo.IsEmpty); // снаряжение места в трюме не занимает
    }

    private static ShopRules Shop() => new(
        StartCredits: 100_000,
        Hulls: new Dictionary<string, int> { ["light"] = 0 },
        Items: new Dictionary<string, int> { ["pulse"] = 300, ["flak"] = 900 },
        Stock: ["pulse"]);

    [Fact]
    public void Buying_WhatTheStationDoesNotSell_IsRefused()
    {
        _room = NewRoom(shop: Shop());
        var a = Connect("A", "pulse", token: "buyer-token-1");
        Place(a, 0, 0); // у станции, иначе док не даётся
        _room.Dock(a, true);
        _room.Buy(a, Protocol.ItemKind, "flak");
        Assert.Equal(Protocol.NotSoldNotice, a.Messages.OfType<NoticeMsg>().Last().Code);
        Assert.Empty(PlayerOf(a).Storage);

        _room.Buy(a, Protocol.ItemKind, "pulse");
        Assert.Equal("pulse", PlayerOf(a).Fit.Get("w1")); // а импульсная здесь есть — встала в свободный слот
    }
}
