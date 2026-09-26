using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Налётчики в пути не бросают своих (плейтест 2026-09-26): волна обороны летит к поселению, и если пилот
/// сцепился с одним, остальные рядом разворачиваются к нему, а не летят дальше, будто ничего не случилось.
/// </summary>
public sealed class RaidAssistTests
{
    private const string Settlement = "pl:terra";

    private static readonly GalaxyRules Rules = new(
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new(
                "Home",
                Gates: [new GateDef("far", 3000, 0)],
                Planets: [new PlanetDef("Терра", "terran", 150, new OrbitDef(Radius: 2000, PeriodMinutes: 600), "terra", new SettlementDef("Новый Порт"))]),
            ["far"] = new("Far", Gates: [new GateDef("home", -3000, 0)]),
        },
        Links: [new LinkDef("home", "far", 10)]);

    private static readonly NpcRules Npcs = new(
        RespawnSeconds: 1,
        Types: new Dictionary<string, NpcType>
        {
            ["pirate"] = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320),
        });

    /// <summary>Одна волна из трёх: этого хватает, чтобы увидеть, кто кому помогает.</summary>
    private static readonly MissionRules Defend = new(
        Offers: 4,
        DangerBonus: 0,
        Defend: [new DefendTemplate(Waves: 1, Strikes: 3, Reward: 200, PerWave: 100, Radius: 1400, AwaySeconds: 30, GapSeconds: 0)],
        Ambush: [[new InvasionGroup("pirate", 1, 3)]]);

    private readonly Galaxy _galaxy;

    public RaidAssistTests()
    {
        var balance = TestBalance.Create(
            new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0),
            Npcs,
            new LootRules(StationRange: 200),
            shop: new ShopRules(StartCredits: 1000)) with
        {
            GalaxySet = Rules,
            MissionSet = Defend,
        };
        _galaxy = new Galaxy(balance, NullLogger.Instance, roll: () => 0, random: seed => new Random(seed));
    }

    private Room Room => _galaxy["home"];

    [Fact]
    public void HittingOneRaider_BringsTheRestOfTheWaveOnYou()
    {
        var c = new FakeConnection(1);
        _galaxy.Join(c, null, "Alice", null);
        _galaxy.Undock(c); // вход в доке (M15.6), а садиться надо в поселение
        var player = Room.Pilot(c.Last<WelcomeMsg>().Id)!;
        (player.Ship.X, player.Ship.Y) = Room.PlacePosition(Room.Balance.Place(Settlement)!);
        Room.Dock(c, true, Settlement);
        var offer = c.Last<MissionsMsg>().Offers.First(o => o.Kind == MissionRules.DefendKind);
        Room.Mission(c, Protocol.AcceptMission, offer.Id);
        Room.Dock(c, false);
        _galaxy.Step();

        var raiders = Room.Pirates.Where(p => p.MissionId != 0 && !p.IsDead).ToList();
        Assert.Equal(3, raiders.Count);
        Assert.All(raiders, r => Assert.Equal(PirateState.Return, r.State));

        // Звено держится кучкой в пути, пилот встречает его на подлёте и бьёт первого.
        var (lx, ly) = (raiders[0].Ship.X, raiders[0].Ship.Y);
        for (var i = 1; i < raiders.Count; i++) (raiders[i].Ship.X, raiders[i].Ship.Y) = (lx + 150 * i, ly + 100);
        (player.Ship.X, player.Ship.Y) = (lx - 300, ly);
        raiders[0].LastAttackerId = player.Id;

        _galaxy.Step();
        Assert.Equal((PirateState.Attack, player.Id), (raiders[0].State, raiders[0].TargetId));

        _galaxy.Step();
        Assert.All(raiders, r => Assert.Equal((PirateState.Attack, player.Id), (r.State, r.TargetId)));
    }
}
