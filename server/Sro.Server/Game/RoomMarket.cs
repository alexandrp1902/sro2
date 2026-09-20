using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Комната — торговая площадка (M12): склады мест, живые цены и покупка товара в доке. Продажа груза
/// живёт в <c>Room.Sell</c> рядом с остальным трюмом, а здесь — всё, что знает про склад.
/// С M15 склад свой у каждого места: у станции и у каждого поселения. Где рынка нет, цены плоские, как до M12.
/// </summary>
public sealed partial class Room
{
    /// <summary>Склад каждого места по его ключу. Пусто — торговать в системе негде.</summary>
    private readonly Dictionary<string, Market> _markets = new(StringComparer.Ordinal);

    /// <summary>Когда считать возврат запасов к норме.</summary>
    private long _nextMarketTick;

    /// <summary>
    /// Склад места; null — такого места здесь нет. Пара «правила + склад» всегда берётся вместе:
    /// цена считается из них обоих, и перепутать склад одного места с правилами другого нельзя.
    /// </summary>
    private (MarketRules Rules, Market Stock)? MarketAt(string? key)
    {
        if (key is null || !_markets.TryGetValue(key, out var stock)) return null;
        var rules = Balance.MarketAt(key);
        // Событие спроса (M15.5) живёт по ключу места: у станции-соседки по системе цена не шелохнётся.
        return (_demand?.Apply(key, rules) ?? rules, stock);
    }

    /// <summary>Рынок того места, где стоит пилот; null — он в космосе или там не торгуют.</summary>
    private (MarketRules Rules, Market Stock)? MarketOf(Player player) => MarketAt(PlaceOf(player)?.Key);

    /// <summary>
    /// Место, чей склад двигают NPC-торговцы. Они летают от станции к вратам и обратно, поэтому поселения
    /// их поставками пока не живут (M15): их запас только возвращается к норме.
    /// </summary>
    private string? TraderPlace => Balance.DefaultPlace?.Key;

    /// <summary>Общие для системы правила рынка — шаг возврата к норме и прочие числа, не зависящие от места.</summary>
    private MarketRules MarketTiming => Balance.MarketAt(TraderPlace);

    /// <summary>Склады засеваются на норме: пока никто не торговал, цены честные.</summary>
    private void StartMarket()
    {
        _markets.Clear();
        foreach (var place in Balance.Places)
        {
            var rules = Balance.MarketAt(place.Key);
            if (!rules.Any) continue;
            var stock = new Market();
            stock.Seed(rules);
            _markets[place.Key] = stock;
        }
        _nextMarketTick = Tick + MarketTicks;
    }

    private int MarketTicks => Math.Max(1, Combat.SecondsToTicks(MarketTiming.TickSeconds));

    /// <summary>
    /// Торгуют ли этим грузом там, где стоит пилот. Без рынка место, как и до M12, принимает всё подряд
    /// по плоской цене — на этом стоят тесты с рукописным балансом и системы, где market.json ещё не описан.
    /// </summary>
    private bool Trades(Player player, string good) => MarketOf(player) is not { } m || !m.Rules.Any || m.Rules.Trades(good);

    /// <summary>Снять с рынка столько штук и заплатить пилоту; склад места при этом двигается.</summary>
    private int SellToStation(Player player, string good, int count)
    {
        if (MarketOf(player) is not { } m || !m.Rules.Any) return Balance.Loot.Price(good) * count;
        var credits = m.Stock.Sell(m.Rules, Balance.Loot, good, count);
        // Квота события убывает после сделки, а не внутри неё: множитель заморожен на сделку,
        // иначе клиент, считающий цену той же формулой, не сошёлся бы с сервером.
        if (CountDemand(player, good, count)) _host?.DemandFilled(this);
        return credits;
    }

    /// <summary>Запасы тянутся к норме; кто стоит в доке — видит, как цены расходятся обратно.</summary>
    private void StepMarket()
    {
        if (_markets.Count == 0 || Tick < _nextMarketTick) return;
        foreach (var (key, stock) in _markets)
        {
            var rules = Balance.MarketAt(key);
            if (rules.Any) stock.Step(rules, rules.TickSeconds);
        }
        _nextMarketTick = Tick + MarketTicks;
        BroadcastMarket();
    }

    /// <summary>Горячая правка market.json: у каждого места сохраняется не запас, а его отклонение от нормы.</summary>
    private void RebaseMarkets(Balance old, Balance balance)
    {
        foreach (var (key, stock) in _markets) stock.Rebase(old.MarketAt(key), balance.MarketAt(key));
        // Место могло появиться или исчезнуть вместе с правкой галактики — тогда склады пересобираются.
        var places = balance.Places.Where(p => balance.MarketAt(p.Key).Any).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        if (places.SetEquals(_markets.Keys)) return;
        foreach (var gone in _markets.Keys.Where(k => !places.Contains(k)).ToList()) _markets.Remove(gone);
        foreach (var added in places.Where(k => !_markets.ContainsKey(k)))
        {
            var stock = new Market();
            stock.Seed(balance.MarketAt(added));
            _markets[added] = stock;
        }
    }

