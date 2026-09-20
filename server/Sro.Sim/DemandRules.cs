using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Повод для события спроса (M15.5): восстание, эпидемия, блокада — и что при этом просят.</summary>
/// <param name="Id">Ключ повода; по нему клиент ничего не решает, он нужен логу и тестам.</param>
/// <param name="Title">Как называется в ленте и в доке.</param>
/// <param name="Goods">Что просят. Товары переводятся в «скупает» на срок события, каким бы ни было место.</param>
public sealed record DemandCase(string Id = "", string Title = "", IReadOnlyList<string>? Goods = null)
{
    [JsonIgnore] public IReadOnlyList<string> GoodList => Goods ?? [];
}

/// <summary>
/// События спроса (M15.5) из shared/demand.json: на месте в красной зоне на срок вспыхивает нужда
/// в нескольких товарах, и цена на них подскакивает.
///
/// Суть не в доставке, а в том, что туда слетятся все: гружёные корабли сходятся в одну точку, где PvP
/// свободен и убийство не стоит репутации, — и товар можно не везти, а отнять у того, кто уже довёз его
/// до половины пути.
///
/// Спрос конечен: нужно столько-то единиц, а не «дорого, пока идёт событие». Иначе это бесконечный кран
/// денег и спешить некуда. С квотой первые довёзшие снимают сливки, цена на глазах оседает, и опоздавшему
/// достаётся обычный рейс.
///
/// Событие живёт только в памяти, как вторжение и как весь рынок: перезапуск сервера его гасит,
/// и цены возвращаются к норме. Единственный, кто пишет на диск, — AccountStore, второго заводить незачем.
/// </summary>
/// <param name="Enabled">false — событий нет вовсе.</param>
/// <param name="FirstMinutes">Первое событие — через столько после запуска сервера.</param>
/// <param name="IntervalMinutes">Между концом одного и анонсом следующего.</param>
/// <param name="AnnounceSeconds">Объявлено на всю галактику, но приёмка ещё не открыта: время долететь.</param>
/// <param name="DurationSeconds">Сколько идёт приёмка.</param>
/// <param name="Quota">Сколько единиц всего примут; дальше событие кончается досрочно.</param>
/// <param name="Mul">Во сколько раз дороже обычного в начале…</param>
/// <param name="MulEnd">…и когда квота почти выбрана. Не единица: иначе последний трюм везти незачем.</param>
/// <param name="CrashShare">До какой доли нормы обрушивается запас на старте: нужда должна быть видна сразу.</param>
/// <param name="PerPilot">Потолок зачёта на одного пилота, единиц; 0 — без потолка.</param>
/// <param name="Cases">Поводы; пусто — событий нет.</param>
public sealed record DemandRules(
    bool Enabled = true,
    double FirstMinutes = 8,
    double IntervalMinutes = 40,
    double AnnounceSeconds = 90,
    double DurationSeconds = 1200,
    int Quota = 180,
    double Mul = 4.5,
    double MulEnd = 2,
    double CrashShare = 0.15,
    int PerPilot = 0,
    IReadOnlyList<DemandCase>? Cases = null)
{
    public const string File = "demand.json";

    /// <summary>Событий нет: сервер без demand.json ведёт себя как до M15.5.</summary>
    public static readonly DemandRules None = new(Enabled: false, Cases: null);

    [JsonIgnore] public IReadOnlyList<DemandCase> CaseList => Cases ?? [];

    [JsonIgnore] public bool Any => Enabled && CaseList.Count > 0 && Quota > 0;

    [JsonIgnore] public long FirstTicks => (long)Math.Round(FirstMinutes * 60 * SimConfig.TickRate);

    [JsonIgnore] public long IntervalTicks => (long)Math.Round(IntervalMinutes * 60 * SimConfig.TickRate);

    [JsonIgnore] public long AnnounceTicks => (long)Math.Round(AnnounceSeconds * SimConfig.TickRate);

    [JsonIgnore] public long DurationTicks => (long)Math.Round(DurationSeconds * SimConfig.TickRate);

    /// <summary>
    /// Множитель цены при таком остатке квоты: от <see cref="Mul"/> в начале к <see cref="MulEnd"/> к концу.
    /// Это и есть «первые довёзшие снимают сливки»: чем больше уже привезли, тем меньше платят.
    /// </summary>
    public double Multiplier(int left, int quota) =>
        quota <= 0 ? 1 : MulEnd + (Mul - MulEnd) * Math.Clamp(left / (double)quota, 0, 1);

    /// <param name="items">Груз из loot.json: каждый просимый товар должен быть там.</param>
    public string? Validate(IReadOnlyDictionary<string, LootItem>? items)
    {
        if (!(FirstMinutes >= 0) || !(IntervalMinutes >= 0)) return "firstMinutes and intervalMinutes must not be negative";
        if (!(AnnounceSeconds >= 0)) return "announceSeconds must not be negative";
        if (!(DurationSeconds > 0)) return "durationSeconds must be positive";
        if (Quota < 0) return "quota must not be negative";
        if (!(MulEnd >= 1)) return "mulEnd must be at least 1";
        if (!(Mul >= MulEnd)) return "mul must not be below mulEnd";
        if (!(CrashShare is >= 0 and <= 1)) return "crashShare must be within 0..1";
        if (PerPilot < 0) return "perPilot must not be negative";
        if (Enabled && CaseList.Count == 0) return "no cases";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < CaseList.Count; i++)
        {
            var item = CaseList[i];
            if (item is null) return $"cases[{i}]: is null";
            if (string.IsNullOrWhiteSpace(item.Id)) return $"cases[{i}]: id is empty";
            if (!seen.Add(item.Id)) return $"cases[{i}]: duplicate id '{item.Id}'";
            if (string.IsNullOrWhiteSpace(item.Title)) return $"cases[{i}]: title is empty";
            if (item.GoodList.Count == 0) return $"cases[{i}]: no goods";
            foreach (var good in item.GoodList)
                if (items is null || !items.ContainsKey(good)) return $"cases[{i}]: unknown good {good}";
        }
        return null;
    }

    public static bool TryParse(string json, IReadOnlyDictionary<string, LootItem>? items, out DemandRules rules, out string? error) =>
        Social.TryParse(json, None, r => r.Validate(items), out rules, out error);
}
