using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>
/// Ассортимент (M11): что продают в регионе или в отдельном месте — на станции, а с M15 и в поселении.
/// У места списки — добавка к региону, <paramref name="Remove"/> убирает из него (все тиры), тиры и цена — свои.
/// </summary>
/// <param name="Hulls">Корпуса в продаже.</param>
/// <param name="Items">Пушки и модули в продаже — базовые id (Mk1); тиры добавляет <paramref name="Tiers"/>.</param>
/// <param name="Tiers">Какие тиры продают: [1] — только Mk1, [2, 3] — Mk2 и Mk3. У станции null — как в регионе.</param>
/// <param name="Remove">Место: чего из регионального ассортимента здесь нет.</param>
/// <param name="Price">Место: множитель всех цен (0.9 — на 10 % дешевле).</param>
/// <param name="Title">Место: подпись магазина в доке — «Военная верфь», «Шахтёрский склад».</param>
public sealed record StockDef(
    IReadOnlyList<string>? Hulls = null,
    IReadOnlyList<string>? Items = null,
    IReadOnlyList<int>? Tiers = null,
    IReadOnlyList<string>? Remove = null,
    double Price = 1,
    string? Title = null)
{
    [JsonIgnore] public IReadOnlyList<string> HullList => Hulls ?? [];
    [JsonIgnore] public IReadOnlyList<string> ItemList => Items ?? [];
    [JsonIgnore] public IReadOnlyList<string> RemoveList => Remove ?? [];
}

/// <summary>
/// Тариф буксира (M15.6): во сколько обходится перегон своего корпуса из другого дока сюда.
/// Ни множитель цен места, ни репутация к нему не применяются — тариф галактический, и он не должен
/// зависеть от того, на каком конце маршрута стоит заказчик.
/// </summary>
/// <param name="Base">Плата за вызов: столько стоит перегон в пределах одной системы.</param>
/// <param name="PerJump">За каждый прыжок маршрута.</param>
/// <param name="HullShare">Доля цены корпуса за каждый прыжок: тащить «Титана» дороже, чем «Пчелу».</param>
public sealed record TransportDef(int Base = 400, int PerJump = 800, double HullShare = 0.05)
{
    public string? Validate() =>
        Base >= 0 && PerJump >= 0 && HullShare >= 0 ? null : "base, perJump and hullShare must not be negative";
}

