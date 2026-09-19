using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Группы игроков (GDD §37) из shared/party.json.</summary>
/// <param name="MaxSize">Больше стольких пилотов в группе не бывает.</param>
/// <param name="InviteSeconds">Столько приглашение ждёт ответа.</param>
/// <param name="ShareRange">Награду за голову делят участники группы не дальше этого от сбитого.</param>
/// <param name="ShareBonus">Прибавка к награде за каждого, с кем её делят: вдвоём — ×1.2 на двоих.</param>
/// <param name="StatusSeconds">Так часто участники получают состояние группы: корпус, щит, где кто.</param>
public sealed record PartyRules(
    int MaxSize = 5,
    double InviteSeconds = 15,
    double ShareRange = 2000,
    double ShareBonus = 0.2,
    double StatusSeconds = 1)
{
    public const string File = "party.json";
    public const int MaxMaxSize = 10;

    public static readonly PartyRules Default = new();

    [JsonIgnore] public int InviteTicks => Math.Max(1, Combat.SecondsToTicks(InviteSeconds));
    [JsonIgnore] public int StatusTicks => Math.Max(1, Combat.SecondsToTicks(StatusSeconds));

    /// <summary>Доля каждого из n, кто делит награду bounty: вместе чуть больше, чем в одиночку, но каждому меньше.</summary>
    public int Share(int bounty, int n) =>
        n <= 1 ? bounty : (int)Math.Round(bounty * (1 + ShareBonus * (n - 1)) / n);

    public string? Validate()
    {
        if (MaxSize is < 2 or > MaxMaxSize) return $"maxSize must be within 2..{MaxMaxSize}";
        if (!(InviteSeconds > 0)) return "inviteSeconds must be positive";
        if (!(ShareRange >= 0)) return "shareRange must not be negative";
        if (!(ShareBonus >= 0)) return "shareBonus must not be negative";
        if (!(StatusSeconds > 0)) return "statusSeconds must be positive";
        return null;
    }

    public static bool TryParse(string json, out PartyRules rules, out string? error) =>
        Social.TryParse(json, Default, r => r.Validate(), out rules, out error);
}

/// <summary>Пираты волны вторжения: столько такого типа; уровень — плюс опасность системы.</summary>
public sealed record InvasionGroup(string Type, int Level = 1, int Count = 1)
{
    public string? Validate(IReadOnlyDictionary<string, NpcType> types)
    {
        if (Type is null || !types.TryGetValue(Type, out var type)) return $"unknown type '{Type}'";
        if (!type.IsPirate) return $"'{Type}' is not a pirate";
        if (Level is < 1 or > NpcSpawn.MaxLevel) return $"level must be within 1..{NpcSpawn.MaxLevel}";
        if (Count is < 1 or > NpcSpawn.MaxCount) return $"count must be within 1..{NpcSpawn.MaxCount}";
        return null;
    }

    /// <summary>Уровень в системе опасности danger: в опасной системе вторгаются пираты сильнее.</summary>
    public int LevelIn(int danger) => Math.Clamp(Level + Math.Max(0, danger - 1), 1, NpcSpawn.MaxLevel);
}

