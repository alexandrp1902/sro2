using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>
/// Станция и экономика из shared/shop.json (GDD §26, §30, §50, §54): стартовые кредиты, цены корпусов и пушек,
/// цена ремонта. Стартовые корпус и пушка (<see cref="SimConfig.DefaultHull"/>, <see cref="SimConfig.DefaultWeapon"/>)
/// есть у каждого пилота бесплатно, что бы ни стояло в прайсе.
/// </summary>
/// <param name="StartCredits">Столько кредитов у нового пилота (GDD §54 — 1 000).</param>
/// <param name="RepairPrice">Кредитов за единицу прочности корпуса при ремонте в доке; 0 — бесплатно.</param>
/// <param name="Hulls">Корпус — цена. Корпуса, которого здесь нет, на станции не продают.</param>
/// <param name="Weapons">Пушка — цена. Пушки, которой здесь нет, на станции не продают.</param>
public sealed record ShopRules(
    int StartCredits = 1000,
    double RepairPrice = 0,
    IReadOnlyDictionary<string, int>? Hulls = null,
    IReadOnlyDictionary<string, int>? Weapons = null)
{
    public const string File = "shop.json";

    /// <summary>Без магазина: ничего не продают и кредитов на старте нет — для тестов и когда файла нет.</summary>
    public static readonly ShopRules None = new(StartCredits: 0);

    [JsonIgnore] public IReadOnlyDictionary<string, int> HullPrices => Hulls ?? new Dictionary<string, int>();
    [JsonIgnore] public IReadOnlyDictionary<string, int> WeaponPrices => Weapons ?? new Dictionary<string, int>();

    /// <returns>Цена корпуса; null — не продаётся.</returns>
    public int? HullPrice(string id) => HullPrices.TryGetValue(id, out var price) ? price : null;

    /// <returns>Цена пушки; null — не продаётся.</returns>
    public int? WeaponPrice(string id) => WeaponPrices.TryGetValue(id, out var price) ? price : null;

    /// <summary>Сколько стоит довести корпус до полной прочности; округляется вверх.</summary>
    public int RepairCost(double missingHp) => missingHp > 0 ? (int)Math.Ceiling(missingHp * RepairPrice) : 0;

    /// <param name="hulls">Каждый корпус в прайсе должен быть в hulls.json.</param>
    /// <param name="weapons">Каждая пушка в прайсе должна быть в weapons.json.</param>
    public string? Validate(IReadOnlyDictionary<string, HullParams> hulls, IReadOnlyDictionary<string, WeaponParams> weapons)
    {
        if (StartCredits < 0) return "startCredits must not be negative";
        if (!(RepairPrice >= 0)) return "repairPrice must not be negative";
        foreach (var (id, price) in HullPrices)
        {
            if (!hulls.ContainsKey(id)) return $"hulls.{id}: unknown hull";
            if (price < 0) return $"hulls.{id}: price must not be negative";
        }
        foreach (var (id, price) in WeaponPrices)
        {
            if (!weapons.ContainsKey(id)) return $"weapons.{id}: unknown weapon";
            if (price < 0) return $"weapons.{id}: price must not be negative";
        }
        return null;
    }

    public static bool TryParse(
        string json,
        IReadOnlyDictionary<string, HullParams> hulls,
        IReadOnlyDictionary<string, WeaponParams> weapons,
        out ShopRules rules,
        out string? error)
    {
        rules = None;
        ShopRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ShopRules>(json, JsonCatalog.Options);
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
        error = parsed.Validate(hulls, weapons);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
