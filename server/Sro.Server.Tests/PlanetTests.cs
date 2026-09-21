using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Посадка на планету (M15): поселение — такое же место, как станция, только своё. Здесь проверяется,
/// что комната различает два места в одной системе и не путает их витрину, склад и память.
/// </summary>
public sealed class PlanetTests
{
    private const string Station = "st:sol";
    private const string Settlement = "pl:terra";

    /// <summary>Орбита с долгим оборотом: за тики теста планета сдвигается незаметно, но остаётся на орбите.</summary>
    private static readonly OrbitDef PlanetOrbit = new(Radius: 2000, PeriodMinutes: 600, Phase: 0);

    private static readonly ShopRules Shop = new(
        StartCredits: 1000,
        Hulls: new Dictionary<string, int> { ["light"] = 0, ["heavy"] = 900 },
        Items: new Dictionary<string, int> { ["pulse"] = 100 });

    private static readonly LootRules Loot = new(
        StationRange: 200,
        Items: new Dictionary<string, LootItem>
        {
            ["metal"] = new("Металл", Volume: 1, Price: 10),
            ["food"] = new("Еда", Volume: 1, Price: 10),
        });

    private static readonly MarketRules Market = new(
        Goods: new Dictionary<string, MarketGood> { ["metal"] = new(), ["food"] = new() },
        Places: new Dictionary<string, MarketStation>
        {
            [Station] = new(Produces: ["metal"], Consumes: ["food"]),
            [Settlement] = new(Produces: ["food"], Consumes: ["metal"]),
        });

    private readonly Room _room;
    private int _nextConnection;

    public PlanetTests()
    {
        var terra = new PlanetDef("Терра", "terran", 150, PlanetOrbit, "terra", new SettlementDef("Новый Порт"));
        var mars = new PlanetDef("Марс", "desert", 120, new OrbitDef(2800, 900)); // без поселения: садиться некуда
        var galaxy = new GalaxyRules(Systems: new Dictionary<string, SystemDef>
        {
            ["sol"] = new SystemDef("Sol", Planets: [terra, mars]),
        });
        var balance = TestBalance.Create(
            new CombatRules(ProtectionSeconds: 3, SpawnJitter: 0), loot: Loot, shop: Shop, market: Market) with
        {
            GalaxySet = galaxy,
        };
        _room = new Room(balance.ForSystem("sol"), NullLogger.Instance);
    }