/// <summary>
/// Станция и экономика из shared/shop.json (GDD §26, §30, §50, §54): стартовые кредиты, цены корпусов, пушек и модулей,
/// цена ремонта. Стартовый корпус (<see cref="SimConfig.DefaultHull"/>) и стартовое оснащение (<see cref="Fitting.Starter"/>)
/// есть у каждого пилота бесплатно, что бы ни стояло в прайсе.
/// С M11 у каждой системы свой магазин (<see cref="Local"/>): ассортимент региона плюс отличия станции, цены плавают.
/// </summary>
/// <param name="StartCredits">Столько кредитов у нового пилота (GDD §54 — 1 000).</param>
/// <param name="RepairPrice">Кредитов за единицу прочности корпуса при ремонте в доке; 0 — бесплатно.</param>
/// <param name="Hulls">Корпус — цена. Корпуса, которого здесь нет, не продают и не выкупают.</param>
/// <param name="Items">Пушка или модуль — цена Mk1; цена старших тиров — по <paramref name="Tiers"/>.</param>
/// <param name="SellShare">Доля цены, за которую станция выкупает пушку или модуль со склада.</param>
/// <param name="RepairHullShare">
/// Доля цены корпуса за полный ремонт (M12): латать крейсер дороже, чем челнок. 0 — ремонт зависит
/// только от <paramref name="RepairPrice"/>, как до M12.
/// </param>
/// <param name="Tiers">Множители Mk2 и Mk3 (<see cref="TierDef"/>); null — тиров нет.</param>
/// <param name="Regions">Ассортимент по регионам галактики; null — везде продают всё, что в прайсе (как до M11).</param>
/// <param name="Places">Отличия отдельных мест по ключу места (M15): «st:sol», «pl:terra».</param>
/// <param name="Stock">Магазин одного места: что здесь продают. null — всё, что в прайсе.</param>
/// <param name="Title">Магазин одного места: его подпись.</param>
/// <param name="Transport">Тариф буксира (M15.6); null — перевозки корпусов нет, за ними летают сами.</param>
/// <param name="Legacy">
/// Цены предметов, снятых с баланса (M15.6): по ним вход возвращает кредиты за то, чего больше нет в игре.
/// Эти id нарочно не проверяются по каталогам — их там уже нет, иначе проверка падала бы по построению.
/// </param>
public sealed record ShopRules(
    int StartCredits = 1000,
    double RepairPrice = 0,
    IReadOnlyDictionary<string, int>? Hulls = null,
    IReadOnlyDictionary<string, int>? Items = null,
    double SellShare = 0.5,
    IReadOnlyList<TierDef>? Tiers = null,
    IReadOnlyDictionary<string, StockDef>? Regions = null,
    IReadOnlyDictionary<string, StockDef>? Places = null,
    IReadOnlyList<string>? Stock = null,
    string? Title = null,
    double RepairHullShare = 0,
    TransportDef? Transport = null,
    IReadOnlyDictionary<string, int>? Legacy = null)
{
    public const string File = "shop.json";

    /// <summary>Без магазина: ничего не продают и кредитов на старте нет — для тестов и когда файла нет.</summary>
    public static readonly ShopRules None = new(StartCredits: 0);

    [JsonIgnore] public IReadOnlyDictionary<string, int> HullPrices => Hulls ?? new Dictionary<string, int>();
    [JsonIgnore] public IReadOnlyDictionary<string, int> ItemPrices => Items ?? new Dictionary<string, int>();

    /// <returns>Цена корпуса; null — нет в прайсе.</returns>
    public int? HullPrice(string id) => HullPrices.TryGetValue(id, out var price) ? price : null;

    /// <returns>Цена пушки или модуля (и старшего тира); null — нет в прайсе.</returns>
    public int? ItemPrice(string id)
    {
        if (ItemPrices.TryGetValue(id, out var price)) return price;
        var (baseId, tier) = Sim.Tiers.Split(id);
        if (tier <= 1 || Tiers is null || tier - 2 >= Tiers.Count || !ItemPrices.TryGetValue(baseId, out var basePrice)) return null;
        return Sim.Tiers.Price(basePrice, tier, Tiers);
    }

    /// <summary>Продают ли здесь этот корпус.</summary>
    public bool SellsHull(string id) => HullPrice(id) is not null && (Stock is null || Stock.Contains(id));

    /// <summary>Продают ли здесь эту пушку или модуль.</summary>
    public bool SellsItem(string id) => ItemPrice(id) is not null && (Stock is null || Stock.Contains(id));

    /// <summary>Доля цены, которую место платит за вещь; округляется вниз.</summary>
    private int Share(int price) => (int)Math.Floor(price * SellShare + 1e-9);

    /// <summary>Сколько станция даёт за пушку или модуль со склада; то, чего нет в прайсе, — даром.</summary>
    public int SellPrice(string id) => ItemPrice(id) is { } price ? Share(price) : 0;

    /// <summary>Сколько верфь даёт за голый корпус; то, чего нет в прайсе, — даром.</summary>
    public int SellHullPrice(string id) => HullPrice(id) is { } price ? Share(price) : 0;

    /// <summary>
    /// Сколько верфь даёт за корабль вместе с оснащением (M20c): корпус и каждая вещь считаются отдельно,
    /// каждая часть округляется вниз сама. Зеркало sellShipPrice в client/src/sim/shop.ts.
    /// </summary>
    public int SellShipPrice(string hullId, IEnumerable<string> items) => SellHullPrice(hullId) + items.Sum(SellPrice);

    /// <summary>
    /// Сколько стоит довести корпус до полной прочности; округляется вверх. С M12 к плате за единицу
    /// прочности добавляется доля цены корпуса: дорогой корабль и чинить дорого.
    /// </summary>
    /// <param name="maxHp">Полная прочность корпуса; 0 — считаем только по <see cref="RepairPrice"/>.</param>
    /// <param name="hullPrice">Цена корпуса в прайсе; 0 — надбавки нет (стартовый корпус даром).</param>
    public int RepairCost(double missingHp, double maxHp = 0, int hullPrice = 0)
    {
        if (!(missingHp > 0)) return 0;
        var byHull = maxHp > 0 && hullPrice > 0 ? RepairHullShare * hullPrice * missingHp / maxHp : 0;
        return (int)Math.Ceiling(missingHp * RepairPrice + byHull);
    }

    /// <summary>
    /// Сколько стоит привезти сюда корпус за jumps прыжков (M15.6); null — услуги здесь нет.
    /// Зеркало transportCost в client/src/sim/shop.ts: в доке показывают ровно то, что спишет сервер.
    /// </summary>
    /// <param name="hullPrice">Цена корпуса в прайсе; 0 — надбавки нет (стартовый корпус даром).</param>
    /// <param name="jumps">Прыжков между системами; 0 — другое место этой же системы.</param>
    public int? TransportCost(int hullPrice, int jumps) =>
        Transport is not { } t || jumps < 0 || hullPrice < 0
            ? null
            : (int)Math.Ceiling(t.Base + jumps * (t.PerJump + t.HullShare * hullPrice) - 1e-9);

    /// <summary>
    /// Цена снятого с баланса предмета (и его старшего тира); null — за это не возвращают. Этими ценами вход
    /// выкупает у пилота то, чего в игре больше нет (M15.6 — баки): молча отнять оплаченный модуль нельзя.
    /// </summary>
    public int? LegacyPrice(string id)
    {
        if (Legacy is null) return null;
        if (Legacy.TryGetValue(id, out var price)) return price;
        var (baseId, tier) = Sim.Tiers.Split(id);
        if (tier <= 1 || !Legacy.TryGetValue(baseId, out var basePrice)) return null;
        return Tiers is null || tier - 2 >= Tiers.Count ? null : Sim.Tiers.Price(basePrice, tier, Tiers);
    }

    /// <summary>
    /// Магазин места (M11, по местам — M15): ассортимент региона и отличия места, все цены — местные. Цены есть
    /// на всё, чтобы здесь можно было продать что угодно со склада, а купить — только из <see cref="Stock"/>.
    /// Без блока regions — этот же магазин везде.
    /// </summary>
    /// <param name="placeKey">Ключ места: см. <see cref="PlaceKey"/>.</param>
    /// <param name="itemIds">Все пушки и модули всех тиров.</param>
    public ShopRules Local(string placeKey, string? region, IEnumerable<string> itemIds)
    {
        if (Regions is null) return this;
        var regional = region is not null ? Regions.GetValueOrDefault(region) : null;
        var station = Places?.GetValueOrDefault(placeKey);
        var tiers = station?.Tiers ?? regional?.Tiers ?? [1];
        var factor = station?.Price ?? 1;

        var stock = new HashSet<string>();
        foreach (var hull in (regional?.HullList ?? []).Concat(station?.HullList ?? []))
            if (HullPrices.ContainsKey(hull)) stock.Add(hull);
        var ids = itemIds.ToHashSet();
        foreach (var item in (regional?.ItemList ?? []).Concat(station?.ItemList ?? []))
        {
            foreach (var tier in tiers)
            {
                var id = Sim.Tiers.Id(item, tier);
                if (ids.Contains(id) && ItemPrice(id) is not null) stock.Add(id);
            }
        }
        foreach (var removed in station?.RemoveList ?? []) stock.RemoveWhere(id => Sim.Tiers.Split(id).Base == removed);

        var hulls = HullPrices.ToDictionary(p => p.Key, p => Round(p.Value * factor));
        var items = new Dictionary<string, int>();
        foreach (var id in ids) if (ItemPrice(id) is { } price) items[id] = Round(price * factor);
        return this with
        {
            Hulls = hulls,
            Items = items,
            Tiers = null,
            Regions = null,
            Places = null,
            Stock = [.. stock.Order(StringComparer.Ordinal)],
            Title = station?.Title,
        };
    }

    /// <summary>
    /// Округление цены: крупные — до десятков, мелочь — до кредита. internal, потому что этим же правилом
    /// репутация двигает цены (<see cref="ReputationRules.Price"/>) — иначе дока и сервер разошлись бы.
    /// </summary>
    internal static int Round(double price) => price >= 100 ? (int)(Math.Round(price / 10) * 10) : (int)Math.Round(price);

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
        if (!(SellShare >= 0 && SellShare <= 1)) return "sellShare must be within 0..1";
        if (!(RepairHullShare >= 0)) return "repairHullShare must not be negative";
        if (Transport?.Validate() is { } transport) return $"transport: {transport}";
        foreach (var (id, price) in Legacy ?? new Dictionary<string, int>())
            if (price < 0) return $"legacy.{id}: price must not be negative";
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
        if (Sim.Tiers.Validate(Tiers) is { } tiers) return tiers;
        var maxTier = 1 + (Tiers?.Count ?? 0);
        foreach (var (block, defs) in new[] { ("regions", Regions), ("places", Places) })
        {
            if (defs is null) continue;
            foreach (var (id, def) in defs)
            {
                if (def is null) return $"{block}.{id}: is null";
                foreach (var hull in def.HullList) if (!HullPrices.ContainsKey(hull)) return $"{block}.{id}: hull '{hull}' has no price";
                foreach (var item in def.ItemList.Concat(def.RemoveList))
                {
                    if (!ItemPrices.ContainsKey(item) && !HullPrices.ContainsKey(item)) return $"{block}.{id}: '{item}' has no price";
                }
                if (def.Tiers is { } list && list.Any(t => t < 1 || t > maxTier)) return $"{block}.{id}: tiers must be within 1..{maxTier}";
                if (!(def.Price > 0)) return $"{block}.{id}: price must be positive";
            }
        }
        return null;
    }

    /// <summary>Только множители тиров — они нужны до разбора остального баланса: каталоги пушек и модулей раскрываются по ним.</summary>
    public static bool TryReadTiers(string json, out IReadOnlyList<TierDef>? tiers, out string? error)
    {
        tiers = null;
        try
        {
            tiers = JsonSerializer.Deserialize<ShopRules>(json, JsonCatalog.Options)?.Tiers;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }
        error = Sim.Tiers.Validate(tiers);
        return error is null;
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
