using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Предмет, который можно поднять в космосе (GDD §21–22): ресурс, деталь или компонент.</summary>
/// <param name="Rarity">Редкость (GDD §23) — от неё цвет на экране.</param>
/// <param name="Volume">Сколько места занимает одна штука в трюме.</param>
/// <param name="Price">Сколько кредитов дают за штуку при сдаче на станции.</param>
public sealed record LootItem(string Name, string Rarity = LootItem.Common, double Volume = 1, int Price = 0)
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

/// <summary>Одна строка таблицы дропа: с шансом Chance выпадает от Min до Max штук предмета.</summary>
public sealed record LootRoll(string Item, double Chance = 1, int Min = 1, int Max = 1)
{
    public const int MaxCount = 100;

    public string? Validate(IReadOnlyDictionary<string, LootItem> items)
    {
        if (Item is null || !items.ContainsKey(Item)) return $"unknown item '{Item}'";
        if (!(Chance > 0 && Chance <= 1)) return "chance must be within 0..1";
        if (Min < 1) return "min must be at least 1";
        if (Max < Min) return "max must not be less than min";
        if (Max > MaxCount) return $"max must not exceed {MaxCount}";
        return null;
    }
}

/// <summary>Что роняет NPC такого типа. Имя таблицы — ключ типа из npcs.json; таблицы нет — дропа нет.</summary>
/// <param name="LevelChanceBonus">Прибавка к шансу за каждый уровень выше первого, долей.</param>
/// <param name="LevelCountBonus">Прибавка к количеству за каждый уровень выше первого, долей.</param>
public sealed record LootTable(
    IReadOnlyList<LootRoll>? Rolls = null,
    double LevelChanceBonus = 0,
    double LevelCountBonus = 0)
{
    [JsonIgnore] public IReadOnlyList<LootRoll> RollList => Rolls ?? [];

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

    public string? Validate(IReadOnlyDictionary<string, LootItem> items)
    {
        if (!(LevelChanceBonus >= 0) || !(LevelCountBonus >= 0)) return "level bonuses must not be negative";
        for (var i = 0; i < RollList.Count; i++)
        {
            var problem = RollList[i] is null ? "is null" : RollList[i].Validate(items);
            if (problem is not null) return $"rolls[{i}]: {problem}";
        }
        return null;
    }
}

/// <summary>
/// Контейнер (GDD §21): предмет, который ждёт на месте и появляется снова через RespawnSeconds.
/// Для ядра это обычный дроп — просто бессрочный и без дрейфа.
/// </summary>
/// <param name="Item">Фиксированное содержимое; задаётся либо оно, либо Table.</param>
/// <param name="Table">Таблица из tables: содержимое разное при каждом появлении.</param>
/// <param name="RespawnSeconds">0 — контейнер одноразовый и больше не появится.</param>
public sealed record LootContainer(
    string Name,
    double X,
    double Y,
    string? Item = null,
    int Count = 1,
    string? Table = null,
    double RespawnSeconds = 120)
{
    [JsonIgnore] public int RespawnTicks => RespawnSeconds > 0 ? Math.Max(1, Combat.SecondsToTicks(RespawnSeconds)) : 0;

    /// <param name="stationSafeRadius">Укрытие у станции: там контейнер был бы бесплатным лутом без риска.</param>
    public string? Validate(
        IReadOnlyDictionary<string, LootItem> items,
        IReadOnlyDictionary<string, LootTable> tables,
        double stationSafeRadius)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (Item is null == (Table is null)) return "exactly one of item and table must be set";
        if (Item is not null && !items.ContainsKey(Item)) return $"unknown item '{Item}'";
        if (Table is not null && !tables.ContainsKey(Table)) return $"unknown table '{Table}'";
        if (Count < 1 || Count > LootRoll.MaxCount) return $"count must be within 1..{LootRoll.MaxCount}";
        if (!(RespawnSeconds >= 0)) return "respawnSeconds must not be negative";
        if (!(Math.Abs(X) <= NpcRules.WorldLimit) || !(Math.Abs(Y) <= NpcRules.WorldLimit))
            return $"x and y must be within ±{NpcRules.WorldLimit}";

        var toStation = Math.Sqrt(Sq(X - SimConfig.StationX) + Sq(Y - SimConfig.StationY));
        if (toStation < stationSafeRadius)
            return $"too close to the station: must be at least {stationSafeRadius} away (inside the shelter loot would be free)";
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
/// <param name="StationRange">Ближе этого к станции груз превращается в кредиты.</param>
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

    /// <returns>Объём одной штуки; 0 — предмета такого нет.</returns>
    public double Volume(string item) => ItemMap.TryGetValue(item, out var found) ? found.Volume : 0;

    /// <returns>Цена одной штуки в кредитах; 0 — предмета такого нет.</returns>
    public int Price(string item) => ItemMap.TryGetValue(item, out var found) ? found.Price : 0;

    /// <param name="stationSafeRadius">Из npcs.json: ближе этого к станции контейнеры ставить нельзя.</param>
    public string? Validate(double stationSafeRadius = 0)
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

        foreach (var (id, item) in ItemMap)
        {
            var problem = item is null ? "is null" : item.Validate();
            if (problem is not null) return $"items.{id}: {problem}";
        }
        foreach (var (id, table) in TableMap)
        {
            var problem = table is null ? "is null" : table.Validate(ItemMap);
            if (problem is not null) return $"tables.{id}: {problem}";
        }
        for (var i = 0; i < ContainerList.Count; i++)
        {
            var container = ContainerList[i];
            var problem = container is null ? "is null" : container.Validate(ItemMap, TableMap, stationSafeRadius);
            if (problem is not null) return $"containers[{i}]: {problem}";
        }
        return null;
    }

    public static bool TryParse(string json, out LootRules rules, out string? error, double stationSafeRadius = 0)
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
        error = parsed.Validate(stationSafeRadius);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
