using Sro.Server.Accounts;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Комната и сюжетные кампании (M20a). Кампания не заводит новых видов заданий: она берёт те, что доска
/// умеет с M14, и добавляет к ним память о выполненном, имена, реплики, выбор и скриптованные появления.
/// Поэтому сюжетная миссия — это обычное <see cref="ActiveMission"/> с нагрузкой <see cref="StoryRef"/>,
/// и весь путь «взял → цель на экране → сдал → провалил» у неё общий с работой с доски.
///
/// Кампания персональная: она меняет пилота, а не мир. Единственное, что она изменила для всех, —
/// поселение на Нова-Прайм, и это данные, а не код.
/// </summary>
public sealed partial class Room
{
    /// <summary>
    /// Корабли, вызванные сюжетом для этого пилота: id пилота → номер прогона, под которым они помечены.
    /// Это не <see cref="MissionRun"/> нарочно: прогон кончается прыжком и гибелью и тянет за собой провал
    /// задания, а сюжетной засаде пятой миссии провал не полагается — она просто встречает пилота у врат.
    /// </summary>
    private readonly Dictionary<int, int> _storyActors = [];

    /// <summary>Кому и для какой миссии уже разложен скриптованный груз: второй раз раскладывать не надо.</summary>
    private readonly Dictionary<int, string> _storyDrops = new();

    /// <summary>Кто сейчас стоит перед выбором в диалоге: id пилота → миссия, которая спросила.</summary>
    private readonly Dictionary<int, StoryRef> _storyAsked = [];

    /// <summary>Миссия из баланса по нагрузке предложения; null — её убрала горячая правка story.json.</summary>
    private StoryMission? StoryMissionOf(StoryRef? story) =>
        story is null ? null : Balance.Story.Mission(story.Campaign, story.Mission);

    // ------------------------------------------------------------------ профиль

    /// <summary>
    /// Кампании из профиля (M20a). Профиль старше M20 их не знает — это просто «кампания не начата»,
    /// и первая миссия ждёт такого пилота на доске наравне с новичком.
    /// </summary>
    private static void LoadStory(Player player, AccountProfile? profile)
    {
        if (profile?.Story is not { } saved) return;
        foreach (var (campaign, progress) in saved)
        {
            if (progress is null) continue;
            var log = player.StoryOf(campaign);
            // Миссии, которой больше нет в файле, в списке выполненных просто не встретится: цепочка
            // считается по тому, что написано сейчас, а не по тому, что было записано тогда.
            foreach (var id in progress.Done) log.Done.Add(id);
            foreach (var flag in progress.Flags ?? []) log.Flags.Add(flag);
            log.Lines = [.. progress.Lines ?? []];
        }
    }

    /// <summary>Кампании в профиль; null — пилот сюжета не касался, и писать в файл нечего.</summary>
    private static IReadOnlyDictionary<string, StoryProgress>? SaveStory(Player player)
    {
        if (player.Story.Count == 0) return null;
        var saved = new SortedDictionary<string, StoryProgress>(StringComparer.Ordinal);
        foreach (var (campaign, log) in player.Story)
        {
            if (!log.Any) continue;
            saved[campaign] = new StoryProgress(
                [.. log.Done.Order(StringComparer.Ordinal)],
                log.Flags.Count == 0 ? null : [.. log.Flags.Order(StringComparer.Ordinal)],
                log.Lines.Count == 0 ? null : [.. log.Lines]);
        }
        return saved.Count == 0 ? null : saved;
    }

    // ------------------------------------------------------------------ предложение и журнал

    /// <summary>
    /// Что показать пилоту про кампанию: сколько пройдено, последние реплики и работа, доступная здесь.
    /// Шлётся и тогда, когда работы тут нет: журналу надо что-то показывать в любом доке.
    /// </summary>
    private StoryStateDto? StoryState(Player player)
    {
        var rules = Balance.Story;
        if (!rules.Any) return null;

        // Взятая сюжетная миссия главнее места: журнал должен говорить о ней, где бы пилот ни стоял.
        if (player.Missions.Active?.Offer.Story is { } taken && rules.Campaign(taken.Campaign) is { } running)
            return State(taken.Campaign, running, null);

        foreach (var (id, campaign) in rules.CampaignMap)
        {
            var log = player.Story.GetValueOrDefault(id);
            IReadOnlyCollection<string> done = log?.Done ?? [];
            var next = campaign.Next(done);
            // Написанное кончилось: у того, кто это прошёл, в журнале «продолжение следует».
            if (next is null)
            {
                if (done.Count > 0) return State(id, campaign, null);
                continue;
            }
            var offer = Available(player, next) ? StoryOffer(id, campaign, next) : null;
            // Кампания, к которой пилот ещё не притронулся и которую здесь не дают, — не его дело:
            // журнал о ненайденной истории молчит.
            if (offer is null && done.Count == 0) continue;
            return State(id, campaign, offer);
        }
        return null;

        StoryStateDto State(string id, StoryCampaign campaign, MissionOffer? offer)
        {
            var log = player.Story.GetValueOrDefault(id);
            // Считаем только то, что в файле и есть: id, оставшийся в профиле от убранной миссии,
            // не должен превращать «3 из 14» в «4 из 14».
            var count = log is null ? 0 : campaign.MissionList.Count(m => log.Done.Contains(m.Id));
            IReadOnlyCollection<string> passed = log?.Done ?? [];
            return new StoryStateDto(
                id,
                campaign.Name,
                count,
                campaign.Length,
                log?.Lines ?? [],
                offer,
                offer is null && player.Missions.Active?.Offer.Story is null && campaign.Next(passed) is null);
        }
    }