/// <summary>
/// «Вторжение пиратов» (GDD §38) из shared/invasion.json: раз в intervalMinutes в случайной системе со станцией.
/// Галактика объявляет его за announceSeconds, потом у станции идут волны; на все — durationSeconds.
/// Фонд делится по урону, нанесённому пиратам вторжения.
/// </summary>
/// <param name="IntervalMinutes">Между концом одного вторжения и анонсом следующего.</param>
/// <param name="FirstMinutes">Первое — через столько после запуска сервера.</param>
/// <param name="AnnounceSeconds">Анонс: за столько до начала.</param>
/// <param name="DurationSeconds">На отбой всех волн; не успели — вторжение провалено, пираты уходят.</param>
/// <param name="WaveGapSeconds">Пауза между зачищенной волной и следующей.</param>
/// <param name="Fund">Кредиты за отбитое вторжение — на всех, по урону.</param>
/// <param name="FailShare">Доля фонда, которую делят, если вторжение не отбито.</param>
/// <param name="MinShare">Меньше этого не получает никто, кто нанёс хоть какой-то урон.</param>
/// <param name="PointOffset">Пираты собираются на столько дальше станции, прочь от звезды: вне укрытия.</param>
/// <param name="Waves">Волны по порядку; пусто — вторжений нет.</param>
public sealed record InvasionRules(
    double IntervalMinutes = 20,
    double FirstMinutes = 5,
    double AnnounceSeconds = 120,
    double DurationSeconds = 300,
    double WaveGapSeconds = 20,
    int Fund = 3000,
    double FailShare = 0.3,
    int MinShare = 100,
    double PointOffset = 1400,
    IReadOnlyList<IReadOnlyList<InvasionGroup>>? Waves = null)
{
    public const string File = "invasion.json";

    /// <summary>Без волн: вторжений нет (тесты и когда файла нет).</summary>
    public static readonly InvasionRules None = new();

    [JsonIgnore] public IReadOnlyList<IReadOnlyList<InvasionGroup>> WaveList => Waves ?? [];
    [JsonIgnore] public bool Enabled => WaveList.Count > 0;
    [JsonIgnore] public long IntervalTicks => Math.Max(1, Combat.SecondsToTicks(IntervalMinutes * 60));
    [JsonIgnore] public long FirstTicks => Math.Max(1, Combat.SecondsToTicks(FirstMinutes * 60));
    [JsonIgnore] public long AnnounceTicks => Math.Max(1, Combat.SecondsToTicks(AnnounceSeconds));
    [JsonIgnore] public long DurationTicks => Math.Max(1, Combat.SecondsToTicks(DurationSeconds));
    [JsonIgnore] public long WaveGapTicks => Math.Max(1, Combat.SecondsToTicks(WaveGapSeconds));

    /// <summary>
    /// Доли по урону: fund (или его failShare при провале) пропорционально урону, но не меньше minShare.
    /// Нулевой урон — ничего. Порядок — как во входе.
    /// </summary>
    public IReadOnlyList<int> Shares(IReadOnlyList<double> damage, bool won)
    {
        var fund = won ? Fund : Fund * FailShare;
        var total = damage.Where(d => d > 0).Sum();
        return [.. damage.Select(d => d > 0 && total > 0 ? Math.Max(MinShare, (int)Math.Round(fund * d / total)) : 0)];
    }

    public string? Validate(IReadOnlyDictionary<string, NpcType> types)
    {
        if (!(IntervalMinutes > 0)) return "intervalMinutes must be positive";
        if (!(FirstMinutes >= 0)) return "firstMinutes must not be negative";
        if (!(AnnounceSeconds >= 0)) return "announceSeconds must not be negative";
        if (!(DurationSeconds > 0)) return "durationSeconds must be positive";
        if (!(WaveGapSeconds >= 0)) return "waveGapSeconds must not be negative";
        if (Fund < 0 || MinShare < 0) return "fund and minShare must not be negative";
        if (!(FailShare is >= 0 and <= 1)) return "failShare must be within 0..1";
        if (!(PointOffset >= 0) || !(PointOffset <= NpcRules.WorldLimit)) return $"pointOffset must be within 0..{NpcRules.WorldLimit}";
        for (var i = 0; i < WaveList.Count; i++)
        {
            if (WaveList[i] is not { Count: > 0 } wave) return $"waves[{i}] must not be empty";
            for (var j = 0; j < wave.Count; j++)
            {
                var problem = wave[j] is null ? "is null" : wave[j].Validate(types);
                if (problem is not null) return $"waves[{i}][{j}]: {problem}";
            }
        }
        return null;
    }

    public static bool TryParse(string json, IReadOnlyDictionary<string, NpcType> types, out InvasionRules rules, out string? error) =>
        Social.TryParse(json, None, r => r.Validate(types), out rules, out error);
}

internal static class Social
{
    public static bool TryParse<T>(string json, T fallback, Func<T, string?> validate, out T rules, out string? error) where T : class
    {
        rules = fallback;
        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(json, JsonCatalog.Options);
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
        error = validate(parsed);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
