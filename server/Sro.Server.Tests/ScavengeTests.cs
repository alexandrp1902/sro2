using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Кто подбирает брошенный груз (GDD §31). До M15.1 это умели только пираты, и добро, выпавшее у станции
/// или у врат, висело в пустоте до истечения срока. Теперь рейнджеры и торговцы тоже берут то, что по пути.
/// </summary>
public sealed class ScavengeTests
{
    private const double LairX = 0;
    private const double LairY = -2500;
    private const double PostX = 0;
    private const double PostY = 2500;

    private static readonly CombatRules Rules = new(RespawnSeconds: 5, ProtectionSeconds: 0, SpawnJitter: 0);

    private static readonly NpcRules Npcs = new(
        RespawnSeconds: 5,
        PatrolRadius: 1200,
        Types: new Dictionary<string, NpcType>
        {
            ["pirate"] = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320),
            ["ranger"] = new("Рейнджер", "light", "pulse", Faction: NpcType.RangerFaction,
                Hp: 600, Shield: 200, Damage: 0.45, HoldRange: 360, DefendRange: 2500, LeashRange: 3500),
            ["trader"] = new("Торговец", "light", "pulse", Faction: NpcType.TraderFaction, Hp: 400, Shield: 100, Damage: 0.3),
        },
        Spawns: [new NpcSpawn("pirate", 1, LairX, LairY), new NpcSpawn("ranger", 1, PostX, PostY)]);

    private static readonly LootRules Loot = new(
        LifetimeSeconds: 600,
        FadeSeconds: 0,
        DropRadius: 1,
        PickupRange: 120,
        Items: new Dictionary<string, LootItem> { ["metal"] = new("Металл", Volume: 1, Price: 10) });

    private readonly Room _room;
    private int _nextConnection;

    public ScavengeTests()
    {
        var galaxy = new GalaxyRules(Systems: new Dictionary<string, SystemDef>
        {
            ["sol"] = new SystemDef(
                "Sol",
                Traders: new TraderRules(Count: 1, RespawnSeconds: 5, Type: "trader", Throttle: 1),
                Gates: [new GateDef("far", 4000, 0)]),
            ["far"] = new SystemDef("Far", Gates: [new GateDef("sol", -4000, 0)]),
        });
        _room = new Room(
            TestBalance.Create(Rules, Npcs, Loot) with { GalaxySet = galaxy },
            NullLogger.Instance,
            ai: new Random(1),
            loot: new Random(1));
    }

    private FakeConnection Watcher()
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, null, "Watcher", null);
        return connection;
    }

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private Pirate Pirate(Func<Pirate, bool> match) => _room.Pirates.First(match);

    private Trader Trader() => _room.Traders.First();

    /// <summary>Кладёт стопку прямо под нос кораблю и даёт комнате тики, чтобы её заметили и забрали.</summary>
    private void DropUnder(ShipEntity ship, string item, int count)
    {
        _room.SpillAt(item, count, ship.Ship.X, ship.Ship.Y);
        Steps(3);
    }

    [Fact]
    public void ARangerOnPatrol_PicksUpWhatItFinds()
    {
        Watcher();
        Steps(1);
        var ranger = Pirate(p => p.Type.IsRanger);
        ranger.Ship = new ShipState { X = PostX, Y = PostY };

        DropUnder(ranger, "metal", 2);

        Assert.Equal(2, ranger.Hold.Count("metal"));
    }

    [Fact]
    public void APirateStillPicksUpToo()
    {
        // Старое поведение не потеряно: обобщение делалось не вместо него.
        Watcher();
        Steps(1);
        var pirate = Pirate(p => p.Type.IsPirate);
        pirate.Ship = new ShipState { X = LairX, Y = LairY };

        DropUnder(pirate, "metal", 2);

        Assert.Equal(2, pirate.Hold.Count("metal"));
    }

    [Fact]
    public void ATraderPicksUpWhatLiesOnItsWay()
    {
        Watcher();
        Steps(1);
        var trader = Trader();

        DropUnder(trader, "metal", 2);

        Assert.Equal(2, trader.Hold.Count("metal"));
    }

    [Fact]
    public void ARangerKilled_SpillsWhatItCollected()
    {
        // Сборщика можно ограбить — как пирата: подобранное высыпается вместе с его таблицей.
        var a = Watcher();
        Steps(1);
        var ranger = Pirate(p => p.Type.IsRanger);
        ranger.Ship = new ShipState { X = PostX, Y = PostY };
        DropUnder(ranger, "metal", 3);
        Assert.Equal(3, ranger.Hold.Count("metal"));

        ranger.Hp = 0;
        Steps(2);

        var loose = (a.Last<SnapshotMsg>().Loot ?? []).Where(d => d.I == "metal").Sum(d => d.N);
        Assert.Equal(3, loose);
    }

    [Fact]
    public void ATraderKilled_SpillsWhatItCollected()
    {
        var a = Watcher();
        Steps(1);
        var trader = Trader();
        DropUnder(trader, "metal", 3);
        Assert.Equal(3, trader.Hold.Count("metal"));

        trader.Hp = 0;
        Steps(2);

        var loose = (a.Last<SnapshotMsg>().Loot ?? []).Where(d => d.I == "metal").Sum(d => d.N);
        Assert.True(loose >= 3, $"выпало {loose}");
    }

    [Fact]
    public void NobodyTakesGearOutOfSpace()
    {
        // Снаряжение NPC ни к чему: у пушки нет объёма, и трюм его не считает.
        Watcher();
        Steps(1);
        var ranger = Pirate(p => p.Type.IsRanger);
        ranger.Ship = new ShipState { X = PostX, Y = PostY };

        DropUnder(ranger, "pulse", 1);

        Assert.True(ranger.Hold.IsEmpty);
    }
}
