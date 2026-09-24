using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Точка в системе: куда класть скриптованный груз и где ждут вызванные сюжетом корабли.</summary>
public sealed record StoryPoint(double X = 0, double Y = 0);

/// <summary>
/// Реплики миссии (M20a). Короткие: космос — рабочее место, а не сцена.
/// </summary>
/// <param name="Offer">На доске, под названием, — зачем это нужно заказчику.</param>
/// <param name="Accept">Карточкой, когда работу взяли.</param>
/// <param name="Done">Карточкой, когда сдали.</param>
public sealed record StoryLines(
    IReadOnlyList<string>? Offer = null,
    IReadOnlyList<string>? Accept = null,
    IReadOnlyList<string>? Done = null);

/// <summary>
/// Ответ в диалоге: что написано на кнопке и какой флаг это оставляет в профиле.
/// </summary>
/// <param name="Flag">Уникален в пределах кампании: по нему M20b и узнает, как пилот тогда поступил.</param>
/// <param name="Lines">Что скажут в ответ; пусто — карточка просто закроется.</param>
/// <param name="Take">Забрать сюжетный предмет миссии из трюма: «отдать» — это отдать.</param>
public sealed record StoryOption(
    string Label,
    string Flag,
    IReadOnlyList<string>? Lines = null,
    bool Take = false);

/// <summary>
/// Выбор посреди миссии (M20a): карточка с вопросом и двумя кнопками. Ответ пишет флаг и, если
/// <see cref="StoryMission.Finish"/> — «choice», закрывает миссию.
/// </summary>
/// <param name="Trigger">Когда спрашивают; сейчас умеем только <see cref="StoryRules.OnPickup"/>.</param>
/// <param name="Who">Кто спрашивает; null — тот же, кто выдал работу.</param>
public sealed record StoryChoice(
    string Question,
    IReadOnlyList<StoryOption>? Options = null,
    string Trigger = StoryRules.OnPickup,
    string? Who = null,
    string? Role = null)
{
    [JsonIgnore] public IReadOnlyList<StoryOption> OptionList => Options ?? [];
}

/// <summary>
/// Скриптованное появление (M20a): по событию миссии в названной точке встают корабли.
/// Это то же, чем живут засады сопровождения, только состав и место берутся из сюжета, а не из таблицы волн.
/// </summary>
/// <param name="Trigger"><see cref="StoryRules.OnAccept"/>, <see cref="StoryRules.OnUndock"/> или <see cref="StoryRules.OnPickup"/>.</param>
/// <param name="Name">Имя над кораблём; null — обычное «Тип Ур.N». Одно на всю группу.</param>
/// <param name="At">Где встают; null — у врат <paramref name="Gate"/>, а без них — рядом с пилотом.</param>
/// <param name="Gate">Врата в эту систему: засада ждёт там, где пилот и так пройдёт.</param>
public sealed record StorySpawn(
    string Trigger,
    string Npc,
    int Level = 1,
    int Count = 1,
    string? Name = null,
    StoryPoint? At = null,
    string? Gate = null);

