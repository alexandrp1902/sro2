using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Пираты злее (плейтест 2026-09-27): налётчик по дороге к своей точке сам бросается на пилота ближе 2.5 сектора
/// (engageRange), а не пролетает мимо, пока по нему не выстрелят; товарищи подтягиваются издалека.
/// </summary>
public sealed class PirateEngageTests
{
    private const string Settlement = "pl:terra";
    private const double Engage = 1750;

    private static readonly GalaxyRules Rules = new(
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new(
                "Home",
                Gates: [new GateDef("far", 3000, 0)],
                Planets: [new PlanetDef("Терра", "terran", 150, new OrbitDef(Radius: 2000, PeriodMinutes: 600), "terra", new SettlementDef("Терра"))]),
            ["far"] = new("Far", Gates: [new GateDef("home", -3000, 0)]),
        },
        Links: [new LinkDef("home", "far", 10)]);

    private static readonly NpcRules Npcs = new(
        RespawnSeconds: 1,
        AggroRange: 700,
        EngageRange: Engage,
        DropRange: 2100,
        AssistRange: 1750,
        Types: new Dictionary<string, NpcType>
        {
            ["pirate"] = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320),
        });

    private static readonly MissionRules Defend = new(
        Offers: 4,
        DangerBonus: 0,
        Defend: [new DefendTemplate(Waves: 1, Strikes: 3, Reward: 200, PerWave: 100, Radius: 1400, AwaySeconds: 30, GapSeconds: 0)],
        Ambush: [[new InvasionGroup("pirate", 1, 2)]]);

    private readonly Galaxy _galaxy;

    public PirateEngageTests()
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

    [Theory]
    [InlineData(Engage - 50, true)]
    [InlineData(Engage + 50, false)]
    public void ARaiderOnItsWay_AttacksAPilotWithinEngageRange_WithoutBeingShot(double distance, bool attacks)
    {
        var (player, raiders) = StartDefence();
        var raider = raiders[0];
        // Второго уводим далеко: проверяем одного налётчика, без помощи товарища.
        (raiders[1].Ship.X, raiders[1].Ship.Y) = (-3500, -3500);
        (raider.Ship.X, raider.Ship.Y) = (3000, 1500);
        (player.Ship.X, player.Ship.Y) = (3000 - distance, 1500);

        _galaxy.Step();

        Assert.Equal(attacks ? (PirateState.Attack, player.Id) : (PirateState.Return, 0), (raider.State, raider.TargetId));
    }

    [Fact]
    public void AMateFarAway_ComesToHelp()
    {
        var (player, raiders) = StartDefence();
        // Первый рядом с пилотом, второй — в 1500 от первого и дальше 2.5 сектора от пилота: сам его не видит.
        (raiders[0].Ship.X, raiders[0].Ship.Y) = (3000, 1500);
        (player.Ship.X, player.Ship.Y) = (2700, 1500);
        (raiders[1].Ship.X, raiders[1].Ship.Y) = (3000 + 1500, 1500);
        Assert.True(Math.Abs(raiders[1].Ship.X - player.Ship.X) > Engage);

        _galaxy.Step();
        Assert.Equal((PirateState.Attack, player.Id), (raiders[0].State, raiders[0].TargetId));

        _galaxy.Step();
        Assert.Equal((PirateState.Attack, player.Id), (raiders[1].State, raiders[1].TargetId));
    }

    private (Player, List<Pirate>) StartDefence()
    {
        var c = new FakeConnection(1);
        _galaxy.Join(c, null, "Alice", null);
        _galaxy.Undock(c);
        var player = Room.Pilot(c.Last<WelcomeMsg>().Id)!;
        (player.Ship.X, player.Ship.Y) = Room.PlacePosition(Room.Balance.Place(Settlement)!);
        Room.Dock(c, true, Settlement);
        Room.Mission(c, Protocol.AcceptMission, c.Last<MissionsMsg>().Offers.First(o => o.Kind == MissionRules.DefendKind).Id);
        Room.Dock(c, false);
        _galaxy.Step();
        var raiders = Room.Pirates.Where(p => p.MissionId != 0 && !p.IsDead).ToList();
        Assert.Equal(2, raiders.Count);
        Assert.All(raiders, r => Assert.Equal(PirateState.Return, r.State));
        return (player, raiders);
    }
}
