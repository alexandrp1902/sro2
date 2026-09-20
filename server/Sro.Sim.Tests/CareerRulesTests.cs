using Sro.Sim;

namespace Sro.Sim.Tests;

/// <summary>
/// Пути пилота (M15.5): разбор careers.json и его проверки. Опечатку в наборе надо ловить при разборе,
/// а не при первом входе нового пилота — иначе она достанется живому человеку.
/// </summary>
public class CareerRulesTests
{
    private static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams>
    {
        ["light"] = TestHulls.Light with { Class = EquipClass.S, WeaponSlots = [EquipClass.S, EquipClass.S], Cargo = 20 },
        ["hauler"] = TestHulls.Light with { Class = EquipClass.M, WeaponSlots = [EquipClass.S, EquipClass.S], Cargo = 75 },
    };

    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["pulse"] = TestWeapons.Pulse,
    };

    private static readonly IReadOnlyDictionary<string, ModuleParams> Modules = new Dictionary<string, ModuleParams>
    {
        ["engineS"] = new("Двигатель", Fitting.EngineSlot, Power: 5),
        ["shieldS"] = new("Щит", Fitting.ShieldSlot, Power: 10, Shield: 150),
        ["radarS"] = new("Радар", Fitting.RadarSlot, Power: 5, Radar: 2000),
        ["tankS"] = new("Бак", Fitting.TankSlot, Fuel: 100),
        ["generatorS"] = new("Генератор", Fitting.GeneratorSlot, Output: 300),
    };

    private static readonly LootRules Loot = new(
        Items: new Dictionary<string, LootItem> { ["food"] = new("Продовольствие", Volume: 1, Price: 30) });

    private static readonly GalaxyRules Galaxy = new(
        StartSystem: "sol",
        Systems: new Dictionary<string, SystemDef>
        {
            ["sol"] = new("Sol", Planets: [new PlanetDef("Терра", "earth", 60, new OrbitDef(1000), Id: "terra", Settlement: new SettlementDef("Новый Порт"))]),
            ["vega"] = new("Vega"),
        });

    private static CareerRules Rules(params (string Id, CareerDef Career)[] careers) =>
        new("ranger", careers.ToDictionary(c => c.Id, c => c.Career));

    private static string? Check(CareerRules rules) => rules.Validate(Hulls, Weapons, Modules, Loot, Galaxy);

    private static CareerDef Ranger(params string[] _) => new("Рейнджер", "Боевой", Hull: "light", Weapons: ["pulse"]);

    [Fact]
    public void AGoodFileIsAccepted()
    {
        Assert.Null(Check(Rules(
            ("ranger", Ranger()),
            ("trader", new CareerDef(
                "Торговец", "Грузовой", Hull: "hauler", Weapons: ["pulse"],
                Place: PlaceKey.Planet("terra"), System: "sol",
                Cargo: new Dictionary<string, int> { ["food"] = 40 })))));
    }

    [Fact]
    public void AFileWithoutCareersIsRejected()
    {
        Assert.Equal("no careers", Check(new CareerRules()));
    }

    [Fact]
    public void TheDefaultCareerMustExistAndBeOpen()
    {
        Assert.Equal("default: unknown career ranger", Check(Rules(("trader", Ranger()))));
        Assert.Equal(
            "default: career ranger is locked",
            Check(Rules(("ranger", Ranger() with { Enabled = false }))));
    }

    [Fact]
    public void UnknownHullWeaponOrModuleIsRejected()
    {
        Assert.Equal("ranger: unknown hull titan", Check(Rules(("ranger", Ranger() with { Hull = "titan" }))));
        Assert.Equal("ranger: unknown weapon railgun", Check(Rules(("ranger", Ranger() with { Weapons = ["railgun"] }))));
        Assert.Equal(
            "ranger: unknown module engineL",
            Check(Rules(("ranger", Ranger() with { Modules = new Dictionary<string, string> { [Fitting.EngineSlot] = "engineL" } }))));
    }

    [Fact]
    public void MoreWeaponsThanSlotsIsRejected()
    {
        Assert.Equal(
            "ranger: 3 weapons for 2 slots of 'light'",
            Check(Rules(("ranger", Ranger() with { Weapons = ["pulse", "pulse", "pulse"] }))));
    }

    [Fact]
    public void StartingCargoMustFitTheHold()
    {
        // «Пчела» возит 20, а не 40: иначе пилот появился бы с перегрузом, который никогда не набрать честно.
        Assert.Equal(
            "ranger: starting cargo does not fit the hold of 'light'",
            Check(Rules(("ranger", Ranger() with { Cargo = new Dictionary<string, int> { ["food"] = 40 } }))));
        Assert.Equal(
            "ranger: unknown cargo gold",
            Check(Rules(("ranger", Ranger() with { Cargo = new Dictionary<string, int> { ["gold"] = 1 } }))));
    }

    [Fact]
    public void ThePlaceMustExistAndSitInItsOwnSystem()
    {
        Assert.Equal(
            "ranger: unknown place pl:mars",
            Check(Rules(("ranger", Ranger() with { Place = PlaceKey.Planet("mars") }))));
        // Иначе пилот появился бы в одной системе, а домом считал бы место из другой.
        Assert.Equal(
            "ranger: place pl:terra is not in system vega",
            Check(Rules(("ranger", Ranger() with { Place = PlaceKey.Planet("terra"), System = "vega" }))));
    }

    [Fact]
    public void ReputationKeysMustPointAtSomethingReal()
    {
        Assert.Equal(
            "ranger: unknown reputation key sys:nowhere",
            Check(Rules(("ranger", Ranger() with { Rep = new Dictionary<string, double> { ["sys:nowhere"] = 5 } }))));
        Assert.Null(Check(Rules(("ranger", Ranger() with { Rep = new Dictionary<string, double> { ["sys:sol"] = 5 } }))));
    }

    [Fact]
    public void ALockedCareerNeedsNoKit_OnlyACard()
    {
        // У пирата пока нет ни корабля, ни места: карточка стоит в ряду, чтобы было видно — путь будет.
        Assert.Null(Check(Rules(("ranger", Ranger()), ("pirate", new CareerDef("Пират", "Скоро", Enabled: false)))));
    }

    [Fact]
    public void AnUnknownOrLockedCareerReadsAsTheDefaultOne()
    {
        var rules = Rules(("ranger", Ranger()), ("pirate", new CareerDef("Пират", "Скоро", Enabled: false)));
        Assert.Equal("Рейнджер", rules.Of("pirate")?.Name);
        Assert.Equal("Рейнджер", rules.Of("nobody")?.Name);
        Assert.Equal("Рейнджер", rules.Of(null)?.Name);
        // Путей нет вовсе — набора тоже нет, и старт остаётся общим, как до M15.5.
        Assert.Null(CareerRules.None.Of("ranger"));
    }

    [Fact]
    public void TheKitTakesTheStarterModulesWhenTheFileNamesNone()
    {
        var fit = CareerRules.FitOf(Ranger());
        Assert.Equal(Fitting.StarterEngine, fit.Engine);
        Assert.Equal(Fitting.StarterGenerator, fit.Generator);
        Assert.Equal(["pulse"], fit.Weapons);
    }
}