/// <summary>
/// Миссия кампании (M20a). Механика — из уже существующих видов заданий: сюжет добавляет не новые
/// глаголы, а имена, реплики, скрипты и память о выполненном. Поэтому тексты приходят готовыми строками
/// и живут здесь, рядом с репликами, а не собираются клиентом из вида и числа.
/// </summary>
/// <param name="Place">Где выдаётся: ключ места, «st:nova».</param>
/// <param name="Giver">Имя выдающего — он же говорит в карточке.</param>
/// <param name="Role">Его должность: строка под именем.</param>
/// <param name="Brief">Что написано на доске под названием.</param>
/// <param name="Objective">Строка трекера цели: что делать прямо сейчас.</param>
/// <param name="Hint">Подсказка под ней; пусто — трекер обойдётся одной строкой.</param>
/// <param name="Rep">Минимальная ступень отношения системы места; null — берут всех.</param>
/// <param name="Kind">Вид задания из <see cref="StoryRules.Kinds"/> — всё те же collect, deliver, escort, defend.</param>
/// <param name="System">Система, где всё происходит; null — система места выдачи.</param>
/// <param name="Dest">Куда везти или где сдавать; null — там же, где взяли.</param>
/// <param name="Item">Collect: что собрать (предмет из loot.json).</param>
/// <param name="Count">Сколько собрать, сколько единиц везти, сколько волн отбить или засад пережить.</param>
/// <param name="Radius">Escort: в каком радиусе держаться; defend: как далеко можно отойти.</param>
/// <param name="Strikes">Defend: столько налётчиков могут дойти до места, прежде чем работа провалена.</param>
/// <param name="RepReward">Очки отношения месту сверх обычной награды.</param>
/// <param name="RepPlace">Кому эти очки; null — месту, выдавшему работу.</param>
/// <param name="Point">Куда класть скриптованный груз collect; null — груз не кладётся.</param>
/// <param name="Give">Сюжетные предметы, которые выдают при взятии: они и есть груз миссии.</param>
/// <param name="Finish">
/// «dock» — сдаётся в <paramref name="Dest"/>, как обычная работа; «choice» — закрывается ответом
/// в диалоге, и дока для этого не нужно.
/// </param>
/// <param name="DoneBy">Кто говорит на сдаче; null — тот же, кто выдал.</param>
public sealed record StoryMission(
    string Id,
    string Title,
    string Place,
    string Giver,
    string Role,
    string Brief = "",
    string Objective = "",
    string Hint = "",
    string? Rep = null,
    string Kind = MissionRules.CollectKind,
    string? System = null,
    string? Dest = null,
    string? Item = null,
    int Count = 1,
    int Reward = 0,
    double Radius = 0,
    int Strikes = 3,
    double RepReward = 0,
    string? RepPlace = null,
    StoryPoint? Point = null,
    IReadOnlyList<StorySpawn>? Spawns = null,
    IReadOnlyList<IReadOnlyList<InvasionGroup>>? Waves = null,
    StoryLines? Lines = null,
    StoryChoice? Choice = null,
    IReadOnlyList<string>? Give = null,
    string Finish = StoryRules.FinishDock,
    string? DoneBy = null,
    string? DoneRole = null)
{
    [JsonIgnore] public IReadOnlyList<StorySpawn> SpawnList => Spawns ?? [];
    [JsonIgnore] public IReadOnlyList<IReadOnlyList<InvasionGroup>> WaveList => Waves ?? [];
    [JsonIgnore] public IReadOnlyList<string> GiveList => Give ?? [];

    /// <summary>Волна номер index; волн в задании больше, чем списков, — повторяется последний.</summary>
    public IReadOnlyList<InvasionGroup> Wave(int index) =>
        WaveList.Count == 0 ? [] : WaveList[Math.Clamp(index, 0, WaveList.Count - 1)];

    /// <summary>Где сдавать: место назначения, а если его нет — там же, где взяли.</summary>
    [JsonIgnore] public string Destination => Dest ?? Place;
}

/// <summary>Кампания: список миссий по порядку и сколько их будет всего, когда она будет дописана.</summary>
/// <param name="Total">
/// Сколько миссий в задуманной кампании. Журнал пишет «миссия 3 из 14» ещё тогда, когда в файле их шесть:
/// игрок должен понимать, что впереди, а не решать, что история кончилась на полуслове.
/// </param>
public sealed record StoryCampaign(string Name, IReadOnlyList<StoryMission>? Missions = null, int Total = 0)
{
    [JsonIgnore] public IReadOnlyList<StoryMission> MissionList => Missions ?? [];

    /// <summary>Сколько миссий всего: заявленное число, а если его не задали — сколько есть.</summary>
    [JsonIgnore] public int Length => Math.Max(Total, MissionList.Count);

