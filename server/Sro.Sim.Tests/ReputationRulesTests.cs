namespace Sro.Sim.Tests;

/// <summary>
/// Репутация (M13): ступени шкалы, распад к нулю, цены со скидкой, гейт магазина и доска заданий.
/// Числа здесь свои, а не из shared/, чтобы тюнинг баланса не ронял тесты; настоящий файл проверяется
/// отдельным случаем — он должен разбираться и сходиться с корпусами.
/// </summary>
public class ReputationRulesTests
{
    private static ReputationRules Rules() => new(
        Limit: 100,
        Levels:
        [
            new RepLevel("enemy", "Враг", -100, 1.10),
            new RepLevel("distrust", "Недоверие", -50, 1.05),
            new RepLevel("neutral", "Нейтрал", -10),
            new RepLevel("friend", "Друг", 30, 0.95),
            new RepLevel("hero", "Герой", 70, 0.90),
        ],
        DecayPerDay: 3,
        Events: new RepEvents(MissionPlace: 8, MissionSystem: 2),
        Gate: new RepGate("friend", [3], ["cruiser"]),
        Missions: new RepMissions(new Dictionary<string, int> { ["enemy"] = 0, ["distrust"] = 2 }, "friend", 1.5));

    [Theory]
    [InlineData(-100, "enemy")]
    [InlineData(-51, "enemy")]
    [InlineData(-50, "distrust")]
    [InlineData(-11, "distrust")]
    [InlineData(-10, "neutral")]
    [InlineData(0, "neutral")]
    [InlineData(29, "neutral")]
    [InlineData(30, "friend")]
    [InlineData(69, "friend")]
    [InlineData(70, "hero")]
    [InlineData(100, "hero")]
    public void Level_TakesTheLowerBoundInclusive(double value, string expected) =>
        Assert.Equal(expected, Rules().Level(value).Id);

    [Fact]
    public void Clamp_KeepsTheScaleWithinItsLimit()
    {
        var rules = Rules();
        Assert.Equal(100, rules.Clamp(180));
        Assert.Equal(-100, rules.Clamp(-180));
        Assert.Equal(42, rules.Clamp(42));
    }

    [Fact]
    public void Decay_PullsBothSidesTowardsZero()
    {
        var rules = Rules();
        Assert.Equal(51, rules.Decay(60, 3), 6);
        Assert.Equal(-51, rules.Decay(-60, 3), 6);
    }

    [Fact]
    public void Decay_StopsAtZeroInsteadOfCrossingIt()
    {
        var rules = Rules();
        Assert.Equal(0, rules.Decay(4, 10), 6);
        Assert.Equal(0, rules.Decay(-4, 10), 6);
    }

    [Fact]
    public void Decay_IgnoresTimeRunningBackwards()
    {
        // Часы на сервере перевели назад: репутация не должна от этого расти.
        var rules = Rules();
        Assert.Equal(60, rules.Decay(60, -5), 6);
        Assert.Equal(60, rules.Decay(60, 0), 6);
    }

    [Fact]
    public void Decay_LetsAnEnemyBackToDistrustInADayAndAHalf()
    {
        // Замысел: испортить репутацию быстро, исправить — заметно дольше, но не безнадёжно.
        var rules = Rules();
        Assert.Equal("enemy", rules.Level(rules.Decay(-54, 1)).Id);
        Assert.Equal("distrust", rules.Level(rules.Decay(-54, 1.5)).Id);
    }

    [Fact]
    public void Price_BendsByTheLevelAndRoundsLikeTheShop()
    {
        var rules = Rules();
        // Округление магазина: от сотни — до десятков, мелочь — до кредита.
        Assert.Equal(3000, rules.Price(3000, 0));
        Assert.Equal(2850, rules.Price(3000, 40));
        Assert.Equal(2700, rules.Price(3000, 80));
        Assert.Equal(3300, rules.Price(3000, -80));
        Assert.Equal(76, rules.Price(80, 40));
    }

