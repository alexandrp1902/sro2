using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Комната — торговая площадка (M12): склад станции, живые цены и покупка товара в доке. Продажа груза
/// живёт в <c>Room.Sell</c> рядом с остальным трюмом, а здесь — всё, что знает про склад.
/// В системе без станции рынка нет: <see cref="_market"/> пуст, цены плоские, как до M12.
/// </summary>
public sealed partial class Room
{
    private readonly Market _market = new();

    /// <summary>Когда считать возврат запасов к норме.</summary>
    private long _nextMarketTick;

    /// <summary>Правила рынка этой станции; без станции — «рынка нет».</summary>
    private MarketRules MarketRules => Balance.Market;

    /// <summary>Склад засевается на норме: пока никто не торговал, цены честные.</summary>
    private void StartMarket()
    {
        _market.Seed(MarketRules);
        _nextMarketTick = Tick + MarketTicks;
    }

    private int MarketTicks => Math.Max(1, Combat.SecondsToTicks(MarketRules.TickSeconds));

    /// <summary>
    /// Торгуют ли здесь этим грузом. Без рынка станция, как и до M12, принимает всё подряд по плоской цене —
    /// на этом стоят тесты с рукописным балансом и системы, где market.json ещё не описан.
    /// </summary>
    private bool Trades(string good) => !MarketRules.Any || MarketRules.Trades(good);

    /// <summary>Снять с рынка столько штук и заплатить пилоту; склад при этом двигается.</summary>
    private int SellToStation(string good, int count) =>
        MarketRules.Any
            ? _market.Sell(MarketRules, Balance.Loot, good, count)
            : Balance.Loot.Price(good) * count;

    /// <summary>Запасы тянутся к норме; кто стоит в доке — видит, как цены расходятся обратно.</summary>
    private void StepMarket()
    {
        if (!MarketRules.Any || Tick < _nextMarketTick) return;
        _market.Step(MarketRules, MarketRules.TickSeconds);
        _nextMarketTick = Tick + MarketTicks;
        BroadcastMarket();
    }

    /// <summary>
    /// Цены этой станции для соседей (M12): по ним торговцы в других доках рассказывают, где что берут.
    /// Пусто — станции или рынка здесь нет.
    /// </summary>
    public IReadOnlyList<MarketPrice> Prices()
    {
        var market = MarketRules;
        if (!market.Any) return [];
        var loot = Balance.Loot;
        var list = new List<MarketPrice>();
        foreach (var q in _market.Quotes(market, loot))
            list.Add(new MarketPrice(q.Id, q.Buy, q.Sell, q.Stock, q.Norm, market.Sells(q.Id)));
        return list;
    }

    /// <summary>
    /// О чём здесь судачат. Торговец рассказывает одну историю — ту, что выгоднее прочих: список из трёх
    /// читался как прайс-лист, а не как разговор. Считается один раз, на стыковке: слух — это то, что пилот
    /// услышал, а не строка, которая переписывается после каждой его же сделки.
    /// </summary>
    private void MakeRumours(Player player)
    {
        player.Rumours = [];
        var market = MarketRules;
        if (!market.Any || _host is null) return;
        var here = new StationPrices(SystemId, Balance.SystemDef.Name, 0, Prices());
        if (here.Prices.Count == 0) return;
        player.Rumours = Rumours.Pick(here, _host.MarketsExcept(SystemId), count: 1);
    }

    /// <summary>Цены изменились — обновить их у всех, кто сейчас в доке. В космосе рынок не нужен.</summary>
    private void BroadcastMarket()
    {
        foreach (var player in DockedPlayers()) SendMarket(player);
    }

