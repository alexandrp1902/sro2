using Sro.Server.Accounts;
using Sro.Server.Net;
using Sro.Sim;
using Sro.Sim.Mech;

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

    /// <summary>Кому и для какой миссии уже выведены на сцену те, кто ждёт на месте (M20b).</summary>
    private readonly Dictionary<int, string> _storyStaged = new();

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
                offer is null && player.Missions.Active?.Offer.Story is null && campaign.Next(passed) is null,
                RelayOpen(player, campaign, passed),
                log?.Flags.Contains(Protocol.SortieWonFlag) == true);
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
        // Место считаем под весь груз сразу: выданная половина ящиков — это не выданный груз.
        var taken = 0.0;
        foreach (var item in mission.GiveList)
        {
            if (player.Cargo.Fits(item, 1, capacity - taken, loot))
            {
                taken += loot.Volume(item);
                continue;
            }
            player.Connection?.Send(new NoticeMsg(Protocol.StoryHoldNotice));
            return false;
        }
        var price = PriceOf(player, mission);
        if (price > 0)
        {
            // Платят вперёд и своими (M20b): реактор — самая большая трата кампании, и она должна
            // ощущаться покупкой, а не строчкой в награде.
            if (player.Credits < price)
            {
                player.Connection?.Send(new NoticeMsg(Protocol.NoCreditsNotice));
                return false;
            }
            player.Credits -= price;
            _log.LogInformation("Player {Id} paid {Price} for story {Mission}", player.Id, price, mission.Id);
        }
        foreach (var item in mission.GiveList) player.Cargo.Add(item, 1);
        Say(player, story, LinesOf(player, story.Campaign, mission, l => l.Accept), mission.Giver, mission.Role);
        StorySpawn(player, mission, StoryRules.OnAccept);
        // Ящики кладём сразу: пилот стоит в доке той же системы, и к вылету они его уже ждут.
        StoryHere(player);
        // Вопрос при взятии (M20b): Ева раскрывается тогда же, когда даёт работу, и пилот отвечает ей,
        // а не выполняет молча. Карточку шлём последней — она ложится поверх реплики.
        Ask(player, story, mission, StoryRules.OnAccept);
        return true;
    }

    /// <summary>
    /// Сколько стоит взяться за работу (M20b): полная цена, а своим — со скидкой. Магазином это не сделать:
    /// общая шкала отношения даёт «Другу» пять процентов, а сопротивление отдаёт реактор вдвое дешевле
    /// не за отношение к прилавку, а потому что это свой человек.
    /// </summary>
    private int PriceOf(Player player, StoryMission mission)
    {
        if (mission.Cost <= 0) return 0;
        if (mission.CostRep is not { } level) return mission.Cost;
        return Balance.Reputation.AtLeast(PlaceRep(player), level) ? mission.CostCut : mission.Cost;
    }

    /// <summary>
    /// Реплики с поправкой на прошлый выбор (M20b): у кого стоит флаг альтернативы, тот слышит свой
    /// вариант. Не заполнен — берётся общий: переписывать второй раз то, что не поменялось, незачем.
    /// </summary>
    private static IReadOnlyList<string>? LinesOf(
        Player player, string campaign, StoryMission mission, Func<StoryLines, IReadOnlyList<string>?> part)
    {
        if (mission.Alt is { Lines: { } alt } fork
            && player.Story.GetValueOrDefault(campaign)?.Flags.Contains(fork.Flag) == true
            && part(alt) is { Count: > 0 } theirs)
            return theirs;
        return mission.Lines is null ? null : part(mission.Lines);
    }

    /// <summary>Задать вопрос миссии, если он задаётся на этом событии.</summary>
    private void Ask(Player player, StoryRef story, StoryMission mission, string trigger)
    {
        if (mission.Choice is not { } choice || choice.Trigger != trigger) return;
        _storyAsked[player.Id] = story;
        player.Connection?.Send(new DialogMsg(
            story.Campaign,
            story.Mission,
            choice.Who ?? mission.Giver,
            choice.Role ?? mission.Role,
            [choice.Question],
            [.. choice.OptionList.Select(o => new DialogOptionDto(o.Label, o.Flag))]));
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
            // Сданное снято ещё в Room.Mission; здесь — только остатки. Лишнее сюжетное уезжает вместе
            // с миссией, а обычный товар остаётся пилоту (M20b): в седьмой собирают медикаменты, и забрать
            // весь запас за то, что он привёз двадцать из тридцати, — это не сдача, а конфискация.
            if (mission.Finish == StoryRules.FinishDock && mission.Item is { } collected
                && Balance.Loot.IsStory(collected))
                player.Cargo.Remove(collected, player.Cargo.Count(collected));
            GiveHull(player, log, mission);
            Say(
                player, story, LinesOf(player, story.Campaign, mission, l => l.Done),
                mission.DoneBy ?? mission.Giver, mission.DoneRole ?? mission.Role);
            // Отношение — одному месту, тому, ради которого работали. Системной половины у сюжета нет:
            // благодарить властей Новы за шестую миссию точно не за что.
            if (mission.RepReward != 0)
                AddRep(player, mission.RepPlace ?? mission.Place, mission.RepReward, Protocol.RepMissionDone);
        }
        Save(player);
        _log.LogInformation("Player {Id} finished story {Campaign}/{Mission}", player.Id, story.Campaign, story.Mission);
    }

    /// <summary>
    /// Корабль в награду (M20b): встаёт в ангаре там, где работу сдали, — пересаживать пилота силой
    /// посреди кампании незачем. Своего корпуса второй раз не дарят, гостю — не дарят вовсе: ему
    /// некуда его записать.
    /// </summary>
    private void GiveHull(Player player, StoryLog log, StoryMission mission)
    {
        if (mission.RewardHull is not { } gift) return;
        if (mission.RewardIf is { } need && !log.Flags.Contains(need)) return;
        if (player.IsGuest || !Hulls.ContainsKey(gift) || !player.Hulls.Add(gift)) return;
        player.HullPlaces[gift] = mission.Destination;
        SendHangar(player);
        _log.LogInformation("Player {Id} got hull {Hull} for story {Mission}", player.Id, gift, mission.Id);
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
        _storyStaged.Remove(player.Id);
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
        StoryHere(player);
        StorySpawn(player, mission, StoryRules.OnUndock);
    }

    /// <summary>
    /// Пилот в системе миссии: разложить её груз и вывести тех, кто ждёт на месте. Зовётся отовсюду,
    /// откуда это может оказаться правдой, — со взятия работы, с вылета и с прилёта в систему.
    /// </summary>
    private void StoryHere(Player player)
    {
        StoryDrops(player);
        if (player.Missions.Active?.Offer.Story is not { } story) return;
        if (StoryMissionOf(story) is not { } mission) return;
        if (StorySystemOf(mission) is { } where && where != SystemId) return;
        if (_storyStaged.GetValueOrDefault(player.Id) == mission.Id) return;
        _storyStaged[player.Id] = mission.Id;
        StorySpawn(player, mission, StoryRules.OnArrive);
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
        if (StorySystemOf(mission) is { } where && where != SystemId) return;
        // Предмет, который роняет корабль, на земле не валяется (M20b): у восьмой и тринадцатой точка —
        // это место встречи, а не склад, и чертёж надо взять с курьера, а не подобрать до его прилёта.
        if (mission.SpawnList.Any(spawn => spawn.Drop == item)) return;
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

    /// <summary>
    /// Где происходит миссия: названная система, а если не названа — та, где стоит место сдачи.
    /// Собирают не всегда там, где сдают (M20b): каркас ждёт в Касторе, приводы — в Барнарде,
    /// а привезти их надо на Прайм.
    /// </summary>
    private string? StorySystemOf(StoryMission mission) =>
        mission.System ?? Balance.Galaxy.SystemOfPlace(mission.Destination);

    /// <summary>Пилот поднял груз: сюжету это повод прислать встречающих и задать вопрос.</summary>
    private void StoryPicked(Player player, LootDrop drop)
    {
        if (player.Missions.Active?.Offer.Story is not { } story) return;
        if (StoryMissionOf(story) is not { Item: { } item } mission || drop.Item != item) return;
        if (player.Cargo.Count(item) < mission.Count) return;

        StorySpawn(player, mission, StoryRules.OnPickup);
        if (mission.Choice is not { Trigger: StoryRules.OnPickup })
        {
            // Без вопроса «собрать» сдаётся в доке, как обычная работа: счёт уже сошёлся.
            SendMissions(player);
            return;
        }
        Ask(player, story, mission, StoryRules.OnPickup);
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
        if (option.Decline)
        {
            // «Не сейчас» (M20b): работа возвращается на доску, цепочка стоит на той же миссии,
            // а уплаченное возвращается — пилот ни за что не платил.
            if (PriceOf(player, mission) is var paid && paid > 0) player.Credits += paid;
            Abandon(player);
            SendCargo(player);
            SendMissions(player);
            Save(player);
            return;
        }
        if (mission.Finish == StoryRules.FinishChoice) Complete(player);
        else Save(player);
    }

    /// <summary>
    /// Вызвать корабли сюжета. Идут они тем же путём, что засады на конвой, — через <see cref="SpawnWave"/>,
    /// но с именем над корпусом и с пометкой «сюжетный»: за такого не платят и отношение за него не меняют.
    /// </summary>
    private void StorySpawn(Player player, StoryMission mission, string trigger)
    {
        // Миссия, назвавшая свою систему, и сцены свои играет там (M20b): патруль над обломками Барнарда
        // не должен встречать пилота, который вышел из дока в Нове.
        if (StorySystemOf(mission) is { } stage && mission.System is not null && stage != SystemId) return;
        foreach (var spawn in mission.SpawnList)
        {
            if (spawn.Trigger != trigger) continue;
            var point = SpawnPointOf(player, spawn);
            if (!_storyActors.TryGetValue(player.Id, out var runId)) _storyActors[player.Id] = runId = ++_runCount;
            // Убегающий встаёт там, где написано: залетать ему неоткуда — он уже в системе,
            // он из неё уходит.
            var sent = SpawnWave(
                [new InvasionGroup(spawn.Npc, spawn.Level, spawn.Count)],
                point, invasionId: 0, missionId: runId, onSite: spawn.Gate is null || spawn.Flee,
                storyName: spawn.Name, touch: npc => Tune(npc, player, spawn));
            if (sent > 0) player.Connection?.Send(new NoticeMsg(Protocol.AmbushNotice));
            _log.LogInformation(
                "Story {Mission} sent {Count} × {Npc} to player {Id} on {Trigger}",
                mission.Id, sent, spawn.Npc, player.Id, trigger);
        }
        if (_storyActors.ContainsKey(player.Id)) BroadcastPlayers();
    }

    /// <summary>
    /// Чем вызванный корабль отличается от рядового налётчика (M20b): за кем он пришёл, стоит ли он
    /// на посту, что уронит и не уходит ли он в прыжок, не дожидаясь разговора.
    /// </summary>
    private void Tune(Pirate npc, Player player, StorySpawn spawn)
    {
        npc.OwnerId = player.Id;
        npc.HoldsGround = spawn.Hold;
        npc.StoryDrop = spawn.Drop;
        if (!spawn.Flee || spawn.Gate is not { } to || Balance.SystemDef.GateTo(to) is not { } gate) return;
        // Курьер не дерётся и не ждёт: с первого тика он идёт к вратам и там заряжает прыжок. Своего ИИ
        // ему не нужно — ровно так уходит из системы любой налётчик, а попадание сбивает ему подготовку
        // так же, как игроку (M15.7). Сколько у пилота времени — решает расстояние до врат, не код.
        npc.ExitX = gate.X;
        npc.ExitY = gate.Y;
        npc.ExitIsGate = true;
        npc.State = PirateState.Leave;
        npc.PatrolUntilTick = Tick;
    }

    /// <summary>Что роняют сбитые сюжетные корабли (M20b): адресно хозяину миссии и без срока годности.
    /// Иначе чертёж подобрал бы посторонний или он истлел бы за две минуты, и цепочка встала бы.</summary>
    private void StoryKills()
    {
        foreach (var kill in _kills)
        {
            if (_ships.GetValueOrDefault(kill.Id) is not Pirate { StoryDrop: { } item } npc) continue;
            if (!_players.TryGetValue(npc.OwnerId, out var owner)) continue;
            _loot.SpillOne(
                Balance.Loot, item, 1, npc.Ship.X, npc.Ship.Y, npc.DeathVx, npc.DeathVy, Tick,
                owner.Id, waits: true);
            _log.LogInformation("Story ship {Npc} dropped {Item} for player {Id}", npc.Id, item, owner.Id);
        }
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
    private void Say(Player player, StoryRef story, IReadOnlyList<string>? lines, string who, string role) =>
        Say(player, story.Campaign, story.Mission, lines, who, role);

    private static void Say(Player player, string campaign, string mission, IReadOnlyList<string>? lines, string who, string role)
    {
        if (lines is null || lines.Count == 0) return;
        var log = player.StoryOf(campaign);
        log.Lines = [.. lines];
        player.Connection?.Send(new DialogMsg(campaign, mission, who, role, lines));
    }

    // ------------------------------------------------------------------ ретранслятор и мехи (M21)

    /// <summary>
    /// Ретранслятор (M20b): кампания пройдена целиком, и пилот стоит там, где она кончилась. Место берём
    /// из последней миссии, а не строкой в коде: вторая кампания кончится в другом доке, и искать её
    /// финал по имени места никто не должен.
    /// </summary>
    private static bool RelayOpen(Player player, StoryCampaign campaign, IReadOnlyCollection<string> passed) =>
        campaign.MissionList.Count > 0 && campaign.Next(passed) is null
            && player.Docked && player.DockedPlace == campaign.MissionList[^1].Place;

    /// <summary>Кампания, чей ретранслятор открыт пилоту здесь и сейчас; null — такой нет.</summary>
    public string? RelayHere(Player player)
    {
        foreach (var (id, campaign) in Balance.Story.CampaignMap)
        {
            if (RelayOpen(player, campaign, player.Story.GetValueOrDefault(id)?.Done ?? [])) return id;
        }
        return null;
    }

    /// <summary>Отладка (SRO_STORY_SKIP): отметить все миссии кампании пройденными.</summary>
    public void SkipStory(Player player, string campaign)
    {
        if (Balance.Story.Campaign(campaign) is not { } rules) return;
        var log = player.StoryOf(campaign);
        var added = false;
        foreach (var mission in rules.MissionList) added |= log.Done.Add(mission.Id);
        if (!added) return;
        Save(player);
        SendMissions(player);
        _log.LogWarning("Player {Id}: campaign {Campaign} marked done by SRO_STORY_SKIP", player.Id, campaign);
    }

    /// <summary>Реплики наземной миссии — той же карточкой и в тот же журнал, что сюжет.</summary>
    public void MechSay(Player player, string campaign, string mission, MechLines? lines)
    {
        if (lines is not null) Say(player, campaign, mission, lines.Lines, lines.Who, lines.Role);
        Save(player);
    }

    /// <summary>
    /// Победа в наземной миссии. Платим только за первую: бой повторяемый, и фармить его незачем.
    /// true — это была первая.
    /// </summary>
    public bool MechWon(Player player, string campaign, string mission, MechMission rules)
    {
        var log = player.StoryOf(campaign);
        var first = log.Flags.Add(Protocol.SortieWonFlag);
        if (first && rules.Reward > 0) Pay(player, rules.Reward);
        MechSay(player, campaign, mission, first ? rules.Win : null);
        SendMissions(player);
        _log.LogInformation("Player {Id} won the mech mission {Mission} (first: {First})", player.Id, mission, first);
        return first;
    }
}