    public StoryMission? Mission(string? id)
    {
        if (id is null) return null;
        foreach (var mission in MissionList)
            if (mission.Id == id) return mission;
        return null;
    }

    /// <summary>Номер миссии в цепочке; −1 — такой больше нет в файле.</summary>
    public int IndexOf(string? id)
    {
        if (id is null) return -1;
        var list = MissionList;
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id) return i;
        return -1;
    }

    /// <summary>
    /// Следующая невыполненная миссия по порядку; null — всё, что есть в файле, пройдено.
    /// Порядок в файле и есть цепочка: <c>after</c> отдельным полем был бы вторым источником правды.
    /// </summary>
    public StoryMission? Next(IReadOnlyCollection<string> done)
    {
        foreach (var mission in MissionList)
            if (!done.Contains(mission.Id)) return mission;
        return null;
    }
}

/// <summary>
/// Сюжетные кампании из shared/story.json (M20a). Здесь только данные и проверки: прогресс пилота живёт
/// в его профиле, а отыгрывает кампанию комната (Sro.Server.Game.Room, RoomStory).
///
/// Кампания персональная: она меняет состояние игрока — флаги, предметы, отношение, — но не мир.
/// Единственная правка мира, которой она потребовала, сделана данными: поселение на Нова-Прайм.
/// </summary>
public sealed record StoryRules(IReadOnlyDictionary<string, StoryCampaign>? Campaigns = null)
{
    public const string File = "story.json";

    /// <summary>Файла нет или он пуст: сюжета в игре не существует, как до M20.</summary>
    public static readonly StoryRules None = new();

    /// <summary>Триггеры скриптованных появлений и вопросов.</summary>
    public const string OnAccept = "onAccept";
    public const string OnUndock = "onUndock";
    public const string OnPickup = "onPickup";

    public static readonly string[] Triggers = [OnAccept, OnUndock, OnPickup];

    /// <summary>Чем кончается миссия: сдачей в месте или ответом в диалоге.</summary>
    public const string FinishDock = "dock";
    public const string FinishChoice = "choice";

    /// <summary>
    /// Виды, на которых сюжет держится. Новых глаголов кампания не заводит: всё, что она умеет,
    /// уже умеет доска, — иначе каждая миссия тянула бы за собой правку всех экранов клиента.
    /// </summary>
    public static readonly string[] Kinds =
        [MissionRules.CollectKind, MissionRules.DeliverKind, MissionRules.EscortKind, MissionRules.DefendKind];

    /// <summary>Id предложения сюжетной миссии на доске: «story:quietWar:wreck».</summary>
    public const string OfferPrefix = "story:";

    public static string OfferId(string campaign, string mission) => $"{OfferPrefix}{campaign}:{mission}";

    /// <summary>Разобрать id предложения; false — это обычная работа с доски.</summary>
    public static bool SplitOffer(string? id, out string campaign, out string mission)
    {
        campaign = "";
        mission = "";
        if (id is null || !id.StartsWith(OfferPrefix, StringComparison.Ordinal)) return false;
        var rest = id[OfferPrefix.Length..];
        var colon = rest.IndexOf(':');
        if (colon <= 0 || colon == rest.Length - 1) return false;
        campaign = rest[..colon];
        mission = rest[(colon + 1)..];
        return true;
    }

    [JsonIgnore]
    public IReadOnlyDictionary<string, StoryCampaign> CampaignMap =>
        Campaigns ?? new Dictionary<string, StoryCampaign>();

    /// <summary>Есть ли в игре хоть одна кампания.</summary>
    [JsonIgnore] public bool Any => CampaignMap.Count > 0;

    public StoryCampaign? Campaign(string? id) =>
        id is not null && CampaignMap.TryGetValue(id, out var campaign) ? campaign : null;

