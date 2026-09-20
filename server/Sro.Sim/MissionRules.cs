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
/// Задание на доске станции или взятое. Текст собирает клиент — из вида, системы, типа пирата и предмета.
/// </summary>
/// <param name="Id">Уникально в пределах доски: по нему задание и берут.</param>
/// <param name="Kind"><see cref="MissionRules.KillKind"/>, <see cref="MissionRules.CollectKind"/> или <see cref="MissionRules.DeliverKind"/>.</param>
/// <param name="System">Kill — где бить; deliver — куда везти; collect — null, сдать можно на любой станции.</param>
/// <param name="Npc">Kill: тип пирата; null — любой.</param>
/// <param name="Item">Collect: предмет из loot.json.</param>
/// <param name="Count">Сколько уничтожить, собрать или единиц груза.</param>
/// <param name="From">Система станции, где задание выдали.</param>
public sealed record MissionOffer(
    string Id,
    string Kind,
    string? System,
    string? Npc,
    string? Item,
    int Count,
    int Reward,
    string From);

/// <summary>Взятое задание: что и сколько уже сделано. Хранится в аккаунте пилота.</summary>
/// <param name="Progress">Kill — сколько уничтожено; у collect и deliver не растёт: их сдают на станции целиком.</param>
public sealed record ActiveMission(MissionOffer Offer, int Progress = 0);

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
    IReadOnlyList<DeliverTemplate>? Deliver = null)
{
    public const string File = "missions.json";

    public const string KillKind = "kill";
    public const string CollectKind = "collect";
    public const string DeliverKind = "deliver";

    /// <summary>Шаги обучения: вылететь, уничтожить дрон, подобрать груз, продать, прыгнуть через врата.</summary>
    public const string UndockStep = "undock";
    public const string DroneStep = "drone";
    public const string GrabStep = "grab";
    public const string SellStep = "sell";
    public const string JumpStep = "jump";

    public static readonly string[] TutorialIds = [UndockStep, DroneStep, GrabStep, SellStep, JumpStep];

    public const int MaxOffers = 8;
    public const int MaxCount = 100;

    /// <summary>Без заданий и обучения — для тестов и когда файла нет.</summary>
    public static readonly MissionRules None = new(Offers: 0);

    [JsonIgnore] public IReadOnlyList<TutorialStep> Steps => Tutorial ?? [];
    [JsonIgnore] public IReadOnlyList<KillTemplate> KillList => Kill ?? [];
    [JsonIgnore] public IReadOnlyList<CollectTemplate> CollectList => Collect ?? [];
    [JsonIgnore] public IReadOnlyList<DeliverTemplate> DeliverList => Deliver ?? [];

    /// <summary>Шаг обучения по номеру; null — обучение пройдено.</summary>
    public TutorialStep? Step(int index) => index >= 0 && index < Steps.Count ? Steps[index] : null;

    /// <param name="npcs">Типы пиратов для kill.</param>
    /// <param name="items">Предметы лута для collect.</param>
    public string? Validate(IReadOnlyDictionary<string, NpcType> npcs, IReadOnlyDictionary<string, LootItem> items)
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
        error = parsed.Validate(npcs, items);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }

    /// <summary>
    /// Доска станции в системе station (GDD §36). Детерминирована по seed: пилот видит одну и ту же доску,
    /// пока не возьмёт или не сдаст задание — тогда сид сменится. Шаблон, которому негде сбыться
    /// (пиратов такого типа рядом нет, другой станции нет), не выпадает.
    /// </summary>
    public IReadOnlyList<MissionOffer> Board(Balance balance, string station, int seed)
    {
        var galaxy = balance.Galaxy;
        if (Offers <= 0 || galaxy.System(station) is not { Station: true }) return [];

        var candidates = new List<(double Weight, Func<Random, string, MissionOffer> Make)>();
        var near = Near(galaxy, station);
        // Доску просят для названной станции, а не обязательно для той, чей это вид баланса.
        var market = balance.MarketSet?.Local(station, galaxy.System(station)?.Region);
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
        if (candidates.Count == 0) return [];

        // Сид смешан с системой: у каждой станции своя доска при том же сиде пилота.
        var rng = new Random(unchecked(seed * 31 + StableHash(station)));
        var total = candidates.Sum(c => c.Weight);
        var board = new List<MissionOffer>(Offers);
        for (var i = 0; i < Offers; i++)
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
            board.Add(pick.Make(rng, $"{seed}-{i}"));
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
