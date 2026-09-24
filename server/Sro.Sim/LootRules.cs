using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Предмет, который можно поднять в космосе (GDD §21–22): ресурс, деталь или компонент.</summary>
/// <param name="Rarity">Редкость (GDD §23) — от неё цвет на экране.</param>
/// <param name="Volume">Сколько места занимает одна штука в трюме.</param>
/// <param name="Price">Сколько кредитов дают за штуку при сдаче на станции.</param>
/// <param name="Story">
/// Сюжетный предмет (M20a): место в трюме занимает, но не продаётся, не покупается, не выбрасывается,
/// не меняется между игроками и остаётся у пилота после гибели. Иначе цепочка миссий рвалась бы
/// на первом же респауне, а «продать всё» одним нажатием стирало бы улику из шестой миссии.
/// </param>
public sealed record LootItem(
    string Name,
    string Rarity = LootItem.Common,
    double Volume = 1,
    int Price = 0,
    bool Story = false)
{
    public const string Common = "common";

    /// <summary>Порядок — от обычного к легендарному (GDD §23).</summary>
    public static readonly string[] Rarities = [Common, "uncommon", "rare", "epic", "legendary"];

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (Rarity is null || Array.IndexOf(Rarities, Rarity) < 0)
            return $"unknown rarity '{Rarity}', expected one of {string.Join(", ", Rarities)}";
        if (!(Volume > 0)) return "volume must be positive";
        if (Price < 0) return "price must not be negative";
        return null;
    }
}

/// <summary>
/// Одна строка таблицы дропа: с шансом Chance выпадает от Min до Max штук предмета. Предмет — только груз
/// из items: снаряжение роняет <see cref="LootTable.Fit"/>, а не строка дропа.
/// </summary>
public sealed record LootRoll(string Item, double Chance = 1, int Min = 1, int Max = 1)
{
    public const int MaxCount = 100;

    public string? Validate(IReadOnlyDictionary<string, LootItem> items, IReadOnlyDictionary<string, double>? gear = null)
    {
        if (Item is null) return "item is empty";

        // Снаряжению здесь не место: строку дропа разгоняют прибавки за уровень (LevelChanceBonus,
        // LevelCountBonus), и 2 % однажды снова стали бы 47 %, а одна пушка — тремя копиями.
        if (!items.ContainsKey(Item))
            return gear?.ContainsKey(Item) == true
                ? $"'{Item}' is gear: list it in fit, not in rolls"
                : $"unknown item '{Item}'";
        if (!(Chance > 0 && Chance <= 1)) return "chance must be within 0..1";
        if (Min < 1) return "min must be at least 1";
        if (Max < Min) return "max must not be less than min";
        if (Max > MaxCount) return $"max must not exceed {MaxCount}";
        return null;
    }
}

