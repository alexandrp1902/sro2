using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Рынок товаров в комнате (M12): покупка и продажа груза по местным ценам, склад станции, возврат запасов
/// к норме, поставки торговцев и переживание правки баланса.
/// Числа свои, а не из shared/: тюнинг экономики не должен ронять тесты.
/// </summary>
public sealed class MarketTests
{
    private const string Food = "food";
    private const string Ore = "ore";
    private const string Contraband = "arms";

    private static readonly LootRules Loot = new(
        StationRange: 200,
        Items: new Dictionary<string, LootItem>
        {
            [Food] = new("Продовольствие", Volume: 1, Price: 30),
            [Ore] = new("Руда", Volume: 1, Price: 10),
            [Contraband] = new("Оружие", Volume: 1, Price: 100),
        });

    private static readonly ShopRules Shop = new(StartCredits: 1000);

    /// <summary>
    /// Станция делает продовольствие и оружие, скупает руду. В Ядре оружие вне закона, на Рубеже — нет.
    /// </summary>
    private static MarketRules Market(string region = "core") => new MarketRules(
        Goods: new Dictionary<string, MarketGood>
        {
            [Food] = new(Baseline: 100),
            [Ore] = new(Baseline: 100),
            [Contraband] = new(Baseline: 50, Illegal: ["core"]),
        },
        Stations: new Dictionary<string, MarketStation>
        {
            [GalaxyRules.DefaultSystem] = new(Produces: [Food, Contraband], Consumes: [Ore]),
        })
        .Local(GalaxyRules.DefaultSystem, region);

    private static Room NewRoom(MarketRules? market = null) => new(
        TestBalance.Create(new CombatRules(SpawnJitter: 0), loot: Loot, shop: Shop, market: market ?? Market()),
        NullLogger.Instance);

    private static (Room Room, FakeConnection Connection, Player Player) Docked(MarketRules? market = null)
    {
        var room = NewRoom(market);
        var connection = new FakeConnection(1);
        room.Join(connection, null, "Trader", null);
        var player = room.Pilot(connection.Last<WelcomeMsg>().Id)!;
        player.Ship = new ShipState { X = 0, Y = 50 };
        room.Dock(connection, true);
        Assert.True(connection.Last<HangarMsg>().Docked);
        return (room, connection, player);
    }

    private static MarketItemDto Quote(FakeConnection connection, string good) =>
        connection.Last<MarketMsg>().Items.Single(i => i.Id == good);

    [Fact]
    public void Docking_SendsThePrices()
    {
        var (_, a, _) = Docked();

        var market = a.Last<MarketMsg>();
        // Контрабанды в списке нет: в Ядре ею не торгуют.
        Assert.Equal([Food, Ore], market.Items.Select(i => i.Id).Order());
        foreach (var item in market.Items) Assert.True(item.Buy > item.Sell, item.Id);
    }

    [Fact]
    public void Producer_SellsBelowBase_AndConsumerPaysAboveIt()
    {
        var (_, a, _) = Docked();

        // Продовольствие тут делают — отдают дешевле нормы; руду скупают — платят дороже нормы.
        // Отсюда и маршрут: руду везут сюда, продовольствие — отсюда.
        Assert.True(Quote(a, Food).Buy < Loot.Price(Food), $"food at {Quote(a, Food).Buy}, base {Loot.Price(Food)}");
        Assert.True(Quote(a, Ore).Sell > Loot.Price(Ore), $"ore at {Quote(a, Ore).Sell}, base {Loot.Price(Ore)}");
    }

    [Fact]
    public void Buying_TakesCreditsAndFillsTheHold()
    {
        var (room, a, player) = Docked();
        var before = player.Credits;
        var price = Quote(a, Food).Buy;

        room.BuyGoods(a, Food, 5);

        Assert.Equal(5, player.Cargo.Count(Food));
        var spent = before - player.Credits;
        Assert.True(spent >= price * 5, $"spent {spent} must be at least {price * 5}: price steps up as you buy");
        Assert.Equal(player.Credits, a.Last<CargoMsg>().Credits);
    }

    [Fact]
    public void Buying_MovesThePriceUp_AndSellingMovesItBack()
    {
        var (room, a, _) = Docked();
        var start = Quote(a, Food).Buy;

        room.BuyGoods(a, Food, 40);
        var afterBuy = Quote(a, Food).Buy;
        room.Sell(a, Food, 40);
        var afterSell = Quote(a, Food).Buy;

        Assert.True(afterBuy > start, $"{afterBuy} > {start}");
        Assert.True(afterSell < afterBuy, $"{afterSell} < {afterBuy}");
    }

    [Fact]
    public void BuyingAndSellingOnTheSpot_LosesMoney()
    {
        var (room, a, player) = Docked();
        var before = player.Credits;

        room.BuyGoods(a, Food, 10);
        room.Sell(a, Food, 10);

        Assert.True(player.Credits < before, $"{player.Credits} < {before}");
        Assert.Equal(0, player.Cargo.Count(Food));
    }

