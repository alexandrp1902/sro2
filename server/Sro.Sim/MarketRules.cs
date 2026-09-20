using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Товар на рынке (M12): название, объём и базовая цена у него в loot.json, здесь — только рыночное.</summary>
/// <param name="Baseline">Норма запаса на обычной станции, штук. Чем меньше, тем резче цена ходит от сделок.</param>
/// <param name="Illegal">Регионы, где товар вне закона: там его не купить и не продать (наказание — с репутацией в M13).</param>
public sealed record MarketGood(double Baseline = 0, IReadOnlyList<string>? Illegal = null)
{
    [JsonIgnore] public IReadOnlyList<string> IllegalIn => Illegal ?? [];
}

/// <summary>
/// Профиль станции: что она производит и что скупает. Производит — склад большой, цена ниже справедливой;
/// скупает — склад мал, цена выше. Товар не в обоих списках торгуется по справедливой цене.
/// </summary>
public sealed record MarketStation(IReadOnlyList<string>? Produces = null, IReadOnlyList<string>? Consumes = null)
{
    [JsonIgnore] public IReadOnlyList<string> ProduceList => Produces ?? [];
    [JsonIgnore] public IReadOnlyList<string> ConsumeList => Consumes ?? [];
}

/// <summary>Роль товара на этой станции — от неё и уровень цены, и размер склада.</summary>
public enum MarketRole
{
    Neutral,
    Produces,
    Consumes,
}