/// <summary>Что роняет NPC такого типа. Имя таблицы — ключ типа из npcs.json; таблицы нет — дропа нет.</summary>
/// <param name="LevelChanceBonus">Прибавка к шансу за каждый уровень выше первого, долей. Снаряжения не касается.</param>
/// <param name="LevelCountBonus">Прибавка к количеству за каждый уровень выше первого, долей. Снаряжения не касается.</param>
/// <param name="Fit">
/// Снаряжение, которое стоит на этом корабле (M11): 4–6 пушек и модулей. На бой оно не влияет — сила NPC
/// задана в npcs.json, — но с обломков может выпасть только то, что есть здесь, а не любой предмет каталога.
/// Тир снаряжения — региональный: таблицу подменяет система (<see cref="SystemDef.LootTables"/>).
/// </param>
/// <param name="GearChance">Свой шанс для каждой единицы снаряжения; null — общий из loot.json.</param>
public sealed record LootTable(
    IReadOnlyList<LootRoll>? Rolls = null,
    double LevelChanceBonus = 0,
    double LevelCountBonus = 0,
    IReadOnlyList<string>? Fit = null,
    double? GearChance = null)
{
    /// <summary>Больше этого на корабль не вешают: иначе «один из его модулей» перестаёт быть находкой.</summary>
    public const int MaxFit = 8;

    [JsonIgnore] public IReadOnlyList<LootRoll> RollList => Rolls ?? [];
    [JsonIgnore] public IReadOnlyList<string> FitList => Fit ?? [];

    /// <summary>Что выпало с NPC такого уровня. rng — из потока тика, чтобы дроп был воспроизводим по сиду.</summary>
    public void Roll(int level, Func<double> rng, List<(string Item, int Count)> into)
    {
        var chanceBonus = LevelChanceBonus * (level - 1);
        var countFactor = 1 + LevelCountBonus * (level - 1);
        foreach (var roll in RollList)
        {
            if (roll is null) continue;
            if (!(rng() < Math.Min(1, roll.Chance + chanceBonus))) continue;

            // rng() из [0, 1), но страхуемся от единицы: выйти за Max нельзя.
            var count = Math.Min(roll.Max, roll.Min + (int)(rng() * (roll.Max - roll.Min + 1)));
            count = (int)Math.Round(count * countFactor, MidpointRounding.AwayFromZero);
            if (count > 0) into.Add((roll.Item, count));
        }
    }

    /// <summary>
    /// Снаряжение с обломков: каждая единица из <see cref="Fit"/> бросает свою монету отдельно и всегда
    /// по одной штуке. Может не выпасть ничего, может выпасть сразу несколько. Уровень NPC здесь намеренно
    /// ни при чём: прибавки за уровень множатся, и матёрый пират раздевался бы каждый бой.
    /// </summary>
    /// <param name="chance">Общий шанс из loot.json; своя <see cref="GearChance"/> его перебивает.</param>
    public void RollFit(double chance, Func<double> rng, List<(string Item, int Count)> into)
    {
        if (FitList.Count == 0) return;
        var each = Math.Min(1, GearChance ?? chance);
        foreach (var item in FitList)
        {
            if (item is null) continue;
            if (rng() < each) into.Add((item, 1));
        }
    }

    /// <param name="gear">Снаряжение, которое бывает в космосе: id — объём в трюме.</param>
    public string? Validate(IReadOnlyDictionary<string, LootItem> items, IReadOnlyDictionary<string, double>? gear = null)
    {
        if (!(LevelChanceBonus >= 0) || !(LevelCountBonus >= 0)) return "level bonuses must not be negative";
        if (GearChance is { } chance && !(chance >= 0 && chance <= 1)) return "gearChance must be within 0..1";
        for (var i = 0; i < RollList.Count; i++)
        {
            var problem = RollList[i] is null ? "is null" : RollList[i].Validate(items, gear);
            if (problem is not null) return $"rolls[{i}]: {problem}";
        }
        if (FitList.Count > MaxFit) return $"fit must not exceed {MaxFit} items";
        for (var i = 0; i < FitList.Count; i++)
        {
            var item = FitList[i];
            if (item is not null && items.ContainsKey(item)) return $"fit[{i}]: '{item}' is cargo, not gear";
            if (item is null || gear?.ContainsKey(item) != true) return $"fit[{i}]: unknown gear '{item}'";
        }
        return null;
    }
}

/// <summary>
/// Точка, где может лежать контейнер (GDD §21). Точек больше, чем контейнеров: каждая время от времени
/// бросает свою монету, поэтому одни и те же места пустуют и наполняются по-разному, а не по расписанию.
/// Для ядра контейнер — обычный дроп, просто бессрочный и без дрейфа.
/// </summary>
/// <param name="Item">Фиксированное содержимое; задаётся либо оно, либо Table.</param>
/// <param name="Table">Таблица из tables: содержимое разное при каждом появлении.</param>
/// <param name="RespawnSeconds">Среднее время до следующей попытки; 0 — точка одноразовая.</param>
/// <param name="Chance">Вероятность, что попытка удастся: 0.2 — место редкое, 1 — почти всегда занято.</param>
public sealed record LootContainer(
    string Name,
    double X,
    double Y,
    string? Item = null,
    int Count = 1,
    string? Table = null,
    double RespawnSeconds = 120,
    double Chance = 1)
{
    [JsonIgnore] public int RespawnTicks => RespawnSeconds > 0 ? Math.Max(1, Combat.SecondsToTicks(RespawnSeconds)) : 0;

    /// <param name="stationSafeRadius">Укрытие у станции: там контейнер был бы бесплатным лутом без риска.</param>
    /// <param name="stationOrbit">Радиус орбиты станции: укрытие проходит по всему этому кругу; 0 — станция в центре.</param>
    public string? Validate(
        IReadOnlyDictionary<string, LootItem> items,
        IReadOnlyDictionary<string, LootTable> tables,
        double stationSafeRadius,
        double stationOrbit = 0)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (Item is null == (Table is null)) return "exactly one of item and table must be set";
        if (Item is not null && !items.ContainsKey(Item)) return $"unknown item '{Item}'";
        if (Table is not null && !tables.ContainsKey(Table)) return $"unknown table '{Table}'";
        if (Count < 1 || Count > LootRoll.MaxCount) return $"count must be within 1..{LootRoll.MaxCount}";
        if (!(RespawnSeconds >= 0)) return "respawnSeconds must not be negative";
        if (!(Chance > 0 && Chance <= 1)) return "chance must be within 0..1";
        if (!(Math.Abs(X) <= NpcRules.WorldLimit) || !(Math.Abs(Y) <= NpcRules.WorldLimit))
            return $"x and y must be within ±{NpcRules.WorldLimit}";

        var toStation = Math.Abs(Math.Sqrt(Sq(X) + Sq(Y)) - stationOrbit);
        if (toStation < stationSafeRadius)
            return $"too close to the station orbit: must be at least {stationSafeRadius} away (inside the shelter loot would be free)";
        return null;
    }

    private static double Sq(double v) => v * v;
}