    [Fact]
    public void Gate_HoldsTheTopTierAndTheTopHullsForFriends()
    {
        var rules = Rules();
        Assert.True(rules.Gated("plasma_mk3", hull: false));
        Assert.False(rules.Gated("plasma_mk2", hull: false));
        Assert.False(rules.Gated("plasma", hull: false));
        Assert.True(rules.Gated("cruiser", hull: true));
        Assert.False(rules.Gated("medium", hull: true));
    }

    [Fact]
    public void Allows_OpensTheGateAtFriendAndKeepsTherestAlwaysOpen()
    {
        var rules = Rules();
        Assert.False(rules.Allows(29, "plasma_mk3", hull: false));
        Assert.True(rules.Allows(30, "plasma_mk3", hull: false));
        Assert.False(rules.Allows(-80, "cruiser", hull: true));
        Assert.True(rules.Allows(-80, "medium", hull: true));
    }

    [Fact]
    public void Offers_ShrinkTheBoardForTheDistrusted()
    {
        var rules = Rules();
        Assert.Equal(0, rules.Offers(-80, fallback: 4));
        Assert.Equal(2, rules.Offers(-20, fallback: 4));
        Assert.Equal(4, rules.Offers(0, fallback: 4));
        Assert.Equal(4, rules.Offers(90, fallback: 4));
    }

    [Fact]
    public void Elite_AppearsOnlyForFriends()
    {
        var rules = Rules();
        Assert.False(rules.Elite(29));
        Assert.True(rules.Elite(30));
        Assert.Equal(1.5, rules.EliteReward);
    }

    [Fact]
    public void None_BehavesLikeBeforeM13()
    {
        var none = ReputationRules.None;
        Assert.False(none.Any);
        Assert.True(none.Allows(-100, "cruiser", hull: true));
        Assert.Equal(3000, none.Price(3000, -100));
        Assert.Equal(4, none.Offers(-100, fallback: 4));
        Assert.False(none.Elite(100));
    }

    [Fact]
    public void Validate_WantsTheLevelsInOrder()
    {
        var broken = Rules() with
        {
            Levels =
            [
                new RepLevel("enemy", "Враг", -100),
                new RepLevel("neutral", "Нейтрал", -10),
                new RepLevel("distrust", "Недоверие", -50),
            ],
        };
        Assert.Contains("from must be above", broken.Validate());
    }

    [Fact]
    public void Validate_RefusesAScaleThatDoesNotStartAtTheLimit()
    {
        var broken = Rules() with { Levels = [new RepLevel("neutral", "Нейтрал", -10)] };
        Assert.Contains("first level", broken.Validate());
    }

    [Fact]
    public void Validate_CatchesUnknownNames()
    {
        Assert.Contains("unknown hull", (Rules() with { Gate = new RepGate("friend", [3], ["dreadnought"]) })
            .Validate(TestHulls.Catalog));
        Assert.Contains("unknown level", (Rules() with { Gate = new RepGate("ally") }).Validate());
        Assert.Contains("unknown level", (Rules() with { Missions = new RepMissions(EliteFrom: "ally") }).Validate());
        Assert.Contains(
            "unknown level",
            (Rules() with { Missions = new RepMissions(new Dictionary<string, int> { ["ally"] = 1 }) }).Validate());
    }

    [Fact]
    public void TryParse_ReportsBrokenJson()
    {
        Assert.False(ReputationRules.TryParse("{", null, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void SharedReputation_ParsesAndNamesRealHulls()
    {
        // Главный страх слайса: reputation.json разбирается позиционно, и опечатка в гейте должна падать здесь,
        // а не на живом сервере при первой покупке.
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        var rules = balance!.Reputation;
        Assert.True(rules.Any);
        Assert.Equal("Друг", rules.Level(42).Name);
        foreach (var id in rules.Gate!.HullList) Assert.True(balance.Hulls.ContainsKey(id), $"gate names a missing hull {id}");
    }
}
