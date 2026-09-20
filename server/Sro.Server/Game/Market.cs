using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>Строка рынка для дока: что почём и сколько на складе.</summary>
/// <param name="Buy">Сколько пилот платит станции за штуку.</param>
/// <param name="Sell">Сколько пилот получает от станции за штуку.</param>
/// <param name="Stock">Запас станции, штук.</param>
/// <param name="Norm">Равновесный запас: по нему видно «мало» или «много».</param>
public readonly record struct MarketQuote(string Id, int Buy, int Sell, int Stock, int Norm);

/// <summary>
/// Склад станции (M12): сколько чего лежит прямо сейчас. Цену считает <see cref="MarketRules"/>, здесь —
/// только запас и его движение. Запас живёт в комнате, а не в правилах, потому что <c>Room.Balance</c>
/// пересоздаётся целиком при каждой правке в shared/.
///
/// На диск не пишется намеренно. Запас возвращается к норме за минуты, то есть спроектирован
/// самовосстанавливающимся: после перезапуска сервера рынок просто стоит на честной цене, и это лучшее
/// стартовое состояние — никто не попадает ни в чужое затоваривание, ни в яму, которую не сам вырыл.
/// Единственный, кто пишет на диск, — <see cref="Accounts.AccountStore"/>, и второй такой заводить не за чем.
/// Понадобится сохранять — добавить Snapshot/Restore рядом с <see cref="Seed"/>, остального это не тронет.
///
/// Всё зовётся с потока тика (сетевой поток только кладёт команду в очередь), поэтому замков здесь нет.
/// </summary>
public sealed class Market
{
    private readonly Dictionary<string, double> _stock = new(StringComparer.Ordinal);

    /// <summary>Торгуют ли здесь хоть чем-нибудь.</summary>
    public bool Any => _stock.Count > 0;

    /// <summary>Запас товара, штук; 0 — таким здесь не торгуют или всё разобрали.</summary>
    public double Stock(string good) => _stock.GetValueOrDefault(good);

    /// <summary>Новый склад: каждый товар на своей норме, то есть по честной цене.</summary>
    public void Seed(MarketRules rules)
    {
        _stock.Clear();
        if (!rules.Any) return;
        foreach (var good in rules.Sold) _stock[good] = rules.Norm(good);
    }

    /// <summary>
    /// Обрушить запас товара до доли нормы (M15.5): событие спроса должно быть видно в ценах сразу,
    /// а не через полчаса подвоза. Норма здесь уже событийная — товар на срок события стал «скупаемым».
    /// </summary>
    public void Crash(MarketRules rules, string good, double share)
    {
        if (!rules.Any || !rules.Trades(good)) return;
        _stock[good] = Math.Max(0, rules.Norm(good) * Math.Clamp(share, 0, 1));
    }

    /// <summary>
    /// Баланс поправили на диске. Сохраняется не абсолютный запас, а его отклонение от нормы: иначе правка
    /// baseline телепортировала бы цены посреди игры. Так же, как пилотам сохраняются доли корпуса и щита.
    /// </summary>
    public void Rebase(MarketRules old, MarketRules now)
    {
        if (!now.Any)
        {
            _stock.Clear();
            return;
        }
        var before = new Dictionary<string, double>(_stock, StringComparer.Ordinal);
        _stock.Clear();
        foreach (var good in now.Sold)
        {
            var norm = now.Norm(good);
            if (!before.TryGetValue(good, out var was))
            {
                _stock[good] = norm; // товар появился — начинаем с честной цены
                continue;
            }
            var oldNorm = old.Norm(good);
            _stock[good] = now.Clamp(good, oldNorm > 0 ? was / oldNorm * norm : norm);
        }
    }

    /// <summary>Цены для дока, по порядку id: порядок строк на экране должен быть устойчивым.</summary>
    public IReadOnlyList<MarketQuote> Quotes(MarketRules rules, LootRules loot)
    {
        if (!rules.Any) return [];
        var quotes = new List<MarketQuote>(_stock.Count);
        foreach (var good in rules.Sold)
        {
            if (!_stock.TryGetValue(good, out var stock)) continue;
            var price = loot.Price(good);
            quotes.Add(new MarketQuote(
                good,
                rules.BuyPrice(good, price, stock),
                rules.SellPrice(good, price, stock),
                (int)Math.Round(stock),
                (int)Math.Round(rules.Norm(good))));
        }
        return quotes;
    }

    /// <summary>Сколько станция готова продать прямо сейчас: дробную штуку не продаём.</summary>
    public int Available(string good) => (int)Math.Floor(Stock(good));

    /// <summary>
    /// Пилот покупает у станции. Считает цену по-штучно — каждая следующая дороже предыдущей.
    /// Вызывающий сам ограничивает count трюмом и кредитами.
    /// </summary>
    /// <returns>Сколько всего кредитов; запас уменьшается.</returns>
    public int Buy(MarketRules rules, LootRules loot, string good, int count)
    {
        if (count <= 0 || !_stock.TryGetValue(good, out var stock)) return 0;
        var (credits, after) = rules.Trade(good, loot.Price(good), stock, count, buying: true);
        _stock[good] = after;
        return credits;
    }

    /// <summary>Пилот продаёт станции; запас растёт, цена падает.</summary>
    /// <returns>Сколько всего кредитов.</returns>
    public int Sell(MarketRules rules, LootRules loot, string good, int count)
    {
        if (count <= 0 || !_stock.TryGetValue(good, out var stock)) return 0;
        var (credits, after) = rules.Trade(good, loot.Price(good), stock, count, buying: false);
        _stock[good] = after;
        return credits;
    }

    /// <summary>Сколько пилот выручит за столько штук, не трогая склад: для подсказки и подсчёта «продать всё».</summary>
    public int Quote(MarketRules rules, LootRules loot, string good, int count, bool buying)
    {
        if (count <= 0 || !_stock.TryGetValue(good, out var stock)) return 0;
        return rules.Trade(good, loot.Price(good), stock, count, buying).Credits;
    }

    /// <summary>На сколько штук из max хватит кредитов по здешним ценам.</summary>
    public int Affordable(MarketRules rules, LootRules loot, string good, int max, int credits)
    {
        if (max <= 0 || !_stock.TryGetValue(good, out var stock)) return 0;
        return rules.Affordable(good, loot.Price(good), stock, max, credits);
    }

    /// <summary>NPC-торговец увёз товар со склада: запас упал, цена подросла.</summary>
    public void Take(string good, int units)
    {
        if (units <= 0 || !_stock.TryGetValue(good, out var stock)) return;
        _stock[good] = Math.Max(0, stock - units);
    }

    /// <summary>NPC-торговец довёз товар: запас вырос, цена упала. Сбили по пути — этого не случится.</summary>
    public void Add(MarketRules rules, string good, int units)
    {
        if (units <= 0 || !_stock.TryGetValue(good, out var stock)) return;
        _stock[good] = rules.Clamp(good, stock + units);
    }

    /// <summary>Запас тянется к норме: завоз и спрос со временем сглаживают любую сделку.</summary>
    public void Step(MarketRules rules, double seconds)
    {
        if (!rules.Any) return;
        foreach (var good in _stock.Keys.ToList()) _stock[good] = rules.Regress(good, _stock[good], seconds);
    }
}
