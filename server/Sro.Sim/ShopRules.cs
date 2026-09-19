using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>
/// Станция и экономика из shared/shop.json (GDD §26, §30, §50, §54): стартовые кредиты, цены корпусов, пушек и модулей,
/// цена ремонта. Стартовый корпус (<see cref="SimConfig.DefaultHull"/>) и стартовое оснащение (<see cref="Fitting.Starter"/>)
/// есть у каждого пилота бесплатно, что бы ни стояло в прайсе.
/// </summary>
/// <param name="StartCredits">Столько кредитов у нового пилота (GDD §54 — 1 000).</param>
/// <param name="RepairPrice">Кредитов за единицу прочности корпуса при ремонте в доке; 0 — бесплатно.</param>
/// <param name="Hulls">Корпус — цена. Корпуса, которого здесь нет, на станции не продают.</param>
/// <param name="Items">Пушка или модуль — цена. Чего здесь нет, того на станции не продают.</param>
/// <param name="FuelPrice">Кредитов за единицу топлива при заправке в доке (GDD §6, §26); 0 — бесплатно.</param>
/// <param name="SellShare">Доля цены, за которую станция выкупает пушку или модуль со склада.</param>
public sealed record ShopRules(
    int StartCredits = 1000,
    double RepairPrice = 0,
    IReadOnlyDictionary<string, int>? Hulls = null,
    IReadOnlyDictionary<string, int>? Items = null,
    double FuelPrice = 0,
    double SellShare = 0.5)
{
    public const string File = "shop.json";

    /// <summary>Без магазина: ничего не продают и кредитов на старте нет — для тестов и когда файла нет.</summary>
    public static readonly ShopRules None = new(StartCredits: 0);

    [JsonIgnore] public IReadOnlyDictionary<string, int> HullPrices => Hulls ?? new Dictionary<string, int>();
    [JsonIgnore] public IReadOnlyDictionary<string, int> ItemPrices => Items ?? new Dictionary<string, int>();

    /// <returns>Цена корпуса; null — не продаётся.</returns>
    public int? HullPrice(string id) => HullPrices.TryGetValue(id, out var price) ? price : null;

    /// <returns>Цена пушки или модуля; null — не продаётся.</returns>
    public int? ItemPrice(string id) => ItemPrices.TryGetValue(id, out var price) ? price : null;

    /// <summary>Сколько станция даёт за пушку или модуль со склада; то, чего она не продаёт, — даром.</summary>
    public int SellPrice(string id) => ItemPrice(id) is { } price ? (int)Math.Floor(price * SellShare + 1e-9) : 0;

    /// <summary>Сколько стоит довести корпус до полной прочности; округляется вверх.</summary>
    public int RepairCost(double missingHp) => missingHp > 0 ? (int)Math.Ceiling(missingHp * RepairPrice) : 0;

    /// <summary>Сколько стоит залить столько топлива; округляется вверх.</summary>
    public int FuelCost(double missingFuel) => missingFuel > 0 ? (int)Math.Ceiling(missingFuel * FuelPrice - 1e-9) : 0;

    /// <param name="hulls">Каждый корпус в прайсе должен быть в hulls.json.</param>
    /// <param name="weapons">Каждый предмет в прайсе должен быть в weapons.json…</param>
    /// <param name="modules">…или в modules.json.</param>
    public string? Validate(
        IReadOnlyDictionary<string, HullParams> hulls,
        IReadOnlyDictionary<string, WeaponParams> weapons,
        IReadOnlyDictionary<string, ModuleParams>? modules = null)
    {
        if (StartCredits < 0) return "startCredits must not be negative";
        if (!(RepairPrice >= 0)) return "repairPrice must not be negative";
        if (!(FuelPrice >= 0)) return "fuelPrice must not be negative";
        if (!(SellShare >= 0 && SellShare <= 1)) return "sellShare must be within 0..1";
        foreach (var (id, price) in HullPrices)
        {
            if (!hulls.ContainsKey(id)) return $"hulls.{id}: unknown hull";
            if (price < 0) return $"hulls.{id}: price must not be negative";
        }
        foreach (var (id, price) in ItemPrices)
        {
            if (!weapons.ContainsKey(id) && modules?.ContainsKey(id) != true) return $"items.{id}: unknown weapon or module";
            if (price < 0) return $"items.{id}: price must not be negative";
        }
        return null;
    }

    public static bool TryParse(
        string json,
        IReadOnlyDictionary<string, HullParams> hulls,
        IReadOnlyDictionary<string, WeaponParams> weapons,
        IReadOnlyDictionary<string, ModuleParams>? modules,
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
        error = parsed.Validate(hulls, weapons, modules);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