/// <summary>
/// Лут и трюм из shared/loot.json (GDD §21–23): что роняют NPC, сколько предмет живёт в космосе,
/// с какой дистанции его забирает тракторный луч и почём его принимают на станции.
/// Ёмкость трюма живёт не здесь, а в hulls.json: она принадлежит корпусу (боевой документ §46).
/// </summary>
/// <param name="PickupRange">Ближе этого предмет сам идёт в трюм (GDD §21 — 100–150).</param>
/// <param name="LifetimeSeconds">Столько предмет лежит в космосе, потом исчезает.</param>
/// <param name="FadeSeconds">Последние секунды жизни предмет мигает.</param>
/// <param name="MaxItems">Больше этого предметов в системе не держим — предохранитель от засорения.</param>
/// <param name="DropRadius">Разброс точек появления вокруг обломков: иначе стопка лежит в одной точке.</param>
/// <param name="DriftFactor">Какую долю скорости убитого наследует предмет.</param>
/// <param name="DriftDampTime">За это время дрейф гаснет примерно до 5%.</param>
/// <param name="FullHoldSeconds">Не чаще раза в столько секунд игроку говорят, что трюм полон.</param>
/// <param name="StationUnload">Выключатель сдачи груза на станции.</param>
/// <param name="StationRange">Радиус станции: ближе этого корабль может пристыковаться (M6), в доке продают груз.</param>
/// <param name="MaxContainers">Сколько контейнеров лежит в системе одновременно; 0 — сколько угодно.</param>
/// <param name="GearChance">
/// Шанс, что отдельная единица снаряжения из fit таблицы уцелеет в обломках (M11). Монета у каждой своя,
/// уровень NPC на неё не влияет: это находка, а не награда за уровень. 0 — снаряжение не падает вовсе.
/// </param>
public sealed record LootRules(
    double PickupRange = 130,
    double LifetimeSeconds = 120,
    double FadeSeconds = 10,
    int MaxItems = 200,
    double DropRadius = 40,
    double DriftFactor = 0.35,
    double DriftDampTime = 4,
    double FullHoldSeconds = 5,
    bool StationUnload = true,
    double StationRange = 200,
    int MaxContainers = 0,
    double GearChance = 0.03,
    IReadOnlyDictionary<string, LootItem>? Items = null,
    IReadOnlyDictionary<string, LootTable>? Tables = null,
    IReadOnlyList<LootContainer>? Containers = null)
{
    public const string File = "loot.json";

    /// <summary>Без лута: для тестов и когда файла нет.</summary>
    public static readonly LootRules None = new();

    [JsonIgnore] public int LifetimeTicks => Math.Max(1, Combat.SecondsToTicks(LifetimeSeconds));
    [JsonIgnore] public int FadeTicks => Combat.SecondsToTicks(FadeSeconds);
    [JsonIgnore] public int FullHoldTicks => Combat.SecondsToTicks(FullHoldSeconds);
    [JsonIgnore] public IReadOnlyDictionary<string, LootItem> ItemMap => Items ?? new Dictionary<string, LootItem>();
    [JsonIgnore] public IReadOnlyDictionary<string, LootTable> TableMap => Tables ?? new Dictionary<string, LootTable>();
    [JsonIgnore] public IReadOnlyList<LootContainer> ContainerList => Containers ?? [];

    /// <summary>
    /// Пушки и модули всех тиров: они тоже выпадают (M11). Значение — объём в трюме: трофей летит домой
    /// в грузовом отсеке и на склад попадает только в доке, поэтому за место он спорит с грузом.
    /// </summary>
    [JsonIgnore] public IReadOnlyDictionary<string, double> Gear { get; init; } = new Dictionary<string, double>();

    /// <summary>Место под пушку или модуль класса S, M, L: 2, 4, 6 — крупное возить дороже.</summary>
    public static double GearVolume(string? equipClass) => EquipClass.Rank(equipClass) * 2;

    /// <summary>Предмет — снаряжение, а не груз.</summary>
    public bool IsGear(string item) => !ItemMap.ContainsKey(item) && Gear.ContainsKey(item);

    /// <summary>Такой предмет может лежать в космосе: груз или снаряжение.</summary>
    public bool Knows(string item) => ItemMap.ContainsKey(item) || Gear.ContainsKey(item);

    /// <returns>Объём одной штуки; 0 — предмета такого нет.</returns>
    public double Volume(string item) =>
        ItemMap.TryGetValue(item, out var found) ? found.Volume : Gear.GetValueOrDefault(item);

    /// <returns>Цена одной штуки в кредитах; 0 — предмета такого нет.</returns>
    public int Price(string item) => ItemMap.TryGetValue(item, out var found) ? found.Price : 0;

    /// <summary>
    /// Сюжетный предмет (M20a): его не продают, не выбрасывают, не меняют и не теряют вместе с кораблём.
    /// Один вопрос — один ответ: все запреты смотрят сюда, а не перечисляют предметы поимённо.
    /// </summary>
    public bool IsStory(string item) => ItemMap.TryGetValue(item, out var found) && found.Story;

    /// <param name="stationSafeRadius">Из npcs.json: ближе этого к станции контейнеры ставить нельзя.</param>
    /// <param name="stationOrbit">Радиус орбиты станции; 0 — станция в центре.</param>
    public string? Validate(double stationSafeRadius = 0, double stationOrbit = 0)
    {
        if (!(PickupRange > 0)) return "pickupRange must be positive";
        if (!(LifetimeSeconds > 0)) return "lifetimeSeconds must be positive";
        if (!(FadeSeconds >= 0) || FadeSeconds > LifetimeSeconds) return "fadeSeconds must be within 0..lifetimeSeconds";
        if (MaxItems < 1) return "maxItems must be at least 1";
        if (!(DropRadius >= 0)) return "dropRadius must not be negative";
        if (!(DriftFactor >= 0 && DriftFactor <= 1)) return "driftFactor must be within 0..1";
        if (!(DriftDampTime > 0)) return "driftDampTime must be positive";
        if (!(FullHoldSeconds >= 0)) return "fullHoldSeconds must not be negative";
        if (!(StationRange > 0)) return "stationRange must be positive";
        if (MaxContainers < 0) return "maxContainers must not be negative";
        if (!(GearChance >= 0 && GearChance <= 1)) return "gearChance must be within 0..1";

        foreach (var (id, item) in ItemMap)
        {
            var problem = item is null ? "is null" : item.Validate();
            if (problem is not null) return $"items.{id}: {problem}";
        }
        foreach (var (id, table) in TableMap)
        {
            var problem = table is null ? "is null" : table.Validate(ItemMap, Gear);
            if (problem is not null) return $"tables.{id}: {problem}";
        }
        for (var i = 0; i < ContainerList.Count; i++)
        {
            var container = ContainerList[i];
            var problem = container is null ? "is null" : container.Validate(ItemMap, TableMap, stationSafeRadius, stationOrbit);
            if (problem is not null) return $"containers[{i}]: {problem}";
        }
        return null;
    }

    /// <param name="gear">Пушки и модули, которые могут выпадать (id — объём); null — только груз.</param>
    public static bool TryParse(
        string json,
        out LootRules rules,
        out string? error,
        double stationSafeRadius = 0,
        IReadOnlyDictionary<string, double>? gear = null)
    {
        rules = None;
        LootRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LootRules>(json, JsonCatalog.Options);
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
        if (gear is not null) parsed = parsed with { Gear = gear };
        error = parsed.Validate(stationSafeRadius);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