    /// <summary>
    /// Цены мест этой системы для соседей (M12, по местам — M15): по ним торговцы в других доках
    /// рассказывают, где что берут. Пусто — торговать здесь негде.
    /// </summary>
    public IReadOnlyList<StationPrices> Prices()
    {
        if (_markets.Count == 0) return [];
        var loot = Balance.Loot;
        var list = new List<StationPrices>();
        foreach (var place in Balance.Places)
        {
            if (MarketAt(place.Key) is not { } m || !m.Rules.Any) continue;
            var prices = new List<MarketPrice>();
            foreach (var q in m.Stock.Quotes(m.Rules, loot))
                prices.Add(new MarketPrice(q.Id, q.Buy, q.Sell, q.Stock, q.Norm, m.Rules.Sells(q.Id)));
            if (prices.Count > 0) list.Add(new StationPrices(SystemId, place.Name, 0, prices, place.Key));
        }
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
        if (_host is null || PlaceOf(player) is not { } place) return;
        var here = Prices().FirstOrDefault(p => p.Place == place.Key);
        if (here is null || here.Prices.Count == 0) return;
        player.Rumours = Rumours.Pick(here, _host.MarketsExcept(SystemId), count: 1);
    }

    /// <summary>Цены изменились — обновить их у всех, кто сейчас в доке. В космосе рынок не нужен.</summary>
    private void BroadcastMarket()
    {
        foreach (var player in DockedPlayers()) SendMarket(player);
    }

    /// <summary>
    /// Витрина места — только тому, кто в доке (M15.6). В welcome едет магазин главного места системы,
    /// и на поселении он врёт: ассортимент, цены и подпись там свои.
    /// </summary>
    private void SendShop(Player player)
    {
        if (player.Connection is null || PlaceOf(player) is not { } place) return;
        player.Connection.Send(new ShopMsg(place.Key, Balance.ShopAt(place.Key)));
    }

    /// <summary>Цены — только тому, кто в доке: рынок у каждого места свой.</summary>
    private void SendMarket(Player player)
    {
        if (player.Connection is null) return;
        var items = new List<MarketItemDto>();
        if (MarketOf(player) is { } m)
        {
            foreach (var q in m.Stock.Quotes(m.Rules, Balance.Loot))
                items.Add(new MarketItemDto(q.Id, q.Buy, q.Sell, q.Stock, q.Norm));
        }
        var rumours = new List<RumourDto>(player.Rumours.Count);
        foreach (var r in player.Rumours)
            rumours.Add(new RumourDto(r.Kind, r.Good, r.System, r.Name, r.Hops, r.Price, r.Profit, r.Scarce));
        // Профиль места и спрос события едут сюда же: у клиента из welcome только правила главного места
        // системы, и на поселении он без этого считал бы цену по чужой витрине.
        var place = PlaceOf(player)?.Key;
        var local = MarketAt(place)?.Rules;
        player.Connection.Send(new MarketMsg(
            place ?? SystemId,
            items,
            rumours,
            local?.Station,
            _demand is { } demand && demand.PlaceKey == place
                ? new DemandQuoteDto(demand.Cause.Id, demand.Cause.Title, [.. demand.Cause.GoodList], Math.Round(demand.Mul, 3), demand.Left, demand.Quota)
                : null));
    }

    /// <summary>
    /// Чем гружён торговец (M12). Летит к станции — везёт то, чего ей не хватает: довезёт, и запас вырастет,
    /// собьют — поставка не придёт, и товар останется дорогим. Летит со станции — груз забрали уже сейчас.
    /// </summary>
    /// <param name="fromStation">Вылетел от станции (а не от врат).</param>
    private void LoadTrader(Trader trader, bool fromStation)
    {
        if (MarketAt(TraderPlace) is not { } m || !m.Rules.Any || m.Rules.Station is not { } profile || m.Rules.TraderUnits <= 0) return;
        var list = fromStation ? profile.ProduceList : profile.ConsumeList;
        var goods = list.Where(m.Rules.Trades).ToList();
        if (goods.Count == 0) return;

        trader.Good = goods[_ai.Next(goods.Count)];
        trader.Units = m.Rules.TraderUnits;
        if (!fromStation) return;
        // Загрузился перед вылетом: склад пустеет сразу, а не когда он долетит до врат.
        m.Stock.Take(trader.Good, trader.Units);
        BroadcastMarket();
    }

    /// <summary>Торговец довёз поставку: запас вырос, цена упала. Сбитый сюда не попадает.</summary>
    private void DeliverTrader(Trader trader)
    {
        if (trader.Good is not { } good || !trader.ToStation) return;
        if (MarketAt(TraderPlace) is { } m) m.Stock.Add(m.Rules, good, trader.Units);
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
        // Купить можно только то, что место делает само: чужой товар оно скупает, но не перепродаёт.
        if (MarketOf(player) is not { } m || !loot.StationUnload || !m.Rules.Any || !m.Rules.Sells(item) || !loot.ItemMap.ContainsKey(item))
        {
            connection.Send(new NoticeMsg(Protocol.NoGoodsNotice));
            return;
        }
        var market = m.Rules;

        var want = Math.Min(count, m.Stock.Available(item));
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
        want = m.Stock.Affordable(market, loot, item, Math.Min(want, fits), player.Credits);
        if (want <= 0)
        {
            connection.Send(new NoticeMsg(Protocol.NoCreditsNotice));
            return;
        }

        var cost = m.Stock.Buy(market, loot, item, want);
        player.Credits -= cost;
        player.Cargo.Add(item, want);
        SendCargo(player);
        BroadcastMarket();
        Save(player);
        // Обучение торговца (M15.5): «купить товар» засчитывается здесь — как «продать» в Sell.
        Advance(player, MissionRules.BuyStep);
        _log.LogInformation("Player {Id} bought {Count} {Item} for {Credits} credits", player.Id, want, item, cost);
    }
}
