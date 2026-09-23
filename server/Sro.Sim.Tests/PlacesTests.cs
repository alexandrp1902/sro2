namespace Sro.Sim.Tests;

/// <summary>
/// Места (M15): станция и поселение на планете — одно и то же понятие с разными полями.
/// До M15 местом звалась сама система, поэтому здесь же проверяется, что старое поведение
/// (одна станция на систему) осталось ровно таким же.
/// </summary>
public class PlacesTests
{
    private static OrbitDef Far => new(Radius: 2000);

    private static SystemDef WithPlanet(PlanetDef planet, bool station = true) =>
        new("A", Station: station, Sun: new SunDef(BurnRadius: 600), Planets: [planet]);

    [Fact]
    public void Key_RoundTripsAndTellsPlacesFromSystems()
    {
        Assert.Equal("st:sol", PlaceKey.Station("sol"));
        Assert.Equal("pl:terra", PlaceKey.Planet("terra"));
        Assert.Equal("sys:sol", PlaceKey.System("sol"));
        Assert.Equal(("pl", "terra"), PlaceKey.Split("pl:terra"));
        Assert.Equal(("", "sol"), PlaceKey.Split("sol"));

        Assert.True(PlaceKey.IsPlace("st:sol"));
        Assert.True(PlaceKey.IsPlace("pl:terra"));
        Assert.False(PlaceKey.IsPlace("sys:sol")); // система — не место: сесть на неё нельзя
        Assert.False(PlaceKey.IsPlace("sol"));
        Assert.False(PlaceKey.IsPlace(null));
    }

    [Fact]
    public void Key_UpgradesTheBareSystemIdOfOldProfiles()
    {
        // До M15 местом звался голый id системы. Такие ключи ещё лежат в сохранённых профилях.
        Assert.Equal("st:sol", PlaceKey.Upgrade("sol"));
        Assert.Equal("st:sol", PlaceKey.Upgrade("st:sol"));
        Assert.Equal("pl:terra", PlaceKey.Upgrade("pl:terra"));
    }

    [Fact]
    public void ASystemWithoutSettlements_HasJustItsStation()
    {
        var system = new SystemDef("Sol", StationOrbit: Far);

        var places = system.Places("sol", 200);

        var place = Assert.Single(places);
        Assert.Equal("st:sol", place.Key);
        Assert.Equal(PlaceKey.StationKind, place.Kind);
        Assert.Equal("sol", place.Id);
        Assert.Equal("Sol", place.Name);
        Assert.Equal(200, place.Range);
        Assert.False(place.IsPlanet);
        Assert.True(place.Shipyard); // на станции корпуса продают всегда
    }

    [Fact]
    public void ASystemWithoutAStation_HasNoPlaceUntilAPlanetIsSettled()
    {
        var bare = new SystemDef("Tau", Station: false);
        Assert.Empty(bare.Places("tau", 200));

        var settled = new SystemDef("Tau", Station: false, Planets:
            [new PlanetDef("Тау-Прима", "desert", 120, Far, "tauPrima", new SettlementDef("Порт Тау"))]);

        var place = Assert.Single(settled.Places("tau", 200));
        Assert.Equal("pl:tauPrima", place.Key);
        Assert.Equal("Порт Тау", place.Name);
        Assert.True(place.IsPlanet);
    }

    [Fact]
    public void TheStationCarriesItsOwnSetOfDockScenes()
    {
        // Набор сцен станции живёт в galaxy.json рядом со спрайтом, а до клиента едет местом:
        // у каждой станции галактики свои диспетчер и торговец (пачка G).
        var own = new SystemDef("Sol", StationOrbit: Far, DockScene: "ring");
        Assert.Equal("ring", own.Places("sol", 200).Single().Scene);

        // Не указан — клиент возьмёт общие сцены станции.
        var plain = new SystemDef("Sol", StationOrbit: Far);
        Assert.Null(plain.Places("sol", 200).Single().Scene);
    }