    /// <summary>Дают ли эту миссию здесь и сейчас: то место, свободные руки, хватает отношения.</summary>
    private bool Available(Player player, StoryMission mission)
    {
        if (!player.Docked || player.DockedPlace != mission.Place) return false;
        // Слот задания один на пилота: сюжет и работа с доски делят его, как две обычные работы.
        if (player.Missions.Active is not null) return false;
        return mission.Rep is not { } level || Balance.Reputation.AtLeast(SystemRep(player), level);
    }

    /// <summary>
    /// Сюжетная миссия как предложение доски. Вид, система и место назначения берутся из неё же,
    /// а все русские строки едут готовыми: собирать их клиенту не из чего — у сюжета нет «типа пирата»
    /// и «числа единиц», по которым доска строит свои заголовки.
    /// </summary>
    private MissionOffer StoryOffer(string campaign, StoryCampaign chain, StoryMission mission)
    {
        var number = chain.IndexOf(mission.Id) + 1;
        var story = new StoryRef(
            campaign, mission.Id, chain.Name, mission.Title, mission.Brief, mission.Objective, mission.Hint,
            mission.Giver, mission.Role, number, chain.Length);
        // Система у всех видов одна и та же — та, где стоит место назначения: туда ведёт и доставка,
        // и конвой, и оборона, и обломки, у которых собирают.
        var system = mission.System ?? Balance.Galaxy.SystemOfPlace(mission.Destination) ?? SystemId;
        return new MissionOffer(
            StoryRules.OfferId(campaign, mission.Id),
            mission.Kind,
            system,
            Npc: null,
            Item: mission.Item,
            Count: mission.Count,
            Reward: mission.Reward,
            From: mission.Place,
            Radius: mission.Radius,
            Place: mission.Dest,
            Story: story);
    }

    /// <summary>
    /// Всё, что можно взять здесь: доска плюс сюжет. Один список на два места — взятие ищет задание
    /// по id именно в нём, и разойтись с тем, что пилот видит на экране, ему нечем.
    /// </summary>
    private IReadOnlyList<MissionOffer> OffersFor(Player player)
    {
        var board = Board(player);
        if (StoryState(player)?.Offer is not { } story) return board;
        return [story, .. board];
    }

    // ------------------------------------------------------------------ жизненный цикл миссии

    /// <summary>
    /// Сюжетную работу взяли: выдать её груз, сказать реплику и позвать тех, кто приходит сразу.
    /// </summary>
    /// <returns>false — в трюме не хватило места под сюжетный груз, и работа не берётся.</returns>
    private bool StoryAccept(Player player, StoryRef story)
    {
        if (StoryMissionOf(story) is not { } mission) return true;
        var loot = Balance.Loot;
        var capacity = player.Effective(Balance).Cargo;
        foreach (var item in mission.GiveList)
        {
            if (player.Cargo.Fits(item, 1, capacity, loot)) continue;
            player.Connection?.Send(new NoticeMsg(Protocol.StoryHoldNotice));
            return false;
        }
        foreach (var item in mission.GiveList) player.Cargo.Add(item, 1);
        Say(player, story, mission.Lines?.Accept, mission.Giver, mission.Role);
        StorySpawn(player, mission, StoryRules.OnAccept);
        // Ящики кладём сразу: пилот стоит в доке той же системы, и к вылету они его уже ждут.
        StoryDrops(player);
        return true;
    }

