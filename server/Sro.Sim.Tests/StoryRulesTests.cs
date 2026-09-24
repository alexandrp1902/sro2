using Sro.Sim;

namespace Sro.Sim.Tests;

/// <summary>
/// Кампании из shared/story.json (M20a). Главный тест здесь — настоящий файл: опечатка в месте, предмете
/// или типе NPC должна валить разбор в сборке, а не на доске у пилота, который дошёл до пятой миссии.
/// </summary>
public class StoryRulesTests
{
    private static Balance Shared()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        return balance!;
    }

    [Fact]
    public void SharedFile_Parses()
    {
        var story = Shared().Story;
        Assert.True(story.Any);
        var quiet = story.Campaign("quietWar");
        Assert.NotNull(quiet);
        Assert.Equal("Тихая война", quiet!.Name);
        // Кампания задумана длиннее, чем написана: журнал считает «из 14» уже сейчас.
        Assert.Equal(14, quiet.Length);
        Assert.True(quiet.MissionList.Count >= 6);
    }

    [Fact]
    public void SharedFile_SendsEveryMissionSomewhereThatExists()
    {
        var balance = Shared();
        foreach (var (_, campaign) in balance.Story.CampaignMap)
        {
            foreach (var mission in campaign.MissionList)
            {
                Assert.True(balance.Galaxy.HasPlace(mission.Place), mission.Id);
                Assert.True(balance.Galaxy.HasPlace(mission.Destination), mission.Id);
                // Сюжетная строка без заголовка и без цели — пустая строка на доске и в трекере.
                Assert.False(string.IsNullOrWhiteSpace(mission.Title), mission.Id);
                Assert.False(string.IsNullOrWhiteSpace(mission.Objective), mission.Id);
            }
        }
    }

    [Fact]
    public void TheChainIsTheOrderInTheFile()
    {
        var quiet = Shared().Story.Campaign("quietWar")!;
        var first = quiet.Next([]);
        Assert.NotNull(first);
        Assert.Equal(quiet.MissionList[0].Id, first!.Id);

        var second = quiet.Next([first.Id]);
        Assert.Equal(quiet.MissionList[1].Id, second!.Id);

        // Всё пройдено — следующей нет, и доска сюжет больше не показывает.
        Assert.Null(quiet.Next([.. quiet.MissionList.Select(m => m.Id)]));
    }

    [Fact]
    public void OfferIdsRoundTrip()
    {
        var id = StoryRules.OfferId("quietWar", "wreck");
        Assert.True(StoryRules.SplitOffer(id, out var campaign, out var mission));
        Assert.Equal("quietWar", campaign);
        Assert.Equal("wreck", mission);

        // Обычная работа с доски сюжетом не притворяется.
        Assert.False(StoryRules.SplitOffer("7-2", out _, out _));
        Assert.False(StoryRules.SplitOffer(null, out _, out _));
        Assert.False(StoryRules.SplitOffer("story:quietWar", out _, out _));
    }

    private const string Npcs = """
    { "types": { "pirate": { "name": "Пират", "hull": "light", "weapons": ["pulse"] } } }
    """;

    private static readonly Dictionary<string, HullParams> Hulls = new() { ["light"] = TestHulls.Light };
    private static readonly Dictionary<string, WeaponParams> Weapons = new() { ["pulse"] = TestWeapons.Pulse };

    private static (IReadOnlyDictionary<string, NpcType> Npcs, IReadOnlyDictionary<string, LootItem> Items) Catalogs()
    {
        Assert.True(NpcRules.TryParse(Npcs, Hulls, Weapons, out var npcs, out var error), error);
        var items = new Dictionary<string, LootItem> { ["metal"] = new("Металл"), ["secret"] = new("Улика", Story: true) };
        return (npcs.TypeMap, items);
    }

    private static string? Error(string json)
    {
        var (npcs, items) = Catalogs();
        StoryRules.TryParse(json, npcs, items, out _, out var error);
        return error;
    }

    [Fact]
    public void AnUnknownKindIsRefused()
    {
        var error = Error("""
        { "campaigns": { "c": { "name": "К", "missions": [
          { "id": "a", "title": "Т", "place": "st:home", "giver": "Г", "role": "р", "objective": "о", "kind": "ram" }
        ] } } }
        """);
        Assert.Contains("unknown kind", error);
    }

    [Fact]
    public void DuplicateIdsAreRefused()
    {
        var error = Error("""
        { "campaigns": { "c": { "name": "К", "missions": [
          { "id": "a", "title": "Т", "place": "st:home", "giver": "Г", "role": "р", "objective": "о", "item": "metal" },
          { "id": "a", "title": "Т", "place": "st:home", "giver": "Г", "role": "р", "objective": "о", "item": "metal" }
        ] } } }
        """);
        Assert.Contains("duplicate id", error);
    }

    [Fact]
    public void CollectWithoutAKnownItemIsRefused()
    {
        var error = Error("""
        { "campaigns": { "c": { "name": "К", "missions": [
          { "id": "a", "title": "Т", "place": "st:home", "giver": "Г", "role": "р", "objective": "о", "item": "unobtanium" }
        ] } } }
        """);
        Assert.Contains("collect needs a known item", error);
    }

    [Fact]
    public void TwoOptionsCannotShareAFlag()
    {
        // Флаг — вся память о выборе: одинаковые означали бы, что выбирать было нечего.
        var error = Error("""
        { "campaigns": { "c": { "name": "К", "missions": [
          { "id": "a", "title": "Т", "place": "st:home", "giver": "Г", "role": "р", "objective": "о", "item": "metal",
            "finish": "choice",
            "choice": { "question": "?", "options": [
              { "label": "Да", "flag": "same" }, { "label": "Нет", "flag": "same" }
            ] } }
        ] } } }
        """);
        Assert.Contains("duplicate flag", error);
    }

    [Fact]
    public void FinishByChoiceNeedsAChoice()
    {
        var error = Error("""
        { "campaigns": { "c": { "name": "К", "missions": [
          { "id": "a", "title": "Т", "place": "st:home", "giver": "Г", "role": "р", "objective": "о",
            "item": "metal", "finish": "choice" }
        ] } } }
        """);
        Assert.Contains("needs a choice", error);
    }

    [Fact]
    public void AnUnknownSpawnTriggerIsRefused()
    {
        var error = Error("""
        { "campaigns": { "c": { "name": "К", "missions": [
          { "id": "a", "title": "Т", "place": "st:home", "giver": "Г", "role": "р", "objective": "о", "item": "metal",
            "spawns": [{ "trigger": "onTuesday", "npc": "pirate" }] }
        ] } } }
        """);
        Assert.Contains("unknown trigger", error);
    }

    [Fact]
    public void ACampaignCannotPromiseFewerMissionsThanItHas()
    {
        var error = Error("""
        { "campaigns": { "c": { "name": "К", "total": 1, "missions": [
          { "id": "a", "title": "Т", "place": "st:home", "giver": "Г", "role": "р", "objective": "о", "item": "metal" },
          { "id": "b", "title": "Т", "place": "st:home", "giver": "Г", "role": "р", "objective": "о", "item": "metal" }
        ] } } }
        """);
        Assert.Contains("is less than the 2 missions", error);
    }

    [Fact]
    public void NoFileMeansNoStory()
    {
        Assert.False(StoryRules.None.Any);
        Assert.Null(StoryRules.None.Campaign("quietWar"));
    }
}