    public StoryMission? Mission(string? campaign, string? mission) => Campaign(campaign)?.Mission(mission);

    /// <param name="npcs">Типы NPC — для скриптованных появлений и волн.</param>
    /// <param name="items">Предметы лута — что собирают и что выдают.</param>
    /// <param name="galaxy">Галактика — сверить места и системы; null — не сверять.</param>
    /// <param name="reputation">Шкала отношения — сверить названные ступени; null — не сверять.</param>
    public string? Validate(
        IReadOnlyDictionary<string, NpcType> npcs,
        IReadOnlyDictionary<string, LootItem> items,
        GalaxyRules? galaxy = null,
        ReputationRules? reputation = null)
    {
        foreach (var (id, campaign) in CampaignMap)
        {
            if (string.IsNullOrWhiteSpace(id)) return "campaign id is empty";
            if (campaign is null) return $"{id}: is null";
            if (string.IsNullOrWhiteSpace(campaign.Name)) return $"{id}: name is empty";
            if (campaign.MissionList.Count == 0) return $"{id}: has no missions";
            if (campaign.Total < 0) return $"{id}: total must not be negative";
            if (campaign.Total > 0 && campaign.Total < campaign.MissionList.Count)
                return $"{id}: total {campaign.Total} is less than the {campaign.MissionList.Count} missions in the file";
            var flags = new HashSet<string>(StringComparer.Ordinal);
            var list = campaign.MissionList;
            for (var i = 0; i < list.Count; i++)
            {
                if (Check(list, i, npcs, items, galaxy, reputation, flags) is { } problem)
                    return $"{id}.missions[{i}]: {problem}";
            }
        }
        return null;
    }