    /// <summary>Сюжетная работа сдана: записать её, заплатить отношением и договорить.</summary>
    private void StoryComplete(Player player, StoryRef story)
    {
        var log = player.StoryOf(story.Campaign);
        log.Done.Add(story.Mission);
        var mission = StoryMissionOf(story);
        StoryEnd(player);
        if (mission is not null)
        {
            // Гружёное этой миссией уезжает вместе с ней: дальше оно не нужно, а трюм занимает.
            foreach (var item in mission.GiveList) player.Cargo.Remove(item, player.Cargo.Count(item));
            // Собранное сдаётся, только если миссия кончается сдачей. Та, что кончается выбором,
            // уже решила судьбу предмета кнопкой: «отдал» его забрал, «оставил» — оставил в трюме уликой.
            if (mission.Finish == StoryRules.FinishDock && mission.Item is { } collected)
                player.Cargo.Remove(collected, player.Cargo.Count(collected));
            Say(player, story, mission.Lines?.Done, mission.DoneBy ?? mission.Giver, mission.DoneRole ?? mission.Role);
            // Отношение — одному месту, тому, ради которого работали. Системной половины у сюжета нет:
            // благодарить властей Новы за шестую миссию точно не за что.
            if (mission.RepReward != 0)
                AddRep(player, mission.RepPlace ?? mission.Place, mission.RepReward, Protocol.RepMissionDone);
        }
        Save(player);
        _log.LogInformation("Player {Id} finished story {Campaign}/{Mission}", player.Id, story.Campaign, story.Mission);
    }

    /// <summary>
    /// Сюжетная работа кончилась ничем — провалена или брошена. Отношение при этом не двигается:
    /// кампания и так возвращает миссию на доску, а штрафовать пилота за то, что его сбили по дороге
    /// к точке перелома, значило бы наказывать за попытку пройти сюжет.
    /// </summary>
    private void StoryDropped(Player player)
    {
        StoryEnd(player);
        // Выданный сюжетный груз остаётся в трюме: взяв миссию снова, пилот повезёт тот же ящик.
    }

    /// <summary>Убрать всё, что кампания положила и вызвала для этого пилота в этой комнате.</summary>
    private void StoryEnd(Player player)
    {
        _storyAsked.Remove(player.Id);
        _storyDrops.Remove(player.Id);
        if (_loot.RemoveOwned(player.Id)) ClearMissingLootTargets();
        if (!_storyActors.Remove(player.Id, out var runId)) return;
        foreach (var npc in _pirates)
        {
            if (npc.MissionId != runId) continue;
            npc.MissionId = 0;
            if (npc.IsDead || npc.State == PirateState.Leave) continue;
            npc.State = PirateState.Leave;
            npc.TargetId = 0;
            npc.FireHeld = false;
            npc.PatrolUntilTick = Tick;
        }
        BroadcastPlayers();
    }

    // ------------------------------------------------------------------ скрипты

    /// <summary>Пилот вылетел: сюжет раскладывает ящики и зовёт тех, кто ждёт именно вылета.</summary>
    private void StoryUndock(Player player)
    {
        if (player.Missions.Active?.Offer.Story is not { } story || StoryMissionOf(story) is not { } mission) return;
        StoryDrops(player);
        StorySpawn(player, mission, StoryRules.OnUndock);
    }

    /// <summary>
    /// Скриптованный груз миссии: столько же стопок, сколько надо собрать, у названной точки.
    /// Ящики адресные — видят их все, а берёт хозяин: иначе двое, идущие по одной кампании,
    /// растаскивали бы их друг у друга, и цепочка вставала бы намертво.
    /// </summary>
    private void StoryDrops(Player player)
    {
        if (player.Missions.Active?.Offer.Story is not { } story) return;
        if (StoryMissionOf(story) is not { Point: { } point, Item: { } item } mission) return;
        if (mission.Kind != MissionRules.CollectKind) return;
        if (Balance.Galaxy.SystemOfPlace(mission.Destination) is { } where && where != SystemId) return;
        if (_storyDrops.GetValueOrDefault(player.Id) == mission.Id) return;
        // Сколько уже в трюме, столько и не кладём: пилот вернулся в систему с половиной ящиков.
        var left = mission.Count - player.Cargo.Count(item);
        if (left <= 0) return;
        _storyDrops[player.Id] = mission.Id;
        _loot.SpillOne(Balance.Loot, item, left, point.X, point.Y, 0, 0, Tick, player.Id, waits: true);
        _log.LogInformation(
            "Player {Id} gets {Count} {Item} at ({X}, {Y}) for story {Mission}",
            player.Id, left, item, point.X, point.Y, mission.Id);
    }