    [Fact]
    public void TheStationComesFirst_SoTheNearestPlaceStillPrefersIt()
    {
        var system = WithPlanet(new PlanetDef("Терра", "terran", 150, Far, "terra", new SettlementDef()));

        var places = system.Places("sol", 200);

        Assert.Equal(["st:sol", "pl:terra"], places.Select(p => p.Key));
    }

    [Fact]
    public void LandingRange_GrowsByThePlanetRadius()
    {
        // К планете подлетают снаружи: без её радиуса садиться пришлось бы внутрь картинки.
        var system = WithPlanet(new PlanetDef("Терра", "terran", 150, Far, "terra", new SettlementDef()));

        var planet = system.Places("sol", 200).Single(p => p.IsPlanet);

        Assert.Equal(350, planet.Range);
    }

    [Fact]
    public void ASettlementWithoutAName_IsCalledAfterItsPlanet()
    {
        var system = WithPlanet(new PlanetDef("Терра", "terran", 150, Far, "terra", new SettlementDef()));

        Assert.Equal("Терра", system.Places("sol", 200).Single(p => p.IsPlanet).Name);
    }

    [Fact]
    public void ASettlementNeedsAnId()
    {
        var planet = new PlanetDef("Терра", "terran", 150, Far, Settlement: new SettlementDef());

        Assert.Equal("a settlement needs an id", planet.Validate());
    }

    [Fact]
    public void APlanetWithoutASettlement_StaysScenery()
    {
        var planet = new PlanetDef("Юпитер", "gas", 240, Far);

        Assert.Null(planet.Validate());
        Assert.Empty(WithPlanet(planet).Places("sol", 200).Where(p => p.IsPlanet));
    }

    [Fact]
    public void TwoPlanetsWithTheSameId_AreRejected()
    {
        // Id планеты — ключ места на всю галактику: по нему идут магазин, рынок, задания и репутация.
        var galaxy = new GalaxyRules(StartSystem: "a", Systems: new Dictionary<string, SystemDef>
        {
            ["a"] = WithPlanet(new PlanetDef("Первая", "terran", 100, Far, "twin", new SettlementDef())),
            ["b"] = WithPlanet(new PlanetDef("Вторая", "ice", 100, Far, "twin", new SettlementDef())),
        });

        var error = galaxy.Validate((_, _) => null);

        Assert.Contains("planet id 'twin' is already used", error);
    }

    [Fact]
    public void ASettlementInTheHeatOfTheSun_IsRejected()
    {
        // Та же мерка, что у орбиты станции, плюс радиус самой планеты: садиться в жаре нельзя.
        var close = new OrbitDef(Radius: 600 + GalaxyRules.StationClearance + 50);
        var galaxy = new GalaxyRules(StartSystem: "a", Systems: new Dictionary<string, SystemDef>
        {
            ["a"] = WithPlanet(new PlanetDef("Пекло", "lava", 200, close, "hot", new SettlementDef())),
        });

        var error = galaxy.Validate((_, _) => null);

        Assert.Contains("landing must not burn", error);
    }

    [Fact]
    public void AGasGiant_CanOnlyHoldAnOrbitalPlatform()
    {
        var galaxy = new GalaxyRules(StartSystem: "a", Systems: new Dictionary<string, SystemDef>
        {
            ["a"] = WithPlanet(new PlanetDef("Гигант", "gas", 240, Far, "giant", new SettlementDef(Scene: "jungle"))),
        });

        Assert.Contains("orbital platform", galaxy.Validate((_, _) => null));

        var platform = new GalaxyRules(StartSystem: "a", Systems: new Dictionary<string, SystemDef>
        {
            ["a"] = WithPlanet(new PlanetDef("Гигант", "gas", 240, Far, "giant",
                new SettlementDef(Scene: GalaxyRules.PlatformScene))),
        });

        Assert.Null(platform.Validate((_, _) => null));
    }

