using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Шаг обучения (GDD §54). Что засчитывает шаг, решает его id — см. <see cref="MissionRules.TutorialIds"/>.</summary>
/// <param name="Title">Что сделать — строка трекера цели.</param>
/// <param name="Hint">Как это сделать — подсказка под ней.</param>
/// <param name="Reward">Кредиты за шаг.</param>
public sealed record TutorialStep(string Id, string Title, string Hint = "", int Reward = 0);

/// <summary>Шаблон задания «уничтожить» (GDD §36): count пиратов типа npc в системе рядом со станцией.</summary>
/// <param name="Npc">Тип из npcs.json; null — любой пират.</param>
/// <param name="Reward">Кредитов за одного — в системе опасности 1; дальше растёт на dangerBonus за ступень.</param>
public sealed record KillTemplate(string? Npc, int Min, int Max, int Reward, double Weight = 1);

/// <summary>Шаблон «собрать» (GDD §36): привезти на любую станцию count предметов item.</summary>
/// <param name="Factor">Награда = цена предмета × count × factor: выгоднее, чем просто продать.</param>
public sealed record CollectTemplate(string Item, int Min, int Max, double Factor = 1.5, double Weight = 1);

/// <summary>Шаблон «доставить» (GDD §36): груз на другую станцию. Груз занимает трюм, не продаётся и не теряется.</summary>
/// <param name="PerUnit">Кредитов за единицу груза.</param>
/// <param name="PerJump">Кредитов за каждый прыжок кратчайшего пути.</param>
public sealed record DeliverTemplate(int Min, int Max, int PerUnit, int PerJump, double Weight = 1);

/// <summary>
/// Шаблон «сопровождение» (M14): конвой выходит со станции к вратам, пилот держится рядом, по пути — засады.
/// </summary>
/// <param name="MinWaves">Засад по пути — не меньше стольких; они же <see cref="MissionOffer.Count"/>.</param>
/// <param name="Reward">Кредитов за саму работу — в системе опасности 1.</param>
/// <param name="PerWave">Кредитов за каждую засаду сверх того.</param>
/// <param name="Radius">Дальше этого от конвоя пилот считается отставшим.</param>
/// <param name="AwaySeconds">Столько секунд можно быть вне радиуса; потом провал.</param>
public sealed record EscortTemplate(
    int MinWaves,
    int MaxWaves,
    int Reward,
    int PerWave,
    double Radius = 900,
    int AwaySeconds = 25,
    double Weight = 1);

/// <summary>
/// Шаблон «патруль с рейнджерами» (M14): звено обходит точки системы и ждёт пилота на каждой.
/// </summary>
/// <param name="Min">Точек маршрута — не меньше стольких; они же <see cref="MissionOffer.Count"/>.</param>
/// <param name="PerPoint">Кредитов за каждую точку сверх <paramref name="Reward"/>.</param>
/// <param name="Npc">Тип звена из npcs.json; должен быть рейнджером.</param>
/// <param name="Wing">Сколько рейнджеров в звене.</param>
/// <param name="Rank">Их уровень в системе опасности 1; дальше растёт, как у волн вторжения.</param>
/// <param name="Radius">Ближе этого к точке пилот считается подошедшим.</param>
/// <param name="Rep">Минимальная ступень отношения системы; null — берут всех.</param>
public sealed record PatrolTemplate(
    int Min,
    int Max,
    int Reward,
    int PerPoint,
    string Npc = "ranger",
    int Wing = 2,
    int Rank = 1,
    double Radius = 700,
    string? Rep = null,
    double Weight = 1);

/// <summary>
/// Шаблон «важное письмо» (M14): доставить в место другой системы к сроку. Места в трюме письмо не занимает.
/// </summary>
/// <param name="PerJump">Кредитов за каждый прыжок кратчайшего пути.</param>
/// <param name="SecondsPerJump">Столько секунд даётся на прыжок.</param>
/// <param name="Seconds">Запас сверх того: на дорогу до врат и от них.</param>
/// <param name="MaxHops">Дальше стольких прыжков письма не носят.</param>
/// <param name="Intercept">С такой вероятностью за курьером посылают перехватчиков; 0 — никогда.</param>
public sealed record CourierTemplate(
    int Reward,
    int PerJump,
    int SecondsPerJump,
    int Seconds = 120,
    int MaxHops = 3,
    double Intercept = 0,
    double Weight = 1);