    private FakeConnection Pilot()
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, null, "Alice", null);
        _room.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    private Player PlayerOf(FakeConnection connection) => _room.Pilot(connection.Last<WelcomeMsg>().Id)!;

    private PlaceDef Place(string key) => _room.Balance.Place(key)!;

    /// <summary>Ставит корабль вплотную к месту и просит туда встать.</summary>
    private Player At(FakeConnection connection, string key, double offset = 0)
    {
        var player = PlayerOf(connection);
        var (x, y) = _room.PlacePosition(Place(key));
        player.Ship = new ShipState { X = x + offset, Y = y };
        return player;
    }

    [Fact]
    public void ASettledPlanet_IsAPlaceOfItsOwn()
    {
        var places = _room.Balance.Places;

        Assert.Equal([Station, Settlement], places.Select(p => p.Key));
        Assert.Equal("Новый Порт", Place(Settlement).Name);
        // Радиус посадки — дальность станции плюс радиус планеты: подлетать внутрь картинки не нужно.
        Assert.Equal(350, Place(Settlement).Range);
    }

    [Fact]
    public void LandingOnASettlement_PutsTheShipThere_NotAtTheStation()
    {
        var a = Pilot();
        var player = At(a, Settlement);

        _room.Dock(a, true, Settlement);

        Assert.True(player.Docked);
        Assert.Equal(Settlement, player.DockedPlace);
        Assert.Equal(Settlement, player.HomePlace);
        var place = a.Last<HangarMsg>().Place;
        Assert.Equal((Settlement, "pl", "Новый Порт"), (place!.Key, place.Kind, place.Name));
    }

    [Fact]
    public void LandingFromTooFarAway_SaysSo()
    {
        var a = Pilot();
        var player = At(a, Settlement, offset: 400); // радиус посадки 350

        _room.Dock(a, true, Settlement);

        Assert.False(player.Docked);
        Assert.Equal(Protocol.TooFarNotice, a.Last<NoticeMsg>().Code);
    }

    [Fact]
    public void APlanetWithoutASettlement_IsNoPlaceAtAll()
    {
        // Марс на орбите есть, сесть на него нельзя: ключа у него нет, и рядом с ним док не открывается.
        Assert.Null(_room.Balance.Place("pl:mars"));

        var a = Pilot();
        var player = PlayerOf(a);
        var mars = new OrbitDef(2800, 900);
        (player.Ship.X, player.Ship.Y) = mars.At(0);

        _room.Dock(a, true);

        Assert.False(player.Docked);
    }

    [Fact]
    public void TakingOff_LeavesTheShipBesideThePlanet_NotBesideTheStation()
    {
        var a = Pilot();
        var player = At(a, Settlement);
        _room.Dock(a, true, Settlement);

        _room.Dock(a, false);

        var (px, py) = _room.PlacePosition(Place(Settlement));
        Assert.False(player.Docked);
        Assert.Null(player.DockedPlace);
        Assert.True(Math.Sqrt(Math.Pow(player.Ship.X - px, 2) + Math.Pow(player.Ship.Y - py, 2)) < 1, "взлёт должен быть у планеты");
    }

    [Fact]
    public void WithoutAPlaceGiven_TheStationWins()
    {
        // Пробел без выбранной планеты стыкует со станцией, как было до M15.
        var a = Pilot();
        var player = At(a, Station);

        _room.Dock(a, true);

        Assert.Equal(Station, player.DockedPlace);
    }

    [Fact]
    public void EachPlaceKeepsItsOwnPrices()
    {
        // Станция делает металл и скупает еду, поселение — наоборот: короткий маршрут внутри системы.
        var station = _room.Balance.MarketAt(Station);
        var settlement = _room.Balance.MarketAt(Settlement);

        Assert.True(station.Makes("metal"));
        Assert.False(station.Makes("food"));
        Assert.True(settlement.Makes("food"));
        Assert.False(settlement.Makes("metal"));
        // Продаётся при этом и то и другое: витрина — это склад, а не список продукции (M16a).
        Assert.True(station.Sells("food"));
        Assert.True(settlement.Sells("metal"));
    }

    [Fact]
    public void SellingCargo_UsesThePricesOfThePlaceYouStandAt()
    {
        var a = Pilot();
        var player = At(a, Settlement);
        player.Cargo.Add("metal", 10);
        _room.Dock(a, true, Settlement);
        var before = player.Credits;

        _room.Sell(a, "metal");

        // Поселение скупает металл дорого: больше, чем плоская цена в 10 кредитов за штуку.
        Assert.True(player.Credits - before > 100, $"выручка {player.Credits - before}");
        Assert.Empty(player.Cargo.Items);
    }

    [Fact]
    public void ASettlementWithoutAShipyard_DoesNotSellOrSwapHulls()
    {
        var a = Pilot();
        var player = At(a, Settlement);
        _room.Dock(a, true, Settlement);
        player.Credits = 10_000;

        _room.Buy(a, Protocol.HullItem, "heavy");

        Assert.Equal(Protocol.NoShipyardNotice, a.Last<NoticeMsg>().Code);
        Assert.DoesNotContain("heavy", player.Hulls);
        Assert.Equal(10_000, player.Credits);
    }

    [Fact]
    public void ASettlementWithoutAShipyard_DoesNotSwapHullsEither()
    {
        // Спрятать вкладку мало: правило должно стоять там, где его нельзя обойти мимо интерфейса.
        var a = Pilot();
        var player = At(a, Settlement);
        _room.Dock(a, true, Settlement);

        _room.SetHull(a, "heavy");

        Assert.Equal(Protocol.NoShipyardNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal("light", player.HullId);
    }

    [Fact]
    public void TheStationStillSwapsHulls()
    {
        var a = Pilot();
        var player = At(a, Station);
        _room.Dock(a, true, Station);

        _room.SetHull(a, "heavy");

        Assert.Equal("heavy", player.HullId);
    }
}
