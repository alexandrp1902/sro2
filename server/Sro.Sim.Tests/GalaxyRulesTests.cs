namespace Sro.Sim.Tests;

/// <summary>Галактика из galaxy.json (GDD §4–6, §33–34, §55): системы, врата, маршруты, цена прыжка.</summary>
public class GalaxyRulesTests
{
    private static string? NoLayoutProblems(string id, SystemDef system) => null;

    private static GalaxyRules Two(
        SystemDef? a = null,
        SystemDef? b = null,
        IReadOnlyList<LinkDef>? links = null,
        string start = "a") => new(
            StartSystem: start,
            Systems: new Dictionary<string, SystemDef>
            {
                ["a"] = a ?? new SystemDef("A", Gates: [new GateDef("b", 3000, 0)]),
                ["b"] = b ?? new SystemDef("B", Station: false, Pvp: GalaxyRules.PvpFree, Gates: [new GateDef("a", -3000, 0)]),
            },
            Links: links ?? [new LinkDef("a", "b", 21)]);

    [Fact]
    public void SharedGalaxyJson_IsValid()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        var galaxy = balance!.Galaxy;
        Assert.Equal(11, galaxy.SystemMap.Count); // M11: одиннадцать систем в трёх регионах
        Assert.Equal(8, galaxy.SystemMap.Values.Count(s => s.Station)); // и восемь станций
        Assert.Equal(["core", "frontier", "rim"], galaxy.RegionMap.Keys.Order());
        // В каждом регионе есть где пристыковаться.
        foreach (var region in galaxy.RegionMap.Keys)
            Assert.Contains(galaxy.SystemMap.Values, s => s.Region == region && s.Station);
        Assert.True(galaxy.System(galaxy.StartSystem)!.Station);
        Assert.Equal(GalaxyRules.PvpOff, galaxy.System(galaxy.StartSystem)!.Pvp); // новичок начинает без PvP (§34)
        Assert.Contains(galaxy.SystemMap.Values, s => s.Pvp == GalaxyRules.PvpFree);
    }

    [Fact]
    public void SharedGalaxy_EverySystemIsReachableFromTheStart()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        var galaxy = balance!.Galaxy;
        var reached = new HashSet<string> { galaxy.StartSystem };
        var queue = new Queue<string>(reached);
        while (queue.TryDequeue(out var id))
        {
            foreach (var gate in galaxy.System(id)!.GateList)
            {
                if (reached.Add(gate.To)) queue.Enqueue(gate.To);
            }
        }
        Assert.Equal(galaxy.SystemMap.Keys.Order(), reached.Order());
    }

    [Fact]
    public void Link_FindsTheRouteInEitherDirection_AndNoneToItself()
    {
        var galaxy = Two();
        Assert.Equal(21, galaxy.Link("a", "b")!.Distance);
        Assert.Equal(21, galaxy.Link("b", "a")!.Distance);
        Assert.Null(galaxy.Link("a", "a"));
    }

    [Fact]
    public void Arrival_IsInsideFromTheAnswerGate()
    {
        var galaxy = Two();
        // Из a в b — к вратам b → a (-3000, 0), на arrivalOffset ближе к центру.
        Assert.Equal((-2750.0, 0.0), galaxy.Arrival("a", "b"));
        Assert.Equal((2750.0, 0.0), galaxy.Arrival("b", "a"));
    }

    [Fact]
    public void Validate_AcceptsAWellFormedGalaxy()
    {
        Assert.Null(Two().Validate(NoLayoutProblems));
    }

    [Fact]
    public void Validate_RejectsBrokenGalaxies()
    {
        Assert.Contains("unknown startSystem", Two(start: "x").Validate(NoLayoutProblems));
        Assert.Contains("must have a station", Two(start: "b").Validate(NoLayoutProblems));
        Assert.Contains("unknown system 'x'", Two(a: new SystemDef("A", Gates: [new GateDef("x", 0, 3000)])).Validate(NoLayoutProblems));
        Assert.Contains("another system", Two(a: new SystemDef("A", Gates: [new GateDef("a", 0, 3000)])).Validate(NoLayoutProblems));
        Assert.Contains("pvp", Two(a: new SystemDef("A", Pvp: "sometimes", Gates: [new GateDef("b", 3000, 0)])).Validate(NoLayoutProblems));
        Assert.Contains("danger", Two(a: new SystemDef("A", Danger: 7, Gates: [new GateDef("b", 3000, 0)])).Validate(NoLayoutProblems));
        Assert.Contains("within", Two(a: new SystemDef("A", Gates: [new GateDef("b", 5000, 0)])).Validate(NoLayoutProblems));
        Assert.Contains("no gate from b to a", Two(b: new SystemDef("B")).Validate(NoLayoutProblems));
        Assert.Contains("has no link", Two(links: []).Validate(NoLayoutProblems));
        Assert.Contains("duplicate link", Two(links: [new LinkDef("a", "b", 20), new LinkDef("b", "a", 30)]).Validate(NoLayoutProblems));
        Assert.Contains("distance", Two(links: [new LinkDef("a", "b", 0)]).Validate(NoLayoutProblems));
    }

    [Fact]
    public void Validate_ReportsLayoutProblemsWithTheSystemName()
    {
        var problem = Two().Validate((id, _) => id == "b" ? "spawns[0]: too close" : null);
        Assert.Equal("systems.b: spawns[0]: too close", problem);
    }

    [Fact]
    public void Balance_RejectsALairThatTheOrbitingStationShelterWouldReach()
    {
        // Станция Sol ходит по кругу радиуса 1000: логово в 2000 от звезды она проходит в тысяче — это внутри укрытия.
        var sources = TestHulls.SharedSources();
        var lair = "\"spawns\": [ { \"type\": \"pirate\", \"level\": 1, \"x\": 0, \"y\": -2000 } ], \"pirates\": {";
        var galaxy = ReplaceFirst(sources.Galaxy!, "\"pirates\": {", lair);
        Assert.False(Balance.TryParse(sources with { Galaxy = galaxy }, out _, out var error));
        Assert.Contains("galaxy.json: systems.sol: spawns[0]: too close to the station orbit", error);
    }

    [Fact]
    public void Balance_RejectsARaidOfAnUnknownType()
    {
        var sources = TestHulls.SharedSources();
        var galaxy = ReplaceFirst(sources.Galaxy!, "\"type\": \"pirate\"", "\"type\": \"ghost\"");
        Assert.False(Balance.TryParse(sources with { Galaxy = galaxy }, out _, out var error));
        Assert.Contains("galaxy.json: systems.sol: pirates: groups[0]: unknown type 'ghost'", error);
    }

    [Fact]
    public void Balance_RejectsAContainerInTheHeatOfTheSun()
    {
        var sources = TestHulls.SharedSources();
        var galaxy = ReplaceFirst(sources.Galaxy!, "\"x\": 600,\n          \"y\": 700", "\"x\": 300,\n          \"y\": 300");
        Assert.False(Balance.TryParse(sources with { Galaxy = galaxy }, out _, out var error));
        Assert.Contains("galaxy.json: systems.tau: containers[0]: too close to the sun", error);
    }

    private static string ReplaceFirst(string text, string from, string to)
    {
        var at = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{from}' not found");
        return text[..at] + to + text[(at + from.Length)..];
    }

    [Fact]
    public void ForSystem_SwapsInTheSystemLayout()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        foreach (var (id, system) in balance!.Galaxy.SystemMap)
        {
            var view = balance.ForSystem(id);
            Assert.Equal(id, view.System);
            Assert.Equal(system.SpawnList, view.Npc.SpawnList);
            Assert.Equal(system.ContainerList, view.Loot.ContainerList);
            Assert.Equal(system.DroneList, view.Rules.DroneList);
            // Без станции нет и укрытия: пираты гонятся до самого центра.
            Assert.Equal(system.Station ? balance.Npc.StationSafeRadius : 0, view.Npc.StationSafeRadius);
            Assert.Equal(balance.Npc.StationSafeRadius, view.MeteorCore);
        }
    }

    [Fact]
    public void ForSystem_ScalesMeteors()
    {
        var meteors = new MeteorRules(SpawnIntervalSeconds: 20, MaxAlive: 4);
        var balance = new Balance(TestHulls.Catalog, new Dictionary<string, WeaponParams>(), new CombatRules(), MeteorSet: meteors,
            GalaxySet: Two(
                a: new SystemDef("A", Meteors: 0, Gates: [new GateDef("b", 3000, 0)]),
                b: new SystemDef("B", Station: false, Meteors: 2.5, Gates: [new GateDef("a", -3000, 0)])));

        Assert.False(balance.ForSystem("a").Meteors.Enabled);
        var dense = balance.ForSystem("b").Meteors;
        Assert.Equal(8, dense.SpawnIntervalSeconds, 9);
        Assert.Equal(10, dense.MaxAlive);
    }

    [Fact]
    public void WithoutGalaxyJson_OneSystemKeepsTheOldLayoutAndPvp()
    {
        var spawns = new List<NpcSpawn> { new("pirate", 1, 0, -2500) };
        var balance = new Balance(TestHulls.Catalog, new Dictionary<string, WeaponParams>(), new CombatRules(), new NpcRules(Spawns: spawns));
        var view = balance.ForSystem(GalaxyRules.DefaultSystem);
        Assert.Same(spawns, view.Npc.Spawns);
        Assert.True(view.HasStation);
        Assert.Equal(GalaxyRules.PvpFree, view.SystemDef.Pvp);
    }
}