    [Fact]
    public void Buying_IsCappedByTheHold()
    {
        var (room, a, player) = Docked();

        room.BuyGoods(a, Food, 1000);

        // Трюм «лёгкого» — 20, объём продовольствия 1.
        Assert.Equal(20, player.Cargo.Count(Food));
        Assert.True(player.Cargo.Used(Loot) <= player.Effective(room.Balance).Cargo);
    }

    [Fact]
    public void Buying_IsCappedByCredits()
    {
        var (room, a, player) = Docked();
        player.Credits = Quote(a, Food).Buy * 3;

        room.BuyGoods(a, Food, 20);

        Assert.True(player.Cargo.Count(Food) is > 0 and <= 3);
        Assert.True(player.Credits >= 0);
    }

    [Fact]
    public void Buying_WithoutCredits_SaysSo()
    {
        var (room, a, player) = Docked();
        player.Credits = 0;

        room.BuyGoods(a, Food, 1);

        Assert.Equal(Protocol.NoCreditsNotice, a.Last<NoticeMsg>().Code);
        Assert.Empty(player.Cargo.Items);
    }

    [Fact]
    public void Buying_WithAFullHold_SaysSo()
    {
        var (room, a, player) = Docked();
        player.Cargo.Add(Ore, 20);

        room.BuyGoods(a, Food, 1);

        Assert.Equal(Protocol.CargoFullNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal(0, player.Cargo.Count(Food));
    }

    [Fact]
    public void Buying_ContrabandInTheCore_IsRefused()
    {
        var (room, a, player) = Docked();

        room.BuyGoods(a, Contraband, 1);

        Assert.Equal(Protocol.NoGoodsNotice, a.Last<NoticeMsg>().Code);
        Assert.Empty(player.Cargo.Items);
    }

    [Fact]
    public void Contraband_IsTradedWhereItIsLegal()
    {
        var (room, a, player) = Docked(Market(region: "rim"));

        room.BuyGoods(a, Contraband, 2);

        Assert.Equal(2, player.Cargo.Count(Contraband));
    }

    [Fact]
    public void Buying_WhatTheStationOnlyBuys_IsRefused()
    {
        var (room, a, player) = Docked();

        // Руду здесь скупают, но не перепродают: склад станции — это её продукция, а не витрина всего.
        room.BuyGoods(a, Ore, 1);

        Assert.Equal(Protocol.NoGoodsNotice, a.Last<NoticeMsg>().Code);
        Assert.Empty(player.Cargo.Items);
        Assert.Contains(a.Last<MarketMsg>().Items, i => i.Id == Ore); // но в списке он есть — его видно, чтобы продать
    }

    [Fact]
    public void Buying_OutsideTheDock_SaysSo()
    {
        var room = NewRoom();
        var a = new FakeConnection(1);
        room.Join(a, null, "Trader", null);

        room.BuyGoods(a, Food, 1);

        Assert.Equal(Protocol.TooFarNotice, a.Last<NoticeMsg>().Code);
    }

    [Fact]
    public void Buying_MoreThanTheStationHas_IsCappedByStock()
    {
        var (room, a, player) = Docked();
        var stock = Quote(a, Food).Stock;
        player.Credits = 1_000_000;
        player.HullId = "heavy"; // трюм 60

        room.BuyGoods(a, Food, stock + 100);

        Assert.True(player.Cargo.Count(Food) <= stock);
    }

    [Fact]
    public void Selling_PartOfAStack_LeavesTheRest()
    {
        var (room, a, player) = Docked();
        player.Cargo.Add(Ore, 10);

        room.Sell(a, Ore, 4);

        Assert.Equal(6, player.Cargo.Count(Ore));
        Assert.True(player.Credits > Shop.StartCredits);
    }

    [Fact]
    public void Selling_WithoutACount_TakesTheWholeStack()
    {
        var (room, a, player) = Docked();
        player.Cargo.Add(Ore, 7);

        room.Sell(a, Ore);

        Assert.Equal(0, player.Cargo.Count(Ore));
    }

    [Fact]
    public void Selling_TheWholeHold_LeavesWhatIsNotTradedHere()
    {
        var (room, a, player) = Docked();
        player.Cargo.Add(Ore, 5);
        player.Cargo.Add(Contraband, 3);

        room.Sell(a, null);

        Assert.Equal(0, player.Cargo.Count(Ore));
        Assert.Equal(3, player.Cargo.Count(Contraband)); // контрабанду в Ядре не берут — остаётся в трюме
    }

    [Fact]
    public void Selling_ContrabandInTheCore_SaysSo()
    {
        var (room, a, player) = Docked();
        player.Cargo.Add(Contraband, 2);

        room.Sell(a, Contraband);

        Assert.Equal(Protocol.NoGoodsNotice, a.Last<NoticeMsg>().Code);
        Assert.Equal(2, player.Cargo.Count(Contraband));
    }

    [Fact]
    public void Selling_MissionCargo_IsStillImpossible()
    {
        var (room, a, player) = Docked();
        player.Cargo.Reserved = 5;
        player.Cargo.Add(Ore, 1);

        room.Sell(a, null);

        // Груз задания — не предмет: он не продаётся и остаётся занимать место.
        Assert.Equal(5, player.Cargo.Reserved);
        Assert.Equal(5, player.Cargo.Used(Loot));
    }

    [Fact]
    public void WithoutAMarket_CargoSellsAtTheFlatPrice()
    {
        var room = new Room(
            TestBalance.Create(new CombatRules(SpawnJitter: 0), loot: Loot, shop: Shop),
            NullLogger.Instance);
        var a = new FakeConnection(1);
        room.Join(a, null, "Trader", null);
        var player = room.Pilot(a.Last<WelcomeMsg>().Id)!;
        player.Ship = new ShipState { X = 0, Y = 50 };
        room.Dock(a, true);
        player.Cargo.Add(Ore, 3);
        var before = player.Credits;

        room.Sell(a, Ore);

        // Как до M12: цена из loot.json, без склада и без спреда.
        Assert.Equal(before + 30, player.Credits);
    }

    [Fact]
    public void Stock_DriftsBackToNorm()
    {
        var (room, a, _) = Docked();
        var start = Quote(a, Food).Buy;
        room.BuyGoods(a, Food, 20);
        var moved = Quote(a, Food).Buy;

        // Полчаса торговли не было — запас почти вернулся к норме.
        for (var i = 0; i < SimConfig.TickRate * 1800; i++) room.Step();

        var settled = Quote(a, Food).Buy;
        Assert.True(moved > start);
        Assert.True(settled < moved, $"{settled} < {moved}");
        Assert.True(Math.Abs(settled - start) <= 1, $"{settled} ≈ {start}");
    }

    [Fact]
    public void ApplyBalance_KeepsHowFarStockDriftedFromNorm()
    {
        var (room, a, _) = Docked();
        room.BuyGoods(a, Food, 30);
        var moved = Quote(a, Food).Buy;

        // Перекрутили норму вдвое: цена не должна прыгнуть обратно, пилот только что вычистил склад.
        var bigger = new MarketRules(
            Goods: new Dictionary<string, MarketGood>
            {
                [Food] = new(Baseline: 200),
                [Ore] = new(Baseline: 200),
                [Contraband] = new(Baseline: 100, Illegal: ["core"]),
            },
            Stations: new Dictionary<string, MarketStation>
            {
                [GalaxyRules.DefaultSystem] = new(Produces: [Food], Consumes: [Ore]),
            })
            .Local(GalaxyRules.DefaultSystem, "core");
        room.ApplyBalance(TestBalance.Create(new CombatRules(SpawnJitter: 0), loot: Loot, shop: Shop, market: bigger));

        var after = Quote(a, Food).Buy;
        Assert.Equal(moved, after);
    }

    [Fact]
    public void ApplyBalance_DropsGoodsThatLeftTheBalance()
    {
        var (room, a, _) = Docked();

        var shrunk = new MarketRules(
            Goods: new Dictionary<string, MarketGood> { [Food] = new(Baseline: 100) },
            Stations: new Dictionary<string, MarketStation> { [GalaxyRules.DefaultSystem] = new(Produces: [Food]) })
            .Local(GalaxyRules.DefaultSystem, "core");
        room.ApplyBalance(TestBalance.Create(new CombatRules(SpawnJitter: 0), loot: Loot, shop: Shop, market: shrunk));

        Assert.Equal([Food], a.Last<MarketMsg>().Items.Select(i => i.Id));
    }

    [Fact]
    public void ApplyBalance_DropsCargoThatLeftTheBalance()
    {
        var (room, a, player) = Docked();
        player.Cargo.Add("ghost", 4);

        room.ApplyBalance(TestBalance.Create(new CombatRules(SpawnJitter: 0), loot: Loot, shop: Shop, market: Market()));

        // Иначе такой груз весит 0, стоит 0 и не продаётся — вечный невидимый хлам в трюме.
        Assert.Equal(0, player.Cargo.Count("ghost"));
    }

    [Fact]
    public void ApplyBalance_WithoutAMarket_FallsBackToFlatPrices()
    {
        var (room, a, player) = Docked();
        player.Cargo.Add(Ore, 3);
        var before = player.Credits;

        room.ApplyBalance(TestBalance.Create(new CombatRules(SpawnJitter: 0), loot: Loot, shop: Shop));
        room.Sell(a, Ore);

        Assert.Equal(before + 30, player.Credits);
    }
}