/// <summary>
/// Шаблон «охота на метеориты» (M14): сбить count камней в системе, где они летают.
/// Разбившийся о корабль не считается — у тарана нет убийцы, и лута он тоже не даёт.
/// </summary>
/// <param name="Size">Размер из meteors.json, который засчитывается; null — любой.</param>
/// <param name="Reward">Кредитов за один камень — в системе опасности 1.</param>
public sealed record HuntTemplate(string? Size, int Min, int Max, int Reward, double Weight = 1);

/// <summary>
/// Задание на доске станции или взятое. Текст собирает клиент — из вида, системы, типа пирата и предмета.
/// </summary>
/// <param name="Id">Уникально в пределах доски: по нему задание и берут.</param>
/// <param name="Kind"><see cref="MissionRules.KillKind"/>, <see cref="MissionRules.CollectKind"/> или <see cref="MissionRules.DeliverKind"/>.</param>
/// <param name="System">Kill — где бить; deliver — куда везти; collect — null, сдать можно на любой станции.</param>
/// <param name="Npc">Kill: тип пирата; null — любой.</param>
/// <param name="Item">Collect: предмет из loot.json.</param>
/// <param name="Count">Сколько уничтожить, собрать или единиц груза.</param>
/// <param name="From">Система станции, где задание выдали.</param>
/// <param name="Count">
/// Сколько сделать: kill — пиратов, collect и deliver — единиц, hunt — камней, patrol — точек маршрута,
/// escort — засад по пути, courier — всегда 1.
/// </param>
public sealed record MissionOffer(
    string Id,
    string Kind,
    string? System,
    string? Npc,
    string? Item,
    int Count,
    int Reward,
    string From,
    /// <summary>Особый контракт доски: даётся только друзьям станции и платит больше обычного (M13).</summary>
    bool Elite = false,
    /// <summary>Hunt: какой размер камня засчитывается (meteors.json); null — любой (M14).</summary>
    string? Size = null,
    /// <summary>Courier: сколько секунд на доставку от взятия; 0 — срока нет (M14).</summary>
    int Seconds = 0,
    /// <summary>Escort: в каком радиусе держаться у конвоя; patrol: как близко подойти к точке (M14).</summary>
    double Radius = 0)
{
    /// <summary>
    /// Кому «спасибо» за выполнение и с кого спрос за провал. Обычно — станции заказа; письму платит
    /// получатель: заказчик своё уже отдал, ждут его на том конце (M14).
    /// </summary>
    [JsonIgnore] public string Payer => Kind == MissionRules.CourierKind ? System ?? From : From;
}

/// <summary>Взятое задание: что и сколько уже сделано. Хранится в аккаунте пилота.</summary>
/// <param name="Progress">Kill — сколько уничтожено; у collect и deliver не растёт: их сдают на станции целиком.</param>
/// <param name="Until">
/// Courier: unix-секунды, когда выйдет срок; 0 — срока нет. Именно момент, а не остаток: пилот уходит
/// из игры и возвращается, и остаток тогда можно было бы обнулять перезаходом (M14).
/// </param>
public sealed record ActiveMission(MissionOffer Offer, int Progress = 0, long Until = 0);

