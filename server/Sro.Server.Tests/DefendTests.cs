using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Оборона поселения (M15): волны налётчиков идут от врат к планете, пилот их встречает.
/// Работа только планетная — станция такого не предлагает, её обороняют вторжения (M10).
/// </summary>
public sealed class DefendTests
{
    private const string Settlement = "pl:terra";
    private const string Station = "st:home";

    /// <summary>Орбита с долгим оборотом: поселение остаётся примерно там же, но всё-таки едет.</summary>
    private static readonly OrbitDef PlanetOrbit = new(Radius: 2000, PeriodMinutes: 600);

    private static readonly GalaxyRules Rules = new(
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new(
                "Home",
                Gates: [new GateDef("far", 3000, 0)],
                Planets: [new PlanetDef("Терра", "terran", 150, PlanetOrbit, "terra", new SettlementDef("Новый Порт"))]),
            ["far"] = new("Far", Gates: [new GateDef("home", -3000, 0)]),
        },
        Links: [new LinkDef("home", "far", 10)]);

    private static readonly NpcRules Npcs = new(
        RespawnSeconds: 1,
        Types: new Dictionary<string, NpcType>
        {
            ["pirate"] = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320),
        });

    /// <summary>Две волны по одному налётчику и два пропуска: обе цифры малы, чтобы тест шёл без ожидания.</summary>
    private static readonly MissionRules Defend = new(
        Offers: 4,
        DangerBonus: 0,
        Defend: [new DefendTemplate(Waves: 2, Strikes: 2, Reward: 200, PerWave: 100, Radius: 1400, AwaySeconds: 1, GapSeconds: 0)],
        Ambush: [[new InvasionGroup("pirate", 1, 1)]]);

    private readonly Galaxy _galaxy;
    private int _nextConnection;

    public DefendTests()
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

    private FakeConnection Guest()
    {
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.Join(connection, null, "Alice", null);
        return connection;
    }

    private Room Room => _galaxy["home"];

    private Player PlayerOf(FakeConnection c) => Room.Pilot(c.Last<WelcomeMsg>().Id)!;

    private MissionsMsg Missions(FakeConnection c) => c.Last<MissionsMsg>();

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private (double X, double Y) Spot => Room.PlacePosition(Room.Balance.Place(Settlement)!);

    /// <summary>Ставит корабль у поселения и садится туда.</summary>
    private Player Land(FakeConnection c)
    {
        var player = PlayerOf(c);
        (player.Ship.X, player.Ship.Y) = Spot;
        Room.Dock(c, true, Settlement);
        Assert.Equal(Settlement, c.Last<HangarMsg>().Place?.Key);
        return player;
    }

    /// <summary>Взять оборону в поселении и вылететь: волна выходит на вылете, как конвой и звено (M14).</summary>
    private Player Start(FakeConnection c)
    {
        var player = Land(c);
        var offer = Missions(c).Offers.First(o => o.Kind == MissionRules.DefendKind);
        Room.Mission(c, Protocol.AcceptMission, offer.Id);
        Room.Dock(c, false);
        Steps(1);
        return player;
    }

    /// <summary>Налётчики этого налёта, живые и не ушедшие.</summary>
    private List<Pirate> Raiders() => [.. Room.Pirates.Where(p => p.MissionId != 0 && !p.IsDead && !p.Gone)];

    [Fact]
    public void OnlyASettlementOffersIt()
    {
        var a = Guest();
        var player = PlayerOf(a);

        (player.Ship.X, player.Ship.Y) = Room.PlacePosition(Room.Balance.Place(Station)!);
        Room.Dock(a, true, Station);
        Assert.DoesNotContain(Missions(a).Offers, o => o.Kind == MissionRules.DefendKind);

        Room.Dock(a, false);
        Land(a);
        Assert.Contains(Missions(a).Offers, o => o.Kind == MissionRules.DefendKind);
    }

    [Fact]
    public void TheRaidComesFromTheGates_AndHeadsForTheSettlement()
    {
        var a = Guest();

        Start(a);

        var raiders = Raiders();
        Assert.NotEmpty(raiders);
        // Налёт прилетает извне, а не вырастает над крышами: старт у врат, курс на поселение.
        foreach (var raider in raiders)
        {
            var (sx, sy) = Spot;
            var far = Math.Sqrt(Math.Pow(raider.Ship.X - sx, 2) + Math.Pow(raider.Ship.Y - sy, 2));
            Assert.True(far > 1000, $"налётчик появился в {far:0} от поселения");
        }
    }

    [Fact]
    public void ClearingEveryWave_FinishesTheWork()
    {
        var a = Guest();
        var player = Start(a);
        var credits = player.Credits;

        // Две волны по одному: бьём обе. Пилот стоит у поселения, иначе его сочтут сбежавшим.
        for (var wave = 0; wave < 2; wave++)
        {
            (player.Ship.X, player.Ship.Y) = Spot;
            foreach (var raider in Raiders()) raider.Hp = 0;
            Steps(3);
        }

        Assert.Null(Missions(a).Active);
        Assert.Equal(Protocol.MissionDone, Missions(a).Done?.Kind);
        Assert.True(player.Credits > credits, "оборона должна была заплатить");
    }

    [Fact]
    public void LettingThemThrough_LosesTheSettlement()
    {
        var a = Guest();
        var player = Start(a);

        // Два пропуска — предел. Налётчика, дошедшего до поселения, ставим туда руками.
        for (var strike = 0; strike < 2; strike++)
        {
            (player.Ship.X, player.Ship.Y) = Spot;
            var raider = Raiders().FirstOrDefault();
            if (raider is null) break;
            (raider.Ship.X, raider.Ship.Y) = Spot;
            Steps(2);
        }

        Assert.Null(Missions(a).Active);
        Assert.Equal(Protocol.MissionFailed, Missions(a).Done?.Kind);
        Assert.Equal(Protocol.RaidFail, Missions(a).Done?.Reason);
    }

    [Fact]
    public void ARaiderThatGotThrough_DoesNotKeepHitting()
    {
        var a = Guest();
        var player = Start(a);
        (player.Ship.X, player.Ship.Y) = Spot;
        var raider = Raiders().First();

        (raider.Ship.X, raider.Ship.Y) = Spot;
        Steps(1);

        // Ударил и ушёл: иначе один и тот же корабль копил бы удары, стоя на месте.
        Assert.True(raider.Gone);
    }

    [Fact]
    public void FlyingOffAndLeavingItUndefended_FailsTheWork()
    {
        var a = Guest();
        var player = Start(a);
        var (sx, sy) = Spot;

        // Радиус обороны 1400; терпения — секунда, чтобы тест не ждал.
        (player.Ship.X, player.Ship.Y) = (sx + 9000, sy);
        Steps(SimConfig.TickRate + 4);

        Assert.Equal(Protocol.MissionFailed, Missions(a).Done?.Kind);
        Assert.Equal(Protocol.AwayFail, Missions(a).Done?.Reason);
    }

    [Fact]
    public void GoingBackIntoTheDock_GivesTheSettlementUp()
    {
        var a = Guest();
        var player = Start(a);

        // Сесть посреди налёта — это и есть «бросил»: работа начинается на вылете и в доке не идёт.
        (player.Ship.X, player.Ship.Y) = Spot;
        Room.Dock(a, true, Settlement);
        Steps(2);

        Assert.Equal(Protocol.MissionFailed, Missions(a).Done?.Kind);
        Assert.Equal(Protocol.AwayFail, Missions(a).Done?.Reason);
    }

    [Fact]
    public void ItDoesNotSurviveGoingBackToTheDock()
    {
        // Живое задание не восстанавливается из профиля: его актёры остались в прошлом вылете (M14).
        Assert.True(MissionRules.IsLive(MissionRules.DefendKind));
        Assert.True(MissionRules.DiesWithTheShip(MissionRules.DefendKind));
    }
}
