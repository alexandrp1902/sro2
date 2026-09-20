using System.Text;
using System.Text.Json;

namespace Sro.Sim.Tests;

/// <summary>
/// Эталоны shared/test-vectors/market.json (M12): по ним же проверяется TS-формула клиента (Vitest), так что
/// кнопка «Купить 10 · 1 240 кр» обещает ровно то, что спишет сервер. Перегенерация: SRO_UPDATE_VECTORS=1 dotnet test.
/// </summary>
public class MarketVectorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица без \u — diff читается
    };

    /// <summary>Цена штуки при таком запасе на станции с такой ролью.</summary>
    private sealed record PriceCase(string Station, string Good, double Base, double Stock, int Buy, int Sell);

    /// <summary>Сделка целиком: цена шагает по единицам, поэтому важна и сумма, и во что превратился запас.</summary>
    private sealed record TradeCase(string Station, string Good, double Base, double Stock, int Count, bool Buying, int Credits, double After);

    /// <summary>Сколько штук по карману.</summary>
    private sealed record AffordCase(string Station, string Good, double Base, double Stock, int Max, int Credits, int Count);

    /// <summary>Возврат запаса к норме за столько секунд.</summary>
    private sealed record RegressCase(string Station, string Good, double Stock, double Seconds, double After);

    private sealed record VectorFile(
        Dictionary<string, MarketRules> Stations,
        PriceCase[] Price,
        TradeCase[] Trade,
        AffordCase[] Afford,
        RegressCase[] Regress);

    private const string Food = "food";
    private const string Ore = "ore";
    private const string Arms = "arms";

    /// <summary>Свой набор товаров: эталон не должен зависеть от тюнинга shared/.</summary>
    private static readonly Dictionary<string, MarketGood> Goods = new()
    {
        [Food] = new(Baseline: 100),
        [Ore] = new(Baseline: 240),
        [Arms] = new(Baseline: 50, Illegal: ["core"]),
    };

    private static readonly Dictionary<string, MarketStation> Profiles = new()
    {
        // Ядро делает продовольствие и скупает руду; оружие здесь вне закона.
        ["core"] = new(Produces: [Food], Consumes: [Ore]),
        // Рубеж наоборот: руду добывает, продовольствия не хватает, оружием торгует свободно.
        ["rim"] = new(Produces: [Ore, Arms], Consumes: [Food]),
        // Перевалочная станция: ничего не делает и ничего не ждёт — справедливая цена на всё.
        ["neutral"] = new(),
    };

    private static Dictionary<string, MarketRules> Stations() =>
        Profiles.ToDictionary(
            p => p.Key,
            p => new MarketRules(Goods: Goods, Station: p.Value, Region: p.Key == "core" ? "core" : "rim"));

    private static string VectorPath() => Path.Combine(TestHulls.RepoRoot(), "shared", "test-vectors", "market.json");

    [Fact]
    public void MarketMatchesSharedVectors()
    {
        var generated = Generate();
        var path = VectorPath();
        if (Environment.GetEnvironmentVariable("SRO_UPDATE_VECTORS") == "1")
        {
            File.WriteAllText(path, Format(generated));
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing: run 'SRO_UPDATE_VECTORS=1 dotnet test'");
        var stored = JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(path), Json)!;
        var stations = Stations();
        Assert.Equal(generated.Price.Length, stored.Price.Length);
        Assert.Equal(generated.Trade.Length, stored.Trade.Length);
        Assert.Equal(generated.Afford.Length, stored.Afford.Length);
        Assert.Equal(generated.Regress.Length, stored.Regress.Length);

        foreach (var c in stored.Price)
        {
            var rules = stations[c.Station];
            Assert.Equal(c.Buy, rules.BuyPrice(c.Good, c.Base, c.Stock));
            Assert.Equal(c.Sell, rules.SellPrice(c.Good, c.Base, c.Stock));
        }
        foreach (var c in stored.Trade)
        {
            var (credits, after) = stations[c.Station].Trade(c.Good, c.Base, c.Stock, c.Count, c.Buying);
            Assert.Equal(c.Credits, credits);
            Assert.Equal(c.After, after, 9);
        }
        foreach (var c in stored.Afford)
            Assert.Equal(c.Count, stations[c.Station].Affordable(c.Good, c.Base, c.Stock, c.Max, c.Credits));
        foreach (var c in stored.Regress)
            Assert.Equal(c.After, stations[c.Station].Regress(c.Good, c.Stock, c.Seconds), 9);
    }

    private static VectorFile Generate()
    {
        var stations = Stations();
        var prices = new List<PriceCase>();
        var trades = new List<TradeCase>();
        var affords = new List<AffordCase>();
        var regress = new List<RegressCase>();

        foreach (var (id, rules) in stations)
        {
            foreach (var (good, basePrice) in new (string, double)[] { (Food, 30), (Ore, 10), (Arms, 110) })
            {
                var norm = rules.Norm(good);
                // Пустой склад, четверть нормы, норма, тройная норма — оба зажима и середина.
                foreach (var stock in new[] { 0, norm / 4, norm, norm * 3 })
                {
                    prices.Add(new PriceCase(id, good, basePrice, stock, rules.BuyPrice(good, basePrice, stock), rules.SellPrice(good, basePrice, stock)));
                }
                foreach (var count in new[] { 1, 10, 75 })
                {
                    foreach (var buying in new[] { true, false })
                    {
                        var (credits, after) = rules.Trade(good, basePrice, norm, count, buying);
                        trades.Add(new TradeCase(id, good, basePrice, norm, count, buying, credits, after));
                    }
                }
                foreach (var credits in new[] { 0, 100, 5000 })
                {
                    affords.Add(new AffordCase(id, good, basePrice, norm, 50, credits, rules.Affordable(good, basePrice, norm, 50, credits)));
                }
                foreach (var seconds in new[] { 5.0, 600.0, 3600.0 })
                {
                    regress.Add(new RegressCase(id, good, norm * 3, seconds, rules.Regress(good, norm * 3, seconds)));
                    regress.Add(new RegressCase(id, good, 0, seconds, rules.Regress(good, 0, seconds)));
                }
            }
        }

        return new VectorFile(stations, [.. prices], [.. trades], [.. affords], [.. regress]);
    }

    /// <summary>По строке на случай — чтобы diff файла читался.</summary>
    private static string Format(VectorFile file)
    {
        var sb = new StringBuilder("{\n");
        sb.Append("  \"stations\": {\n");
        var i = 0;
        foreach (var (id, rules) in file.Stations)
        {
            sb.Append("    ").Append(JsonSerializer.Serialize(id, Json)).Append(": ").Append(JsonSerializer.Serialize(rules, Json));
            sb.Append(++i == file.Stations.Count ? "\n" : ",\n");
        }
        sb.Append("  },\n");
        AppendList(sb, "price", file.Price, last: false);
        AppendList(sb, "trade", file.Trade, last: false);
        AppendList(sb, "afford", file.Afford, last: false);
        AppendList(sb, "regress", file.Regress, last: true);
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendList<T>(StringBuilder sb, string name, T[] items, bool last)
    {
        sb.Append("  \"").Append(name).Append("\": [\n");
        for (var i = 0; i < items.Length; i++)
            sb.Append("    ").Append(JsonSerializer.Serialize(items[i], Json)).Append(i == items.Length - 1 ? "\n" : ",\n");
        sb.Append(last ? "  ]\n" : "  ],\n");
    }
}
