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
                            break;
                        default:
                            Assert.Fail($"unknown kind {offer.Kind}");
                            break;
                    }
                }
            }
        }
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
    public void Validate_RejectsBrokenFiles(string json, string problem)
    {
        var balance = Shared();
        Assert.False(MissionRules.TryParse(json, balance.Npc.TypeMap, balance.Loot.ItemMap, out _, out var error));
        Assert.StartsWith(problem, error);
    }
}