    /// <summary>Цены станции — только тому, кто в доке: рынок у каждой станции свой.</summary>
    private void SendMarket(Player player)
    {
        if (player.Connection is null) return;
        var quotes = _market.Quotes(MarketRules, Balance.Loot);
        var items = new List<MarketItemDto>(quotes.Count);
        foreach (var q in quotes) items.Add(new MarketItemDto(q.Id, q.Buy, q.Sell, q.Stock, q.Norm));
        var rumours = new List<RumourDto>(player.Rumours.Count);
        foreach (var r in player.Rumours)
            rumours.Add(new RumourDto(r.Kind, r.Good, r.System, r.Name, r.Hops, r.Price, r.Profit, r.Scarce));
        player.Connection.Send(new MarketMsg(SystemId, items, rumours));
    }

    /// <summary>
    /// Чем гружён торговец (M12). Летит к станции — везёт то, чего ей не хватает: довезёт, и запас вырастет,
    /// собьют — поставка не придёт, и товар останется дорогим. Летит со станции — груз забрали уже сейчас.
    /// </summary>
    /// <param name="fromStation">Вылетел от станции (а не от врат).</param>
    private void LoadTrader(Trader trader, bool fromStation)
    {
        var market = MarketRules;
        if (!market.Any || market.Station is not { } profile || market.TraderUnits <= 0) return;
        var list = fromStation ? profile.ProduceList : profile.ConsumeList;
        var goods = list.Where(market.Trades).ToList();
        if (goods.Count == 0) return;

        trader.Good = goods[_ai.Next(goods.Count)];
        trader.Units = market.TraderUnits;
        if (!fromStation) return;
        // Загрузился перед вылетом: склад пустеет сразу, а не когда он долетит до врат.
        _market.Take(trader.Good, trader.Units);
        BroadcastMarket();
    }

    /// <summary>Торговец довёз поставку: запас вырос, цена упала. Сбитый сюда не попадает.</summary>
    private void DeliverTrader(Trader trader)
    {
        if (trader.Good is not { } good || !trader.ToStation) return;
        _market.Add(MarketRules, good, trader.Units);
        trader.Good = null;
        BroadcastMarket();
    }

    /// <summary>
    /// Купить товар в доке (M12). Количество урезается до того, что есть на складе, что по карману и что
    /// влезет в трюм: пилот жмёт «Макс», а сколько на самом деле — решает сервер.
    /// </summary>
    public void BuyGoods(IClientConnection connection, string? item, int count)
    {
        if (item is null || count <= 0 || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        if (!player.Docked)
        {
            connection.Send(new NoticeMsg(Protocol.TooFarNotice));
            return;
        }
        var loot = Balance.Loot;
        var market = MarketRules;
        // Купить можно только то, что станция делает сама: чужой товар она скупает, но не перепродаёт.
        if (!loot.StationUnload || !market.Any || !market.Sells(item) || !loot.ItemMap.ContainsKey(item))
        {
            connection.Send(new NoticeMsg(Protocol.NoGoodsNotice));
            return;
        }

        var want = Math.Min(count, _market.Available(item));
        if (want <= 0)
        {
            connection.Send(new NoticeMsg(Protocol.NoStockNotice));
            return;
        }
        // Сколько влезет в трюм: объём у товаров разный, и «макс» у тяжёлого меньше, чем кажется.
        var volume = loot.Volume(item);
        var free = player.Effective(Balance).Cargo - player.Cargo.Used(loot);
        var fits = volume > 0 ? (int)Math.Floor((free + 1e-9) / volume) : want;
        if (fits <= 0)
        {
            WarnCargoFull(player);
            return;
        }
        // Цена шагает по-штучно, поэтому «на сколько хватит» считается тем же шагом, а не делением.
        want = _market.Affordable(market, loot, item, Math.Min(want, fits), player.Credits);
        if (want <= 0)
        {
            connection.Send(new NoticeMsg(Protocol.NoCreditsNotice));
            return;
        }

        var cost = _market.Buy(market, loot, item, want);
        player.Credits -= cost;
        player.Cargo.Add(item, want);
        SendCargo(player);
        BroadcastMarket();
        Save(player);
        _log.LogInformation("Player {Id} bought {Count} {Item} for {Credits} credits", player.Id, want, item, cost);
    }
}
