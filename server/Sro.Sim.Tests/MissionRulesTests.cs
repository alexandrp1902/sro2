using Sro.Sim;

namespace Sro.Sim.Tests;

/// <summary>Правила заданий (GDD §36, §54): разбор missions.json и доска станции.</summary>
public class MissionRulesTests
{
    private static Balance Shared()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        return balance!;
    }

    [Fact]
    public void SharedMissionsJson_HasTheWholeTutorialInOrder()
    {
        var missions = Shared().Missions;
        Assert.Equal(MissionRules.TutorialIds, missions.Steps.Select(s => s.Id));
        Assert.True(missions.Offers > 0);
    }

    [Fact]
    public void SharedMissionsJson_OffersEveryKindSomewhere()
    {
        var balance = Shared();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var station in balance.Galaxy.SystemMap.Where(kv => kv.Value.Station).Select(kv => kv.Key))
        {
            for (var seed = 0; seed < 40; seed++)
            {
                // Репутация «нейтрал» — с чего начинает любой пилот: патруль должен быть доступен уже ей.
                foreach (var offer in balance.Missions.Board(balance, station, seed, repHere: 0)) kinds.Add(offer.Kind);
            }
        }
        // Вид, которого нет ни на одной доске, — это вырезанный блок в missions.json, а не тонкая настройка.
        Assert.Equal(
            [
                MissionRules.CollectKind, MissionRules.CourierKind, MissionRules.DeliverKind, MissionRules.EscortKind,
                MissionRules.HuntKind, MissionRules.KillKind, MissionRules.PatrolKind,
            ],
            kinds.Order(StringComparer.Ordinal));
        Assert.NotEmpty(balance.Missions.AmbushList);
    }

    [Fact]
    public void Board_IsDeterministicBySeed_AndDiffersByStation()
    {
        var balance = Shared();
        var missions = balance.Missions;
        Assert.Equal(missions.Board(balance, "sol", 42), missions.Board(balance, "sol", 42));
        Assert.NotEqual(missions.Board(balance, "sol", 42), missions.Board(balance, "sol", 43));
        Assert.NotEqual(missions.Board(balance, "sol", 42), missions.Board(balance, "vega", 42));
        Assert.Empty(missions.Board(balance, "tau", 42)); // станции нет
    }

    [Fact]
    public void Board_SendsOnlyWhereTheMissionCanBeDone()
    {
        var balance = Shared();
        var galaxy = balance.Galaxy;
        foreach (var station in new[] { "sol", "vega", "nova" })
        {
            for (var seed = 0; seed < 50; seed++)
            {
                foreach (var offer in balance.Missions.Board(balance, station, seed))
                {
                    Assert.Equal(station, offer.From);
                    Assert.True(offer.Reward > 0);
                    if (offer.Kind != MissionRules.CourierKind) Assert.Equal(offer.From, offer.Payer);
                    switch (offer.Kind)
                    {
                        case MissionRules.KillKind:
                            Assert.Contains(offer.System!, MissionRules.Near(galaxy, station));
                            Assert.Contains(offer.Npc ?? "pirate", MissionRules.PiratesIn(balance, offer.System!));
                            break;
                        case MissionRules.DeliverKind:
                            Assert.NotEqual(station, offer.System);
                            Assert.True(galaxy.System(offer.System)!.Station);
                            break;
                        case MissionRules.CollectKind:
                            Assert.True(balance.Loot.ItemMap.ContainsKey(offer.Item!));
                            // Купить это здесь же и тут же сдать нельзя: станция такого не продаёт (M12).
                            Assert.False(balance.ForSystem(station).Market.Sells(offer.Item!), offer.Item);
                            break;
                        case MissionRules.EscortKind:
                            // Конвой идёт к вратам своей системы, и вести его есть кому.
                            Assert.Contains(offer.System!, galaxy.System(station)!.GateList.Select(g => g.To));
                            Assert.NotNull(galaxy.System(station)!.Traders);
                            Assert.True(offer.Count >= 1);
                            Assert.True(offer.Radius > 0);
                            break;
                        case MissionRules.PatrolKind:
                            Assert.Equal(station, offer.System);
                            Assert.Contains(offer.Npc!, MissionRules.RangersIn(balance, station));
                            Assert.True(offer.Count >= 2);
                            Assert.True(offer.Radius > 0);
                            break;
                        case MissionRules.CourierKind:
                            Assert.NotEqual(station, offer.System);
                            Assert.True(galaxy.System(offer.System)!.Station);
                            Assert.True(MissionRules.Hops(galaxy, station, offer.System!) <= MissionRules.MaxHopsLimit);
                            Assert.Equal(1, offer.Count);
                            Assert.True(offer.Seconds > 0);
                            // Письму платит получатель, а не заказчик (M14).
                            Assert.Equal(offer.System, offer.Payer);
                            break;
                        case MissionRules.HuntKind:
                            Assert.Contains(offer.System!, MissionRules.Near(galaxy, station));
                            Assert.True(galaxy.System(offer.System)!.Meteors > 0);
                            Assert.True(offer.Size is null || balance.Meteors.SizeMap.ContainsKey(offer.Size));
                            break;
                        default:
                            Assert.Fail($"unknown kind {offer.Kind}");
                            break;
                    }
                }
            }
        }
    }

    /// <summary>Шаблоны M14 поверх настоящей галактики: доска собирается только из них.</summary>
    private static MissionRules OnlyNew(MissionRules rules) => rules with
    {
        Tutorial = null,
        Kill = [],
        Collect = [],
        Deliver = [],
        Escort = [new EscortTemplate(1, 2, 300, 200)],
        Patrol = [new PatrolTemplate(3, 4, 300, 160, Rep: "neutral")],
        Courier = [new CourierTemplate(250, 420, 100, MaxHops: 2)],
        Hunt = [new HuntTemplate(null, 6, 10, 45)],
        Offers = 6,
    };

    [Fact]
    public void Board_OffersPatrolOnlyToThoseTheSystemTrusts()
    {
        var balance = Shared();
        var rules = OnlyNew(balance.Missions);
        bool HasPatrol(double rep) => Enumerable.Range(0, 30)
            .SelectMany(seed => rules.Board(balance, "sol", seed, repHere: rep))
            .Any(o => o.Kind == MissionRules.PatrolKind);
        Assert.True(HasPatrol(0));    // нейтрал — берут
        Assert.False(HasPatrol(-60)); // враг системы — звено с ним не полетит
    }

    [Fact]
    public void Board_KeepsEveryNewKindWhereItCanBeDone()
    {
        var balance = Shared();
        var rules = OnlyNew(balance.Missions);
        var galaxy = balance.Galaxy;
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 0; seed < 60; seed++)
        {
            foreach (var offer in rules.Board(balance, "sol", seed, repHere: 0))
            {
                kinds.Add(offer.Kind);
                // Письмо дальше maxHops не носят — здесь это два прыжка.
                if (offer.Kind == MissionRules.CourierKind)
                    Assert.True(MissionRules.Hops(galaxy, "sol", offer.System!) <= 2);
            }
        }
        Assert.Equal(
            [MissionRules.CourierKind, MissionRules.EscortKind, MissionRules.HuntKind, MissionRules.PatrolKind],
            kinds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Board_DropsNewKindsTheSystemCannotPlayOut()
    {
        var balance = Shared();
        var galaxy = balance.Galaxy;
        // Вести конвой некому и не с кем патрулировать; камни убраны во всей округе — остаётся одно письмо.
        var near = MissionRules.Near(galaxy, "sol").ToHashSet(StringComparer.Ordinal);
        var stripped = balance with
        {
            GalaxySet = galaxy with
            {
                Systems = galaxy.SystemMap.ToDictionary(
                    p => p.Key,
                    p => p.Key == "sol" ? p.Value with { Traders = null, Spawns = [], Meteors = 0 }
                        : near.Contains(p.Key) ? p.Value with { Meteors = 0 }
                        : p.Value),
            },
        };
        var rules = OnlyNew(stripped.Missions);
        var kinds = Enumerable.Range(0, 30)
            .SelectMany(seed => rules.Board(stripped, "sol", seed, repHere: 0))
            .Select(o => o.Kind)
            .Distinct()
            .ToList();
        Assert.Equal([MissionRules.CourierKind], kinds);
    }

    [Fact]
    public void Hops_CountsTheShortestRoute()
    {
        var galaxy = Shared().Galaxy;
        Assert.Equal(0, MissionRules.Hops(galaxy, "sol", "sol"));
        Assert.Equal(1, MissionRules.Hops(galaxy, "sol", "vega"));
        Assert.Equal(2, MissionRules.Hops(galaxy, "sol", "nova"));
        Assert.Equal(3, MissionRules.Hops(galaxy, "sol", "sigma"));
    }

    [Theory]
    [InlineData("""{ "tutorial": [{ "id": "fly", "title": "?" }] }""", "tutorial[0]: unknown id")]
    [InlineData("""{ "tutorial": [{ "id": "jump", "title": "a" }, { "id": "jump", "title": "b" }] }""", "tutorial[1]: duplicate id")]
    [InlineData("""{ "kill": [{ "npc": "dragon", "min": 1, "max": 2, "reward": 10 }] }""", "kill[0]: unknown npc")]
    [InlineData("""{ "kill": [{ "npc": null, "min": 3, "max": 2, "reward": 10 }] }""", "kill[0]: min and max")]
    [InlineData("""{ "collect": [{ "item": "gold", "min": 1, "max": 2 }] }""", "collect[0]: unknown item")]
    [InlineData("""{ "deliver": [{ "min": 1, "max": 2, "perUnit": -1, "perJump": 0 }] }""", "deliver[0]: perUnit")]
    [InlineData("""{ "offers": 99 }""", "offers must be")]
    [InlineData("""{ "escort": [{ "minWaves": 3, "maxWaves": 1, "reward": 10, "perWave": 0 }] }""", "escort[0]: min and max")]
    [InlineData("""{ "escort": [{ "minWaves": 1, "maxWaves": 2, "reward": 10, "perWave": 0, "radius": 0 }] }""", "escort[0]: radius")]
    [InlineData("""{ "patrol": [{ "min": 2, "max": 3, "reward": 10, "perPoint": 0, "npc": "pirate" }] }""", "patrol[0]: 'pirate' is not a ranger")]
    [InlineData("""{ "patrol": [{ "min": 2, "max": 3, "reward": 10, "perPoint": 0, "npc": "sheriff" }] }""", "patrol[0]: unknown npc")]
    [InlineData("""{ "courier": [{ "reward": 10, "perJump": 1, "secondsPerJump": 1, "maxHops": 99 }] }""", "courier[0]: maxHops")]
    [InlineData("""{ "hunt": [{ "size": "huge", "min": 1, "max": 2, "reward": 10 }] }""", "hunt[0]: unknown meteor size")]
    [InlineData("""{ "ambush": [[{ "type": "ranger", "count": 1 }]] }""", "ambush[0][0]: 'ranger' is not a pirate")]
    [InlineData("""{ "ambush": [[]] }""", "ambush[0]: is empty")]
    public void Validate_RejectsBrokenFiles(string json, string problem)
    {
        var balance = Shared();
        Assert.False(MissionRules.TryParse(
            json, balance.Npc.TypeMap, balance.Loot.ItemMap, balance.Meteors.SizeMap, out _, out var error));
        Assert.StartsWith(problem, error);
    }
}
