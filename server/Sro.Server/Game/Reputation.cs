using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Репутация пилота (M13): очки по системам и станциям. Живёт у игрока и едет с ним между комнатами —
/// <see cref="Galaxy"/> переносит сам объект <see cref="Player"/>, а не создаёт его заново.
///
/// Распад к нулю считается лениво, по реальным часам, а не по тикам: пилот бывает оффлайн, сервер
/// перезапускают, и тиковый распад означал бы, что «исправиться со временем» работает только у тех,
/// кто всё это время играл. Вместо этого хранится момент, до которого очки уже приведены, и любое
/// чтение сперва догоняет их до «сейчас».
/// </summary>
public sealed class Reputation
{
    /// <summary>Ключ отношения властей системы.</summary>
    public static string System(string id) => PlaceKey.System(id);

    /// <summary>Ключ отдельной станции.</summary>
    public static string Station(string id) => PlaceKey.Station(id);

    /// <summary>Ключ поселения на планете (M15) — такое же «место», как станция.</summary>
    public static string Planet(string id) => PlaceKey.Planet(id);

    /// <summary>Разбор ключа: («sys», «vega»). Чужой ключ — («», ключ целиком).</summary>
    public static (string Kind, string Id) Split(string key) => PlaceKey.Split(key);

    private const int SecondsPerDay = 24 * 60 * 60;

    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Потолок за час по ключу: в какой час он считался и сколько за этот час уже набежало.
    /// На диск не пишется: потолок — защита от фарма в одну сессию, а не часть состояния пилота.
    /// </summary>
    private readonly Dictionary<string, (long Hour, double Gained)> _caps = new(StringComparer.Ordinal);

    private readonly List<string> _empty = [];

    /// <summary>До этого момента очки уже приведены к «сейчас», unix-секунды.</summary>
    public long At { get; private set; }

    /// <summary>Все ненулевые очки — для отправки клиенту и для сохранения.</summary>
    public IReadOnlyDictionary<string, double> Values => _values;

    /// <summary>Профиль с диска. Очки в нём записаны на момент <paramref name="at"/> — распад догоняет их здесь.</summary>
    public void Load(IReadOnlyDictionary<string, double>? saved, long? at, long now, ReputationRules rules)
    {
        _values.Clear();
        _caps.Clear();
        At = at ?? now;
        if (saved is not null)
        {
            foreach (var (key, value) in saved)
            {
                if (value != 0) _values[key] = rules.Clamp(value);
            }
        }
        Touch(now, rules);
    }

    /// <summary>Догнать очки до «сейчас»: распад за прошедшее время, нули — вон из словаря.</summary>
    public void Touch(long now, ReputationRules rules)
    {
        if (now <= At)
        {
            // Часы перевели назад: время не идёт вспять, просто считаем отсюда.
            At = Math.Min(At, now);
            return;
        }
        var days = (now - At) / (double)SecondsPerDay;
        At = now;
        if (_values.Count == 0) return;
        _empty.Clear();
        foreach (var key in _values.Keys)
        {
            var value = rules.Decay(_values[key], days);
            if (value == 0) _empty.Add(key);
            else _values[key] = value;
        }
        foreach (var key in _empty) _values.Remove(key);
    }

    /// <summary>Очки по ключу с поправкой на прошедшее время.</summary>
    public double Value(string key, long now, ReputationRules rules)
    {
        Touch(now, rules);
        return _values.GetValueOrDefault(key);
    }

    /// <summary>
    /// Отношение региона — среднее по его системам, а не отдельная шкала. Считается по **всем** системам
    /// региона: в незнакомых пилот никто, и они тянут среднее вниз. Иначе один визит делал бы «героем региона».
    /// </summary>
    public double Region(GalaxyRules galaxy, string? region, long now, ReputationRules rules)
    {
        if (region is null) return 0;
        Touch(now, rules);
        var sum = 0.0;
        var count = 0;
        foreach (var (id, system) in galaxy.SystemMap)
        {
            if (system.Region != region) continue;
            sum += _values.GetValueOrDefault(System(id));
            count++;
        }
        return count > 0 ? sum / count : 0;
    }

    /// <summary>
    /// Добавить очки. <paramref name="hourlyCap"/> — сколько этот ключ принимает за час: сверх того
    /// поступок засчитывается, но очков не приносит.
    /// </summary>
    /// <returns>Сколько на самом деле легло; 0 — потолок выбран или шкала уже на пределе.</returns>
    public double Add(string key, double delta, long now, ReputationRules rules, double? hourlyCap = null)
    {
        if (!rules.Any || delta == 0) return 0;
        Touch(now, rules);

        if (hourlyCap is { } cap && delta > 0)
        {
            if (cap <= 0) return 0;
            var hour = now / 3600;
            var bucket = _caps.GetValueOrDefault(key);
            if (bucket.Hour != hour) bucket = (hour, 0);
            var room = cap - bucket.Gained;
            if (room <= 0)
            {
                _caps[key] = bucket;
                return 0;
            }
            delta = Math.Min(delta, room);
            _caps[key] = (hour, bucket.Gained + delta);
        }

        var before = _values.GetValueOrDefault(key);
        var after = rules.Clamp(before + delta);
        if (after == 0) _values.Remove(key);
        else _values[key] = after;
        return after - before;
    }

    /// <summary>Место пропало из баланса — его очки больше ни к чему не относятся.</summary>
    public void Scrub(GalaxyRules galaxy)
    {
        if (_values.Count == 0) return;
        _empty.Clear();
        // Планеты живут не по id системы, поэтому «такое поселение ещё есть» — отдельный вопрос (M15).
        var planets = galaxy.SystemMap.Values.SelectMany(s => s.Settled).Select(p => p.Id!).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _values.Keys)
        {
            var (kind, id) = Split(key);
            var gone = kind switch
            {
                PlaceKey.SystemKind or PlaceKey.StationKind => galaxy.System(id) is null,
                PlaceKey.PlanetKind => !planets.Contains(id),
                _ => false,
            };
            if (gone) _empty.Add(key);
        }
        foreach (var key in _empty) _values.Remove(key);
    }
}
