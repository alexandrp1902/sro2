using System.Text;
using System.Text.Json;

namespace Sro.Sim.Tests;

/// <summary>
/// Эталоны shared/test-vectors/shop.json (M15.6): по ним же проверяется TS-формула клиента (Vitest), так что
/// кнопка «Перевезти · 4 200 кр» обещает ровно то, что спишет сервер. Здесь же ремонт — он тоже посчитан
/// дважды на двух языках и до сих пор не был сверен.
/// Перегенерация: SRO_UPDATE_VECTORS=1 dotnet test.
/// </summary>
public class ShopVectorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица без \u — diff читается
    };

    /// <summary>Перегон корпуса такой цены за столько прыжков.</summary>
    private sealed record TransportCase(int HullPrice, int Jumps, int Credits);

    /// <summary>Ремонт: сколько прочности недостаёт, какая она полная и сколько стоит корпус.</summary>
    private sealed record RepairCase(double MissingHp, double MaxHp, int HullPrice, int Credits);

    /// <summary>Выкуп корабля из ангара вместе с оснащением (M20c).</summary>
    private sealed record SellCase(string Hull, string[] Items, int Credits);

    private sealed record VectorFile(ShopRules Shop, TransportCase[] Transport, RepairCase[] Repair, SellCase[] Sell);

    /// <summary>
    /// Свой прайс: эталон не должен ездить от тюнинга shared/shop.json. Числа тарифа — те же, что в файле,
    /// потому что именно их баланс и проверяется глазами в плейтесте.
    /// </summary>
    private static ShopRules Shop() => new(
        Hulls: new Dictionary<string, int> { ["light"] = 0, ["fighter"] = 3000, ["liner"] = 26_000, ["cruiser"] = 60_000 },
        // 999 — нарочно нечётная цена: доля от неё округляется вниз, и обе формулы должны сделать это одинаково.
        Items: new Dictionary<string, int> { ["laser"] = 800, ["shieldM"] = 2400, ["engineL"] = 5500, ["scrap"] = 999 },
        RepairPrice: 0.25,
        RepairHullShare: 0.06,
        Transport: new TransportDef(Base: 400, PerJump: 800, HullShare: 0.05));

    private static string VectorPath() => Path.Combine(TestHulls.RepoRoot(), "shared", "test-vectors", "shop.json");

    [Fact]
    public void ShopMatchesSharedVectors()
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
        var shop = Shop();
        Assert.Equal(generated.Transport.Length, stored.Transport.Length);
        Assert.Equal(generated.Repair.Length, stored.Repair.Length);
        Assert.Equal(generated.Sell.Length, stored.Sell.Length);

        foreach (var c in stored.Transport) Assert.Equal(c.Credits, shop.TransportCost(c.HullPrice, c.Jumps));
        foreach (var c in stored.Repair) Assert.Equal(c.Credits, shop.RepairCost(c.MissingHp, c.MaxHp, c.HullPrice));
        foreach (var c in stored.Sell) Assert.Equal(c.Credits, shop.SellShipPrice(c.Hull, c.Items));
    }

    /// <summary>Без блока transport услуги нет вовсе: кнопки перевозки в доке не будет.</summary>
    [Fact]
    public void TransportCost_IsNullWithoutATariff_AndRefusesANegativeRoute()
    {
        Assert.Null(new ShopRules().TransportCost(3000, 2));
        Assert.Null(Shop().TransportCost(3000, -1));
        // В пределах одной системы — только плата за вызов: прыжков нет, значит и платить за них нечего.
        Assert.Equal(400, Shop().TransportCost(60_000, 0));
    }

    /// <summary>Тариф переживает Local: он галактический, и местная витрина его не меняет.</summary>
    [Fact]
    public void Local_KeepsTheTariff()
    {
        var shop = Shop() with { Hulls = new Dictionary<string, int> { ["light"] = 0 }, Regions = new Dictionary<string, StockDef> { ["core"] = new() } };
        Assert.Equal(shop.TransportCost(3000, 2), shop.Local("st:sol", "core", []).TransportCost(3000, 2));
    }

    private static VectorFile Generate()
    {
        var shop = Shop();
        var transport = new List<TransportCase>();
        var repair = new List<RepairCase>();

        // Стартовый даром, средний, лайнер и крейсер: весь разбег цен корпусов из shop.json.
        foreach (var price in new[] { 0, 3000, 26_000, 60_000 })
        {
            // 0 — другое место этой же системы, 5 — через всю галактику.
            foreach (var jumps in new[] { 0, 1, 2, 3, 5 })
            {
                transport.Add(new TransportCase(price, jumps, shop.TransportCost(price, jumps)!.Value));
            }
            foreach (var (missing, max) in new (double, double)[] { (0, 400), (1, 400), (200, 400), (400, 400) })
            {
                repair.Add(new RepairCase(missing, max, price, shop.RepairCost(missing, max, price)));
            }
        }

        // Выкуп корабля: голый корпус, он же с оснащением, самый дорогой с полным набором, стартовый
        // (за корпус не дают ничего, а вещи на нём всё равно стоят) и нечётная цена — на округление вниз.
        SellCase sell(string hull, params string[] items) => new(hull, items, shop.SellShipPrice(hull, items));
        SellCase[] sells =
        [
            sell("fighter"),
            sell("fighter", "laser", "laser"),
            sell("cruiser", "laser", "laser", "shieldM", "engineL"),
            sell("light", "laser"),
            sell("fighter", "scrap"),
            sell("fighter", "notInThePriceList"),
        ];

        return new VectorFile(shop, [.. transport], [.. repair], sells);
    }

    /// <summary>По строке на случай — чтобы diff файла читался.</summary>
    private static string Format(VectorFile file)
    {
        var sb = new StringBuilder("{\n");
        sb.Append("  \"shop\": ").Append(JsonSerializer.Serialize(file.Shop, Json)).Append(",\n");
        AppendList(sb, "transport", file.Transport, last: false);
        AppendList(sb, "repair", file.Repair, last: false);
        AppendList(sb, "sell", file.Sell, last: true);
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