    [Fact]
    public void HasPlace_KnowsStationsAndSettlements()
    {
        var galaxy = new GalaxyRules(StartSystem: "a", Systems: new Dictionary<string, SystemDef>
        {
            ["a"] = WithPlanet(new PlanetDef("Терра", "terran", 150, Far, "terra", new SettlementDef())),
            ["b"] = new SystemDef("B", Station: false),
        });

        Assert.True(galaxy.HasPlace("st:a"));
        Assert.True(galaxy.HasPlace("pl:terra"));
        Assert.False(galaxy.HasPlace("st:b")); // станции там нет
        Assert.False(galaxy.HasPlace("pl:ghost"));
        Assert.False(galaxy.HasPlace("a")); // без разметки это не ключ места

        Assert.Equal("a", galaxy.SystemOfPlace("st:a"));
        Assert.Equal("a", galaxy.SystemOfPlace("pl:terra"));
        Assert.Null(galaxy.SystemOfPlace("pl:ghost"));
    }

    [Fact]
    public void SharedGalaxy_GivesEverySystemWithAStationExactlyOnePlace()
    {
        // Пока поселений в данных нет, мест ровно столько же, сколько станций: поведение как до M15.
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);

        foreach (var (id, system) in balance!.Galaxy.SystemMap)
        {
            var view = balance.ForSystem(id);
            Assert.Equal(system.Station, view.HasStation);
            Assert.Equal(system.Station ? 1 : 0, view.Places.Count(p => !p.IsPlanet));
            Assert.Equal(view.Places.Count > 0, view.HasDock);
            if (view.DefaultPlace is { } main) Assert.Same(main, view.Place(main.Key));
        }
    }

    [Fact]
    public void ShopAndMarket_AreLookedUpByPlace()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        var sol = balance!.ForSystem("sol");
        var station = sol.DefaultPlace!.Key;

        Assert.Equal("st:sol", station);
        Assert.Same(sol.ShopAt(station), sol.MainShop);
        Assert.Equal(sol.MainMarket.Station, sol.MarketAt(station).Station);
        // Незнакомое место не торгует ничем: перепутанный ключ не должен молча отдать чужую витрину.
        Assert.Empty(sol.ShopAt("pl:nowhere").Stock!);
        Assert.False(sol.MarketAt("pl:nowhere").Any);
    }

    [Fact]
    public void AMissionTakenBeforeM15_LearnsThatItsEmployerIsAPlace()
    {
        // Так задание лежит в профилях, сохранённых до M15: заказчик и адрес — голые id систем.
        var old = new ActiveMission(new MissionOffer("m1", MissionRules.DeliverKind, "vega", null, null, 3, 500, "sol"));

        var now = MissionRules.Upgrade(old);

        Assert.Equal("st:sol", now.Offer.From);
        Assert.Equal("st:vega", now.Offer.Place);
        Assert.Equal("st:vega", now.Offer.Destination);
        Assert.Equal("st:sol", now.Offer.Payer);
    }

    [Fact]
    public void AMissionWithNowhereToDeliver_KeepsItsEmployerAsTheAddress()
    {
        var old = new ActiveMission(new MissionOffer("m2", MissionRules.KillKind, "vega", "pirate", null, 3, 200, "sol"));

        var now = MissionRules.Upgrade(old);

        Assert.Equal("st:sol", now.Offer.From);
        Assert.Null(now.Offer.Place);
        // Убивать летят в vega, а отчитываться — туда, где взяли.
        Assert.Equal("st:sol", now.Offer.Destination);
    }

    [Fact]
    public void AMissionAlreadyKeyedByPlace_IsLeftAlone()
    {
        var taken = new ActiveMission(
            new MissionOffer("m3", MissionRules.CourierKind, "vega", null, null, 1, 900, "pl:terra", Place: "st:vega"));

        Assert.Same(taken, MissionRules.Upgrade(taken));
    }
}