/// <summary>
/// Рынок товаров из shared/market.json (GDD §22, §27; M12): у станции есть склад с запасом по каждому товару,
/// цена ходит от того, насколько запас отклонился от нормы. Здесь только правила и чистая математика —
/// сам запас живёт в комнате (Sro.Server.Game.Market), потому что этот объект пересоздаётся при каждой
/// правке в shared/.
///
/// Два рычага, и они разные. <see cref="ProduceMul"/>/<see cref="ConsumeMul"/> задают уровень цены — поэтому
/// товар Рубежа дорог в Ядре даже при полном складе. <see cref="ProduceStock"/>/<see cref="ConsumeStock"/>
/// задают, насколько сильно сделка двигает цену. Если бы профиль менял только размер склада, каждая свежая
/// станция стояла бы ровно на базовой цене и региональная разница исчезла бы.
/// </summary>
/// <param name="Spread">Станция продаёт дороже, чем скупает, на эту долю: ±Spread/2 от справедливой цены.</param>
/// <param name="Elasticity">Насколько круто цена отзывается на отклонение запаса от нормы.</param>
/// <param name="MinFactor">Цена не опускается ниже этой доли базовой.</param>
/// <param name="MaxFactor">…и не поднимается выше этой.</param>
/// <param name="StockFloor">Доля нормы, ниже которой запас в формуле не опускается: иначе деление на ноль.</param>
/// <param name="StockCap">Выше нормы, умноженной на это, запас не растёт.</param>
/// <param name="HalfLifeSeconds">За столько запас проходит половину пути к норме.</param>
/// <param name="TickSeconds">Как часто считается возврат к норме.</param>
/// <param name="TraderUnits">Сколько единиц товара двигает один NPC-торговец.</param>
/// <param name="Baseline">Норма запаса по умолчанию, если у товара своя не указана.</param>
/// <param name="Goods">Что вообще торгуется; каждый id должен быть грузом из loot.json.</param>
/// <param name="Stations">Профили станций по id системы. Системы без станции рынка не имеют.</param>
/// <param name="Station">Рынок одной системы (<see cref="Local"/>): профиль этой станции; null — рынка здесь нет.</param>
/// <param name="Region">Рынок одной системы: её регион — по нему смотрят, что тут вне закона.</param>
public sealed record MarketRules(
    double Spread = 0.18,
    double Elasticity = 0.6,
    double MinFactor = 0.45,
    double MaxFactor = 2.2,
    double StockFloor = 0.08,
    double StockCap = 3,
    double HalfLifeSeconds = 600,
    double TickSeconds = 5,
    double ProduceMul = 0.7,
    double ConsumeMul = 1.45,
    double IllegalMul = 1.6,
    double ProduceStock = 2.5,
    double ConsumeStock = 0.5,
    int TraderUnits = 8,
    double Baseline = 100,
    IReadOnlyDictionary<string, MarketGood>? Goods = null,
    IReadOnlyDictionary<string, MarketStation>? Stations = null,
    MarketStation? Station = null,
    string? Region = null)
{
    public const string File = "market.json";

    /// <summary>Рынка нет: товар продаётся по плоской цене loot.json, как до M12.</summary>
    public static readonly MarketRules None = new();

    [JsonIgnore] public IReadOnlyDictionary<string, MarketGood> GoodMap => Goods ?? new Dictionary<string, MarketGood>();

    /// <summary>Торгуют ли здесь вообще: у системы без станции рынка нет.</summary>
    [JsonIgnore] public bool Any => Station is not null && GoodMap.Count > 0;

    /// <summary>Чем торгует эта станция, по порядку id — порядок строк в доке должен быть устойчивым.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Sold =>
        Any ? [.. GoodMap.Keys.Where(id => !IsIllegal(id)).Order(StringComparer.Ordinal)] : [];

    /// <summary>Товар вне закона в этом регионе: здесь его не купить и не продать.</summary>
    public bool IsIllegal(string good) =>
        Region is not null && GoodMap.TryGetValue(good, out var def) && def.IllegalIn.Contains(Region);

    /// <summary>Торгуют ли здесь этим товаром.</summary>
    public bool Trades(string good) => Any && GoodMap.ContainsKey(good) && !IsIllegal(good);

    /// <summary>Что станция делает с этим товаром.</summary>
    public MarketRole Role(string good)
    {
        if (Station is null) return MarketRole.Neutral;
        if (Station.ProduceList.Contains(good)) return MarketRole.Produces;
        if (Station.ConsumeList.Contains(good)) return MarketRole.Consumes;
        return MarketRole.Neutral;
    }

    /// <summary>Равновесный запас товара на этой станции, штук: у производителя большой, у потребителя малый.</summary>
    public double Norm(string good)
    {
        var baseline = GoodMap.TryGetValue(good, out var def) && def.Baseline > 0 ? def.Baseline : Baseline;
        return baseline * Role(good) switch
        {
            MarketRole.Produces => ProduceStock,
            MarketRole.Consumes => ConsumeStock,
            _ => 1,
        };
    }

    /// <summary>Уровень цены на этой станции: производитель отдаёт дешевле справедливой, потребитель платит дороже.</summary>
    public double Level(string good)
    {
        var level = Role(good) switch
        {
            MarketRole.Produces => ProduceMul,
            MarketRole.Consumes => ConsumeMul,
            _ => 1,
        };
        return IsIllegal(good) ? level * IllegalMul : level;
    }

    /// <summary>
    /// Справедливая цена штуки при таком запасе: чем меньше на складе, тем дороже. Зажата в MinFactor..MaxFactor,
    /// чтобы затоваренный склад не отдавал даром, а пустой не просил бесконечность.
    /// </summary>
    /// <param name="basePrice">Базовая цена товара из loot.json.</param>
    public double Mid(string good, double basePrice, double stock)
    {
        var norm = Norm(good);
        if (!(norm > 0) || !(basePrice > 0)) return basePrice;
        var floor = Math.Max(stock, StockFloor * norm);
        var mid = basePrice * Level(good) * Math.Pow(norm / floor, Elasticity);
        return Math.Clamp(mid, basePrice * MinFactor, basePrice * MaxFactor);
    }

    /// <summary>Сколько пилот платит станции за штуку.</summary>
    public int BuyPrice(string good, double basePrice, double stock)
    {
        var sell = SellPrice(good, basePrice, stock);
        var buy = (int)Math.Ceiling(Mid(good, basePrice, stock) * (1 + Spread / 2) - 1e-9);
        // Купить и тут же продать всегда в убыток: иначе станция сама себя обкрадывает.
        return Math.Max(buy, sell + 1);
    }

    /// <summary>Сколько пилот получает от станции за штуку.</summary>
    public int SellPrice(string good, double basePrice, double stock)
    {
        var sell = (int)Math.Floor(Mid(good, basePrice, stock) * (1 - Spread / 2) + 1e-9);
        return Math.Max(sell, basePrice > 0 ? 1 : 0);
    }

    /// <summary>
    /// Сделка целиком. Цена шагает по единицам: каждая следующая штука дороже (при покупке) или дешевле
    /// (при продаже) предыдущей. Поэтому крупная сделка сама себе портит цену, и оптом по цене первой
    /// штуки не уедешь.
    /// </summary>
    /// <param name="buying">true — пилот покупает у станции, false — продаёт ей.</param>
    /// <returns>Сколько всего кредитов и какой станет запас.</returns>
    public (int Credits, double Stock) Trade(string good, double basePrice, double stock, int count, bool buying)
    {
        var credits = 0;
        for (var i = 0; i < count; i++)
        {
            credits += buying ? BuyPrice(good, basePrice, stock) : SellPrice(good, basePrice, stock);
            stock = Math.Max(0, stock + (buying ? -1 : 1));
        }
        return (credits, Clamp(good, stock));
    }

    /// <summary>Запас за столько секунд возвращается к норме: половина пути за HalfLifeSeconds.</summary>
    public double Regress(string good, double stock, double seconds)
    {
        var norm = Norm(good);
        if (!(seconds > 0) || !(HalfLifeSeconds > 0)) return Clamp(good, stock);
        var k = 1 - Math.Pow(0.5, seconds / HalfLifeSeconds);
        return Clamp(good, stock + (norm - stock) * k);
    }

    /// <summary>Запас не бывает отрицательным и не растёт выше нормы, умноженной на StockCap.</summary>
    public double Clamp(string good, double stock) => Math.Clamp(stock, 0, Norm(good) * StockCap);

    /// <summary>
    /// Рынок одной системы (M12): профиль этой станции и её регион. Без блока stations рынка нет нигде.
    /// Станция не названа — в системе не торгуют (её может и не быть вовсе).
    /// </summary>
    public MarketRules Local(string systemId, string? region)
    {
        var station = Stations?.GetValueOrDefault(systemId);
        return this with { Station = station, Region = region, Stations = null };
    }

    /// <param name="items">Груз из loot.json: каждый торгуемый товар должен быть там.</param>
    /// <param name="hasStation">Есть ли станция в системе с таким id; null — галактика ещё не разобрана.</param>
    /// <param name="regions">Регионы галактики; null — не проверяем.</param>
    public string? Validate(
        IReadOnlyDictionary<string, LootItem> items,
        Func<string, bool>? hasStation = null,
        IReadOnlySet<string>? regions = null)
    {
        if (!(Spread >= 0 && Spread < 2)) return "spread must be within 0..2";
        if (!(Elasticity >= 0)) return "elasticity must not be negative";
        if (!(MinFactor > 0)) return "minFactor must be positive";
        if (!(MaxFactor >= MinFactor)) return "maxFactor must not be below minFactor";
        if (!(StockFloor > 0)) return "stockFloor must be positive";
        if (!(StockCap >= 1)) return "stockCap must be at least 1";
        if (!(HalfLifeSeconds > 0)) return "halfLifeSeconds must be positive";
        if (!(TickSeconds > 0)) return "tickSeconds must be positive";
        if (!(ProduceMul > 0)) return "produceMul must be positive";
        if (!(ConsumeMul > 0)) return "consumeMul must be positive";
        if (!(IllegalMul > 0)) return "illegalMul must be positive";
        if (!(ProduceStock > 0)) return "produceStock must be positive";
        if (!(ConsumeStock > 0)) return "consumeStock must be positive";
        if (TraderUnits < 0) return "traderUnits must not be negative";
        if (!(Baseline > 0)) return "baseline must be positive";

        foreach (var (id, def) in GoodMap)
        {
            if (def is null) return $"goods.{id}: is null";
            if (!items.ContainsKey(id)) return $"goods.{id}: unknown item in {LootRules.File}";
            if (def.Baseline < 0) return $"goods.{id}: baseline must not be negative";
            foreach (var region in def.IllegalIn)
            {
                if (regions is not null && !regions.Contains(region)) return $"goods.{id}: unknown region '{region}'";
            }
        }
        foreach (var (id, def) in Stations ?? new Dictionary<string, MarketStation>())
        {
            if (def is null) return $"stations.{id}: is null";
            if (hasStation is not null && !hasStation(id)) return $"stations.{id}: no station in that system";
            foreach (var good in def.ProduceList.Concat(def.ConsumeList))
            {
                if (!GoodMap.ContainsKey(good)) return $"stations.{id}: '{good}' is not traded, add it to goods";
            }
            // Производить и скупать одно и то же — почти наверняка опечатка, а цена от этого ведёт себя загадочно.
            foreach (var good in def.ProduceList)
            {
                if (def.ConsumeList.Contains(good)) return $"stations.{id}: '{good}' is both produced and consumed";
            }
        }
        return null;
    }

    public static bool TryParse(
        string json,
        IReadOnlyDictionary<string, LootItem> items,
        out MarketRules rules,
        out string? error,
        Func<string, bool>? hasStation = null,
        IReadOnlySet<string>? regions = null)
    {
        rules = None;
        MarketRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MarketRules>(json, JsonCatalog.Options);
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
        error = parsed.Validate(items, hasStation, regions);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