/// <summary>
/// Задания и обучение из shared/missions.json (GDD §36, §54). Доска каждой станции генерируется из шаблонов
/// по сиду пилота: одна и та же, пока пилот не взял или не сдал задание, и своя у каждой станции.
/// </summary>
/// <param name="Offers">Сколько заданий на доске.</param>
/// <param name="DangerBonus">Прибавка к награде за уничтожение за каждую ступень опасности выше первой.</param>
public sealed record MissionRules(
    int Offers = 4,
    double DangerBonus = 0.35,
    IReadOnlyList<TutorialStep>? Tutorial = null,
    IReadOnlyList<KillTemplate>? Kill = null,
    IReadOnlyList<CollectTemplate>? Collect = null,
    IReadOnlyList<DeliverTemplate>? Deliver = null,
    IReadOnlyList<EscortTemplate>? Escort = null,
    IReadOnlyList<PatrolTemplate>? Patrol = null,
    IReadOnlyList<CourierTemplate>? Courier = null,
    IReadOnlyList<HuntTemplate>? Hunt = null,
    /// <summary>
    /// Засады на конвой (M14): i-я волна — i-й список. Волн в задании больше, чем списков, — берётся последний.
    /// </summary>
    IReadOnlyList<IReadOnlyList<InvasionGroup>>? Ambush = null)
{
    public const string File = "missions.json";

    public const string KillKind = "kill";
    public const string CollectKind = "collect";
    public const string DeliverKind = "deliver";
    public const string EscortKind = "escort";
    public const string PatrolKind = "patrol";
    public const string CourierKind = "courier";
    public const string HuntKind = "hunt";

    /// <summary>
    /// «Живое» задание (M14): у него есть актёры в системе — конвой или звено. Живёт только в своей комнате
    /// и только пока пилот в космосе: прыжок, гибель и обрыв связи его кончают, на диск оно не переживает.
    /// </summary>
    public static bool IsLive(string? kind) => kind is EscortKind or PatrolKind;

    /// <summary>
    /// Задание, которое кончается вместе с кораблём (M14): письмо тонет с ним — это решение этапа,
    /// а у живых заданий вместе с вылетом пропадают актёры. Груз доставки и счёт убитых гибель переживают.
    /// </summary>
    public static bool DiesWithTheShip(string? kind) => IsLive(kind) || kind == CourierKind;

    /// <summary>Шаги обучения: вылететь, уничтожить дрон, подобрать груз, продать, прыгнуть через врата.</summary>
    public const string UndockStep = "undock";
    public const string DroneStep = "drone";
    public const string GrabStep = "grab";
    public const string SellStep = "sell";
    public const string JumpStep = "jump";

    public static readonly string[] TutorialIds = [UndockStep, DroneStep, GrabStep, SellStep, JumpStep];

    public const int MaxOffers = 8;
    public const int MaxCount = 100;

    /// <summary>Дальше стольких прыжков письма не носят: срок вышел бы раньше, чем пилот долетел (M14).</summary>
    public const int MaxHopsLimit = 6;

    /// <summary>Без заданий и обучения — для тестов и когда файла нет.</summary>
    public static readonly MissionRules None = new(Offers: 0);

    [JsonIgnore] public IReadOnlyList<TutorialStep> Steps => Tutorial ?? [];
    [JsonIgnore] public IReadOnlyList<KillTemplate> KillList => Kill ?? [];
    [JsonIgnore] public IReadOnlyList<CollectTemplate> CollectList => Collect ?? [];
    [JsonIgnore] public IReadOnlyList<DeliverTemplate> DeliverList => Deliver ?? [];
    [JsonIgnore] public IReadOnlyList<EscortTemplate> EscortList => Escort ?? [];
    [JsonIgnore] public IReadOnlyList<PatrolTemplate> PatrolList => Patrol ?? [];
    [JsonIgnore] public IReadOnlyList<CourierTemplate> CourierList => Courier ?? [];
    [JsonIgnore] public IReadOnlyList<HuntTemplate> HuntList => Hunt ?? [];
    [JsonIgnore] public IReadOnlyList<IReadOnlyList<InvasionGroup>> AmbushList => Ambush ?? [];

    /// <summary>Шаблон сопровождения, по которому выдано предложение: из него комната берёт то, чего нет в offer.</summary>
    public EscortTemplate? EscortFor(MissionOffer offer) => EscortList.FirstOrDefault();

    /// <summary>Шаблон патруля: по типу звена, а если такого уже нет в файле — первый попавшийся.</summary>
    public PatrolTemplate? PatrolFor(MissionOffer offer) =>
        PatrolList.FirstOrDefault(t => t.Npc == offer.Npc) ?? PatrolList.FirstOrDefault();

    /// <summary>Группы i-й засады; волн больше, чем списков, — последний повторяется. Пусто — засад нет.</summary>
    public IReadOnlyList<InvasionGroup> Wave(int index) =>
        AmbushList.Count == 0 ? [] : AmbushList[Math.Clamp(index, 0, AmbushList.Count - 1)];

    /// <summary>Шаг обучения по номеру; null — обучение пройдено.</summary>
    public TutorialStep? Step(int index) => index >= 0 && index < Steps.Count ? Steps[index] : null;

    /// <param name="npcs">Типы NPC: пираты для kill и засад, рейнджеры для patrol.</param>
    /// <param name="items">Предметы лута для collect.</param>
    /// <param name="sizes">Размеры метеоритов из meteors.json — для hunt.</param>
    public string? Validate(
        IReadOnlyDictionary<string, NpcType> npcs,
        IReadOnlyDictionary<string, LootItem> items,
        IReadOnlyDictionary<string, MeteorSize> sizes)
    {
        if (Offers is < 0 or > MaxOffers) return $"offers must be within 0..{MaxOffers}";
        if (!(DangerBonus >= 0)) return "dangerBonus must not be negative";
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            var problem = step switch
            {
                null => "is null",
                _ when !TutorialIds.Contains(step.Id) => $"unknown id '{step.Id}': must be one of {string.Join(", ", TutorialIds)}",
                _ when Steps.Take(i).Any(s => s?.Id == step.Id) => $"duplicate id '{step.Id}'",
                _ when string.IsNullOrWhiteSpace(step.Title) => "title is empty",
                _ when step.Reward < 0 => "reward must not be negative",
                _ => null,
            };
            if (problem is not null) return $"tutorial[{i}]: {problem}";
        }
        for (var i = 0; i < KillList.Count; i++)
        {
            var t = KillList[i];
            var problem = t switch
            {
                null => "is null",
                { Npc: { } npc } when !npcs.ContainsKey(npc) => $"unknown npc '{npc}'",
                _ => Range(t.Min, t.Max) ?? Positive(t.Reward, t.Weight),
            };
            if (problem is not null) return $"kill[{i}]: {problem}";
        }
        for (var i = 0; i < CollectList.Count; i++)
        {
            var t = CollectList[i];
            var problem = t switch
            {
                null => "is null",
                _ when t.Item is null || !items.ContainsKey(t.Item) => $"unknown item '{t.Item}'",
                _ when !(t.Factor > 0) => "factor must be positive",
                _ => Range(t.Min, t.Max) ?? Positive(1, t.Weight),
            };
            if (problem is not null) return $"collect[{i}]: {problem}";
        }
        for (var i = 0; i < DeliverList.Count; i++)
        {
            var t = DeliverList[i];
            var problem = t switch
            {
                null => "is null",
                _ when t.PerUnit < 0 || t.PerJump < 0 => "perUnit and perJump must not be negative",
                _ => Range(t.Min, t.Max) ?? Positive(1, t.Weight),
            };
            if (problem is not null) return $"deliver[{i}]: {problem}";
        }
        for (var i = 0; i < EscortList.Count; i++)
        {
            var t = EscortList[i];
            var problem = t switch
            {
                null => "is null",
                _ when !(t.Radius > 0) => "radius must be positive",
                _ when t.AwaySeconds < 1 => "awaySeconds must be at least 1",
                _ when t.PerWave < 0 => "perWave must not be negative",
                _ => Range(t.MinWaves, t.MaxWaves) ?? Positive(t.Reward, t.Weight),
            };
            if (problem is not null) return $"escort[{i}]: {problem}";
        }
        for (var i = 0; i < PatrolList.Count; i++)
        {
            var t = PatrolList[i];
            var problem = t switch
            {
                null => "is null",
                _ when t.Npc is null || !npcs.TryGetValue(t.Npc, out _) => $"unknown npc '{t.Npc}'",
                // Звено патруля — рейнджеры: пират в напарники не годится, они дерутся между собой.
                _ when !npcs[t.Npc].IsRanger => $"'{t.Npc}' is not a ranger",
                _ when t.Wing is < 1 or > NpcSpawn.MaxCount => $"wing must be within 1..{NpcSpawn.MaxCount}",
                _ when t.Rank is < 1 or > NpcSpawn.MaxLevel => $"rank must be within 1..{NpcSpawn.MaxLevel}",
                _ when !(t.Radius > 0) => "radius must be positive",
                _ when t.PerPoint < 0 => "perPoint must not be negative",
                _ => Range(t.Min, t.Max) ?? Positive(t.Reward, t.Weight),
            };
            if (problem is not null) return $"patrol[{i}]: {problem}";
        }
        for (var i = 0; i < CourierList.Count; i++)
        {
            var t = CourierList[i];
            var problem = t switch
            {
                null => "is null",
                _ when t.MaxHops is < 1 or > MaxHopsLimit => $"maxHops must be within 1..{MaxHopsLimit}",
                _ when t.SecondsPerJump < 1 => "secondsPerJump must be at least 1",
                _ when t.Seconds < 0 => "seconds must not be negative",
                _ when t.PerJump < 0 => "perJump must not be negative",
                _ when t.Intercept is < 0 or > 1 => "intercept must be within 0..1",
                _ => Positive(t.Reward, t.Weight),
            };
            if (problem is not null) return $"courier[{i}]: {problem}";
        }
        for (var i = 0; i < HuntList.Count; i++)
        {
            var t = HuntList[i];
            var problem = t switch
            {
                null => "is null",
                { Size: { } size } when !sizes.ContainsKey(size) => $"unknown meteor size '{size}'",
                _ => Range(t.Min, t.Max) ?? Positive(t.Reward, t.Weight),
            };
            if (problem is not null) return $"hunt[{i}]: {problem}";
        }
        for (var i = 0; i < AmbushList.Count; i++)
        {
            var wave = AmbushList[i];
            if (wave is null || wave.Count == 0) return $"ambush[{i}]: is empty";
            for (var j = 0; j < wave.Count; j++)
            {
                if (wave[j] is not { } group) return $"ambush[{i}][{j}]: is null";
                if (group.Validate(npcs) is { } problem) return $"ambush[{i}][{j}]: {problem}";
            }
        }
        return null;
    }

    private static string? Range(int min, int max) =>
        min >= 1 && max >= min && max <= MaxCount ? null : $"min and max must satisfy 1 <= min <= max <= {MaxCount}";

    private static string? Positive(int reward, double weight) =>
        reward < 0 ? "reward must not be negative" : !(weight > 0) ? "weight must be positive" : null;

    public static bool TryParse(
        string json,
        IReadOnlyDictionary<string, NpcType> npcs,
        IReadOnlyDictionary<string, LootItem> items,
        IReadOnlyDictionary<string, MeteorSize> sizes,
        out MissionRules rules,
        out string? error)
    {
        rules = None;
        MissionRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MissionRules>(json, JsonCatalog.Options);
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
        error = parsed.Validate(npcs, items, sizes);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }

    /// <summary>
    /// Доска станции в системе station (GDD §36). Детерминирована по seed: пилот видит одну и ту же доску,
    /// пока не возьмёт или не сдаст задание — тогда сид сменится. Шаблон, которому негде сбыться
    /// (пиратов такого типа рядом нет, другой станции нет), не выпадает.
    /// </summary>
    /// <param name="offers">Сколько предложений; null — <see cref="Offers"/>. Репутация ужимает доску недоверенным (M13).</param>
    /// <param name="elite">
    /// Последнее предложение — особый контракт: та же работа по верхней границе шаблона и за повышенную плату.
    /// Даётся только друзьям станции (M13).
    /// </param>
    /// <param name="eliteReward">Во сколько раз особый контракт дороже обычного.</param>
    /// <param name="repHere">
    /// Очки системы у этого пилота (M14): патруль рейнджеры доверяют не всякому. На остальные виды не влияет —
    /// доску ужимает и удорожает репутация станции, а она приходит отдельными аргументами выше.
    /// </param>
    public IReadOnlyList<MissionOffer> Board(
        Balance balance,
        string station,
        int seed,
        int? offers = null,
        bool elite = false,
        double eliteReward = 1,
        double repHere = 0)
    {
        var galaxy = balance.Galaxy;
        var count = offers ?? Offers;
        if (count <= 0 || galaxy.System(station) is not { Station: true }) return [];

        var candidates = new List<(double Weight, Func<Random, string, MissionOffer> Make)>();
        var near = Near(galaxy, station);
        // Доску просят для названной станции, а не обязательно для той, чей это вид баланса.
        // Рынок с M15 ключуется местом, а доска пока зовётся по системе: здесь это станция системы.
        var market = balance.MarketSet?.Local(PlaceKey.Station(station), galaxy.System(station)?.Region);
        foreach (var t in KillList)
        {
            var targets = near.Where(s => PiratesIn(balance, s).Any(type => t.Npc is null || type == t.Npc)).ToList();
            if (targets.Count == 0) continue;
            candidates.Add((t.Weight, (rng, id) =>
            {
                var system = targets[rng.Next(targets.Count)];
                var count = rng.Next(t.Min, t.Max + 1);
                var danger = galaxy.System(system)?.Danger ?? 1;
                var reward = (int)Math.Round(t.Reward * count * (1 + DangerBonus * (danger - 1)));
                return new MissionOffer(id, KillKind, system, t.Npc, null, count, reward, station);
            }));
        }
        foreach (var t in CollectList)
        {
            // Чем станция торгует сама, того она не просит привезти: иначе задание сдавалось бы
            // покупкой в соседней вкладке, и награда за него превращалась бы в бесплатные кредиты (M12).
            if (market?.Sells(t.Item) == true) continue;
            var price = balance.Loot.Price(t.Item);
            candidates.Add((t.Weight, (rng, id) =>
            {
                var count = rng.Next(t.Min, t.Max + 1);
                var reward = (int)Math.Round(Math.Max(1, price) * count * t.Factor);
                return new MissionOffer(id, CollectKind, null, null, t.Item, count, reward, station);
            }));
        }
        var stations = galaxy.SystemMap.Where(kv => kv.Value.Station && kv.Key != station && Hops(galaxy, station, kv.Key) is not null)
            .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        if (stations.Count > 0)
        {
            foreach (var t in DeliverList)
            {
                candidates.Add((t.Weight, (rng, id) =>
                {
                    var to = stations[rng.Next(stations.Count)];
                    var count = rng.Next(t.Min, t.Max + 1);
                    var reward = t.PerUnit * count + t.PerJump * (Hops(galaxy, station, to) ?? 1);
                    return new MissionOffer(id, DeliverKind, to, null, null, count, reward, station);
                }));
            }
        }

        var home = galaxy.System(station)!;
        // Опасность системы поднимает плату одинаково у всех новых видов: дальше от Sol — дороже работа (M14).
        var bonus = 1 + DangerBonus * (home.Danger - 1);

        // Сопровождение: конвой должен быть кому вести и куда — торговцы в системе и хоть одни врата.
        if (home.Traders is not null && home.GateList.Count > 0)
        {
            foreach (var t in EscortList)
            {
                candidates.Add((t.Weight, (rng, id) =>
                {
                    var gate = home.GateList[rng.Next(home.GateList.Count)];
                    var waves = rng.Next(t.MinWaves, t.MaxWaves + 1);
                    var reward = (int)Math.Round((t.Reward + t.PerWave * waves) * bonus);
                    return new MissionOffer(id, EscortKind, gate.To, null, null, waves, reward, station, Radius: t.Radius);
                }));
            }
        }

        // Патруль: в системе должен быть пост рейнджеров, а у пилота — их доверие.
        var rangers = RangersIn(balance, station).ToList();
        foreach (var t in PatrolList)
        {
            if (!rangers.Contains(t.Npc)) continue;
            if (t.Rep is not null && !balance.Reputation.AtLeast(repHere, t.Rep)) continue;
            candidates.Add((t.Weight, (rng, id) =>
            {
                var points = rng.Next(t.Min, t.Max + 1);
                var reward = (int)Math.Round((t.Reward + t.PerPoint * points) * bonus);
                return new MissionOffer(id, PatrolKind, station, t.Npc, null, points, reward, station, Radius: t.Radius);
            }));
        }

        // Письмо: только туда, куда успеть можно. Награда и срок растут от числа прыжков, а не от опасности:
        // платят за скорость, а не за риск.
        foreach (var t in CourierList)
        {
            var reach = stations.Where(to => Hops(galaxy, station, to) <= t.MaxHops).ToList();
            if (reach.Count == 0) continue;
            candidates.Add((t.Weight, (rng, id) =>
            {
                var to = reach[rng.Next(reach.Count)];
                var hops = Hops(galaxy, station, to) ?? 1;
                return new MissionOffer(
                    id, CourierKind, to, null, null, 1, t.Reward + t.PerJump * hops, station,
                    Seconds: t.Seconds + t.SecondsPerJump * hops);
            }));
        }

        // Охота: система, где камни вообще летают, и нужного размера в ней не ноль.
        foreach (var t in HuntList)
        {
            var fields = near.Where(s => galaxy.System(s) is { } def && HasSize(balance, def, t.Size)).ToList();
            if (fields.Count == 0) continue;
            candidates.Add((t.Weight, (rng, id) =>
            {
                var system = fields[rng.Next(fields.Count)];
                var count = rng.Next(t.Min, t.Max + 1);
                var where = galaxy.System(system)?.Danger ?? 1;
                var reward = (int)Math.Round(t.Reward * count * (1 + DangerBonus * (where - 1)));
                return new MissionOffer(id, HuntKind, system, null, null, count, reward, station, Size: t.Size);
            }));
        }

        if (candidates.Count == 0) return [];

        // Сид смешан с системой: у каждой станции своя доска при том же сиде пилота.
        var rng = new Random(unchecked(seed * 31 + StableHash(station)));
        var total = candidates.Sum(c => c.Weight);
        var board = new List<MissionOffer>(count);
        for (var i = 0; i < count; i++)
        {
            var x = rng.NextDouble() * total;
            var pick = candidates[^1];
            foreach (var c in candidates)
            {
                x -= c.Weight;
                if (x < 0)
                {
                    pick = c;
                    break;
                }
            }
            var offer = pick.Make(rng, $"{seed}-{i}");
            // Особый контракт — последним в списке: он виден как «лучшее, что тут есть», а не теряется в середине.
            if (elite && i == count - 1 && eliteReward > 1)
                offer = offer with { Reward = (int)Math.Round(offer.Reward * eliteReward), Elite = true };
            board.Add(offer);
        }
        return board;
    }

    /// <summary>Сама система и соседние в один прыжок — куда лететь за пиратами недалеко.</summary>
    public static IReadOnlyList<string> Near(GalaxyRules galaxy, string system)
    {
        var list = new List<string> { system };
        foreach (var link in galaxy.LinkList)
        {
            if (link.A == system) list.Add(link.B);
            else if (link.B == system) list.Add(link.A);
        }
        return list.Distinct().Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Какие типы пиратов бывают в системе: налёты и логова. Посты рейнджеров — не пираты.</summary>
    public static IEnumerable<string> PiratesIn(Balance balance, string system)
    {
        var def = balance.Galaxy.System(system);
        var types = balance.Npc.TypeMap;
        bool IsPirate(string type) => !types.TryGetValue(type, out var t) || t.IsPirate;
        if (def is null) return balance.Npc.SpawnList.Select(s => s.Type).Where(IsPirate).Distinct();
        IEnumerable<string> raids = def.Pirates?.GroupList.Select(g => g.Type) ?? [];
        return raids.Concat(def.SpawnList.Select(s => s.Type)).Where(IsPirate).Distinct();
    }

    /// <summary>Типы рейнджеров, чьи посты стоят в системе: с ними и летают в патруль (M14).</summary>
    public static IEnumerable<string> RangersIn(Balance balance, string system)
    {
        var types = balance.Npc.TypeMap;
        var spawns = balance.Galaxy.System(system)?.SpawnList ?? balance.Npc.SpawnList;
        return spawns.Select(s => s.Type).Where(type => types.TryGetValue(type, out var t) && t.IsRanger).Distinct();
    }

    /// <summary>
    /// Летают ли в системе камни такого размера (M14). Свои веса системы главнее общих; размера, которого система
    /// обнулила, там не дождёшься. Общие веса берутся из вида баланса просящей комнаты — для соседей это
    /// приближение, но ошибиться оно может только в пользу пустой доски, а не невыполнимого задания.
    /// </summary>
    private static bool HasSize(Balance balance, SystemDef def, string? size)
    {
        if (!(def.Meteors > 0)) return false;
        if (size is null) return true;
        if (def.MeteorSizes is { } weights && weights.TryGetValue(size, out var own)) return own > 0;
        return balance.Meteors.SizeMap.TryGetValue(size, out var common) && common.Weight > 0;
    }

    /// <summary>Прыжков по кратчайшему пути; null — пути нет.</summary>
    public static int? Hops(GalaxyRules galaxy, string from, string to)
    {
        if (from == to) return 0;
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var frontier = new List<string> { from };
        for (var depth = 1; frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var s in frontier)
            {
                foreach (var link in galaxy.LinkList)
                {
                    var other = link.A == s ? link.B : link.B == s ? link.A : null;
                    if (other is null || !seen.Add(other)) continue;
                    if (other == to) return depth;
                    next.Add(other);
                }
            }
            frontier = next;
        }
        return null;
    }

    /// <summary>string.GetHashCode в .NET случайный от запуска к запуску — доска после перезапуска бы сменилась.</summary>
    private static int StableHash(string s)
    {
        var h = 17;
        foreach (var c in s) h = unchecked(h * 31 + c);
        return h;
    }
}