    private static string? Check(
        IReadOnlyList<StoryMission> list,
        int index,
        IReadOnlyDictionary<string, NpcType> npcs,
        IReadOnlyDictionary<string, LootItem> items,
        GalaxyRules? galaxy,
        ReputationRules? reputation,
        HashSet<string> flags)
    {
        var mission = list[index];
        if (mission is null) return "is null";
        if (string.IsNullOrWhiteSpace(mission.Id)) return "id is empty";
        for (var i = 0; i < index; i++)
            if (list[i]?.Id == mission.Id) return $"duplicate id '{mission.Id}'";
        if (string.IsNullOrWhiteSpace(mission.Title)) return "title is empty";
        if (string.IsNullOrWhiteSpace(mission.Giver)) return "giver is empty";
        if (Array.IndexOf(Kinds, mission.Kind) < 0)
            return $"unknown kind '{mission.Kind}': must be one of {string.Join(", ", Kinds)}";
        if (mission.Finish is not (FinishDock or FinishChoice))
            return $"unknown finish '{mission.Finish}': must be {FinishDock} or {FinishChoice}";
        if (mission.Reward < 0) return "reward must not be negative";
        if (mission.Count is < 1 or > MissionRules.MaxCount) return $"count must be within 1..{MissionRules.MaxCount}";
        if (mission.Radius < 0) return "radius must not be negative";
        if (mission.Strikes < 1) return "strikes must be at least 1";

        if (galaxy is not null)
        {
            // Места сверяем по всей галактике, а не по виду комнаты: кампания летает через системы.
            if (!galaxy.HasPlace(mission.Place)) return $"unknown place '{mission.Place}'";
            if (mission.Dest is { } dest && !galaxy.HasPlace(dest)) return $"unknown dest '{dest}'";
            if (mission.RepPlace is { } paid && !galaxy.HasPlace(paid)) return $"unknown repPlace '{paid}'";
            if (mission.System is { } system && galaxy.System(system) is null) return $"unknown system '{system}'";
        }
        if (reputation is not null && mission.Rep is { } level && reputation.IndexOf(level) < 0)
            return $"unknown reputation level '{level}'";

        switch (mission.Kind)
        {
            case MissionRules.CollectKind when mission.Item is not { } item || !items.ContainsKey(item):
                return $"collect needs a known item, got '{mission.Item}'";
            case MissionRules.EscortKind when mission.Dest is null:
                return "escort needs a dest: the convoy has to be going somewhere";
            case MissionRules.DefendKind when mission.WaveList.Count == 0:
                return "defend needs waves to send";
        }
        foreach (var give in mission.GiveList)
            if (!items.ContainsKey(give)) return $"unknown item '{give}' in give";

        for (var i = 0; i < mission.SpawnList.Count; i++)
        {
            var spawn = mission.SpawnList[i];
            if (spawn is null) return $"spawns[{i}]: is null";
            if (Array.IndexOf(Triggers, spawn.Trigger) < 0)
                return $"spawns[{i}]: unknown trigger '{spawn.Trigger}': must be one of {string.Join(", ", Triggers)}";
            // Волны — пиратские, а скриптованное появление может быть каким угодно: в шестой миссии
            // приходит звено рейнджеров. Поэтому здесь проверка своя, а не InvasionGroup.Validate.
            if (spawn.Npc is null || !npcs.ContainsKey(spawn.Npc)) return $"spawns[{i}]: unknown npc '{spawn.Npc}'";
            if (spawn.Level is < 1 or > NpcSpawn.MaxLevel)
                return $"spawns[{i}]: level must be within 1..{NpcSpawn.MaxLevel}";
            if (spawn.Count is < 1 or > NpcSpawn.MaxCount)
                return $"spawns[{i}]: count must be within 1..{NpcSpawn.MaxCount}";
            if (galaxy is not null && spawn.Gate is { } gate && galaxy.System(gate) is null)
                return $"spawns[{i}]: unknown gate system '{gate}'";
        }
        for (var i = 0; i < mission.WaveList.Count; i++)
        {
            var wave = mission.WaveList[i];
            if (wave is null || wave.Count == 0) return $"waves[{i}]: is empty";
            for (var j = 0; j < wave.Count; j++)
            {
                if (wave[j] is not { } group) return $"waves[{i}][{j}]: is null";
                if (group.Validate(npcs) is { } problem) return $"waves[{i}][{j}]: {problem}";
            }
        }
        if (mission.Choice is { } choice)
        {
            if (string.IsNullOrWhiteSpace(choice.Question)) return "choice.question is empty";
            if (Array.IndexOf(Triggers, choice.Trigger) < 0)
                return $"choice: unknown trigger '{choice.Trigger}'";
            if (choice.OptionList.Count is < 1 or > MaxOptions)
                return $"choice needs 1..{MaxOptions} options";
            foreach (var option in choice.OptionList)
            {
                if (option is null) return "choice: option is null";
                if (string.IsNullOrWhiteSpace(option.Label)) return "choice: option label is empty";
                if (string.IsNullOrWhiteSpace(option.Flag)) return "choice: option flag is empty";
                // Флаг — память кампании: два одинаковых означали бы, что выбор ничего не решил.
                if (!flags.Add(option.Flag)) return $"choice: duplicate flag '{option.Flag}'";
            }
        }
        else if (mission.Finish == FinishChoice)
        {
            return "finish 'choice' needs a choice to answer";
        }
        return null;
    }

    /// <summary>Кнопок в карточке диалога: две помещаются и на телефоне, третья уже не влезает.</summary>
    public const int MaxOptions = 2;

    public static bool TryParse(
        string json,
        IReadOnlyDictionary<string, NpcType> npcs,
        IReadOnlyDictionary<string, LootItem> items,
        out StoryRules rules,
        out string? error,
        GalaxyRules? galaxy = null,
        ReputationRules? reputation = null)
    {
        rules = None;
        StoryRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoryRules>(json, JsonCatalog.Options);
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }
        if (parsed is null)
        {
            error = "no rules";
            return false;
        }
        error = parsed.Validate(npcs, items, galaxy, reputation);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
