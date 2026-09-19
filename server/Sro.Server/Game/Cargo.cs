using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Грузовой отсек игрока (GDD §21). Живёт на <see cref="Player"/>, а не на корабле: по §24 груз не теряется
/// ни при уничтожении, ни при обрыве связи, а ёмкость берётся у корпуса (боевой документ §46).
/// </summary>
public sealed class Cargo
{
    private readonly Dictionary<string, int> _items = new(StringComparer.Ordinal);

    /// <summary>Что лежит: предмет — количество.</summary>
    public IReadOnlyDictionary<string, int> Items => _items;

    public bool IsEmpty => _items.Count == 0;

    /// <summary>
    /// Место под груз доставки (GDD §36): единица — единица объёма. Это не предмет — его не продать, не выбросить
    /// и не потерять; освобождается, когда задание сдано или брошено.
    /// </summary>
    public int Reserved;

    /// <summary>Занятый объём, включая груз доставки.</summary>
    public double Used(LootRules loot)
    {
        var used = (double)Reserved;
        foreach (var (item, count) in _items) used += loot.Volume(item) * count;
        return used;
    }

    /// <summary>
    /// Влезает ли стопка целиком. Частичный подбор не делаем: дробление стопки — ещё одно изменяемое
    /// состояние и мигание снапшота ради выигрыша в один-два предмета.
    /// </summary>
    public bool Fits(string item, int count, double capacity, LootRules loot) =>
        Used(loot) + loot.Volume(item) * count <= capacity;

    public void Add(string item, int count) => _items[item] = _items.GetValueOrDefault(item) + count;

    /// <summary>Сколько кредитов дадут за весь груз на станции.</summary>
    public int Price(LootRules loot)
    {
        var total = 0;
        foreach (var (item, count) in _items) total += loot.Price(item) * count;
        return total;
    }

    /// <summary>Достаёт из трюма весь такой груз.</summary>
    /// <returns>Сколько за него дают кредитов; 0 — такого груза нет.</returns>
    public int Take(string item, LootRules loot)
    {
        if (!_items.Remove(item, out var count)) return 0;
        return loot.Price(item) * count;
    }

    /// <summary>Убирает count штук — без денег: так сдают задание «собрать».</summary>
    /// <returns>false — столько нет, трюм не тронут.</returns>
    public bool Remove(string item, int count)
    {
        var have = _items.GetValueOrDefault(item);
        if (count <= 0 || have < count) return false;
        if (have == count) _items.Remove(item);
        else _items[item] = have - count;
        return true;
    }

    /// <summary>Продано всё: груз доставки остаётся на месте.</summary>
    public void Clear() => _items.Clear();
}