    /// <summary>Пилот поднял груз: сюжету это повод прислать встречающих и задать вопрос.</summary>
    private void StoryPicked(Player player, LootDrop drop)
    {
        if (player.Missions.Active?.Offer.Story is not { } story) return;
        if (StoryMissionOf(story) is not { Item: { } item } mission || drop.Item != item) return;
        if (player.Cargo.Count(item) < mission.Count) return;

        StorySpawn(player, mission, StoryRules.OnPickup);
        if (mission.Choice is not { Trigger: StoryRules.OnPickup } choice)
        {
            // Без вопроса «собрать» сдаётся в доке, как обычная работа: счёт уже сошёлся.
            SendMissions(player);
            return;
        }
        _storyAsked[player.Id] = story;
        player.Connection?.Send(new DialogMsg(
            story.Campaign,
            story.Mission,
            choice.Who ?? mission.Giver,
            choice.Role ?? mission.Role,
            [choice.Question],
            [.. choice.OptionList.Select(o => new DialogOptionDto(o.Label, o.Flag))]));
    }

    /// <summary>
    /// Ответ в диалоге. Флаг проверяется по миссии, которая спрашивала: клиент не может ни ответить
    /// на невыданный вопрос, ни придумать себе вариант, которого не было на кнопке.
    /// </summary>
    private void StoryChoose(Player player, string? flag)
    {
        if (!_storyAsked.TryGetValue(player.Id, out var story)) return;
        if (player.Missions.Active?.Offer.Story is not { } active || active.Mission != story.Mission) return;
        if (StoryMissionOf(story) is not { Choice: { } choice } mission) return;
        if (choice.OptionList.FirstOrDefault(o => o.Flag == flag) is not { } option) return;

        _storyAsked.Remove(player.Id);
        var log = player.StoryOf(story.Campaign);
        log.Flags.Add(option.Flag);
        if (option.Take && mission.Item is { } item) player.Cargo.Remove(item, player.Cargo.Count(item));
        Say(player, story, option.Lines, mission.DoneBy ?? mission.Giver, mission.DoneRole ?? mission.Role);
        SendCargo(player);
        _log.LogInformation("Player {Id} chose {Flag} in story {Mission}", player.Id, option.Flag, story.Mission);
        if (mission.Finish == StoryRules.FinishChoice) Complete(player);
        else Save(player);
    }

    /// <summary>
    /// Вызвать корабли сюжета. Идут они тем же путём, что засады на конвой, — через <see cref="SpawnWave"/>,
    /// но с именем над корпусом и с пометкой «сюжетный»: за такого не платят и отношение за него не меняют.
    /// </summary>
    private void StorySpawn(Player player, StoryMission mission, string trigger)
    {
        foreach (var spawn in mission.SpawnList)
        {
            if (spawn.Trigger != trigger) continue;
            var point = SpawnPointOf(player, spawn);
            if (!_storyActors.TryGetValue(player.Id, out var runId)) _storyActors[player.Id] = runId = ++_runCount;
            var sent = SpawnWave(
                [new InvasionGroup(spawn.Npc, spawn.Level, spawn.Count)],
                point, invasionId: 0, missionId: runId, onSite: spawn.Gate is null, storyName: spawn.Name);
            if (sent > 0) player.Connection?.Send(new NoticeMsg(Protocol.AmbushNotice));
            _log.LogInformation(
                "Story {Mission} sent {Count} × {Npc} to player {Id} on {Trigger}",
                mission.Id, sent, spawn.Npc, player.Id, trigger);
        }
        if (_storyActors.ContainsKey(player.Id)) BroadcastPlayers();
    }

    /// <summary>Где встают вызванные: в названной точке, у названных врат или рядом с пилотом.</summary>
    private (double X, double Y) SpawnPointOf(Player player, StorySpawn spawn)
    {
        if (spawn.At is { } at) return (at.X, at.Y);
        if (spawn.Gate is { } to && Balance.SystemDef.GateTo(to) is { } gate) return (gate.X, gate.Y);
        // Рядом с пилотом, но не в упор: драка должна начаться с подлёта, а не с тарана.
        var angle = _ai.NextDouble() * 2 * Math.PI;
        return (player.Ship.X + AmbushSide * Math.Cos(angle), player.Ship.Y + AmbushSide * Math.Sin(angle));
    }

    // ------------------------------------------------------------------ реплики

    /// <summary>
    /// Сказать пилоту карточкой и запомнить сказанное в журнале: вернувшись через день, он должен
    /// понимать, на чём остановился, а не гадать, куда его вели.
    /// </summary>
    private void Say(Player player, StoryRef story, IReadOnlyList<string>? lines, string who, string role)
    {
        if (lines is null || lines.Count == 0) return;
        var log = player.StoryOf(story.Campaign);
        log.Lines = [.. lines];
        player.Connection?.Send(new DialogMsg(story.Campaign, story.Mission, who, role, lines));
    }
}
