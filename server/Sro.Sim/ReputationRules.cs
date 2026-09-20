using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Ступень отношения: с каких очков начинается, что делает с ценой и каким цветом рисуется.</summary>
/// <param name="From">Нижняя граница включительно. У первого уровня — минус предел шкалы.</param>
/// <param name="Price">Множитель цен снаряжения и ремонта: 0.9 — на 10 % дешевле.</param>
public sealed record RepLevel(string Id, string Name, double From, double Price = 1, string Color = "#8a93a6");

/// <summary>
/// Сколько очков стоит поступок (M13). Знак здесь же: добрые дела положительные, злые отрицательные,
/// чтобы правило читалось в одном месте, а не собиралось из знаков по коду.
/// </summary>
/// <param name="MissionPlace">Выполнил задание станции — ей самой.</param>
/// <param name="MissionSystem">…и системе, слабее: система в основном штрафная шкала.</param>
/// <param name="MissionAbandon">Бросил взятое задание — станции, выдавшей его.</param>
/// <param name="MissionFail">
/// Провалил взятое задание — месту, которое его ждало (M14). Дороже честного отказа: там пилот вернул работу,
/// здесь потерял конвой, звено или письмо.
/// </param>
/// <param name="PirateKill">Сбил пирата — системе.</param>
/// <param name="PirateHourly">Столько очков за головы система засчитывает за час, дальше — даром.</param>
/// <param name="SosHelp">Помог торговцу по SOS.</param>
/// <param name="InvasionMax">Столько получает тот, кто забрал весь фонд вторжения; остальным — по доле.</param>
/// <param name="TraderAttack">Открыл огонь по торговцу; раз за окно нарушителя, а не каждый тик.</param>
/// <param name="TraderKill">Добил торговца — системе.</param>
/// <param name="TraderPlace">…и станции, куда он вёз груз.</param>
/// <param name="RangerAttack">Открыл огонь по рейнджеру.</param>
/// <param name="RangerKill">Убил рейнджера.</param>
/// <param name="PlayerKill">Убил игрока там, где PvP выключен или ограничен; в free-системах — ничего.</param>
public sealed record RepEvents(
    double MissionPlace = 0,
    double MissionSystem = 0,
    double MissionAbandon = 0,
    double MissionFail = 0,
    double PirateKill = 0,
    double PirateHourly = 0,
    double SosHelp = 0,
    double InvasionMax = 0,
    double TraderAttack = 0,
    double TraderKill = 0,
    double TraderPlace = 0,
    double RangerAttack = 0,
    double RangerKill = 0,
    double PlayerKill = 0);

/// <summary>Что продают только своим: тиры снаряжения и поимённо корпуса.</summary>
/// <param name="Level">С какого уровня; null — гейта нет.</param>
public sealed record RepGate(string? Level = null, IReadOnlyList<int>? Tiers = null, IReadOnlyList<string>? Hulls = null)
{
    [JsonIgnore] public IReadOnlyList<int> TierList => Tiers ?? [];
    [JsonIgnore] public IReadOnlyList<string> HullList => Hulls ?? [];
}

/// <summary>Как отношение меняет доску заданий.</summary>
/// <param name="Offers">Уровень — сколько предложений; уровня нет в таблице — как в missions.json.</param>
/// <param name="EliteFrom">С какого уровня на доске появляется особый контракт; null — не появляется.</param>
/// <param name="EliteReward">Во сколько раз он дороже обычного.</param>
public sealed record RepMissions(
    IReadOnlyDictionary<string, int>? Offers = null,
    string? EliteFrom = null,
    double EliteReward = 1);

/// <summary>
/// Репутация из shared/reputation.json (GDD §35; M13): поступки пилота запоминаются местами, где он летает.
/// Здесь только правила и чистая математика — сами очки живут у пилота (Sro.Server.Game.Reputation),
/// потому что этот объект пересоздаётся при каждой правке в shared/.
///
/// Шкала одна и та же и для системы, и для станции: отличается не математика, а то, какие поступки куда
/// кладутся. Распад к нулю считается по часам, а не по тикам, — иначе репутация стояла бы на месте,
/// пока пилот оффлайн, и «исправиться со временем» не работало бы вовсе.
/// </summary>
/// <param name="Limit">Предел шкалы в обе стороны.</param>
/// <param name="Levels">Ступени от худшей к лучшей; пусто — репутации нет, как до M13.</param>
/// <param name="DecayPerDay">Столько очков репутация проходит к нулю за сутки реального времени.</param>
public sealed record ReputationRules(
    double Limit = 100,
    IReadOnlyList<RepLevel>? Levels = null,
    double DecayPerDay = 0,
    RepEvents? Events = null,
    RepGate? Gate = null,
    RepMissions? Missions = null)
{
    public const string File = "reputation.json";

    /// <summary>Файла нет: репутации не существует, всё продаётся всем — для тестов и старых сборок.</summary>
    public static readonly ReputationRules None = new(Levels: []);

    [JsonIgnore] public IReadOnlyList<RepLevel> LevelList => Levels ?? [];

    /// <summary>Работает ли репутация вообще. Без уровней шкала бессмысленна.</summary>
    [JsonIgnore] public bool Any => LevelList.Count > 0;

    [JsonIgnore] public RepEvents Event => Events ?? new RepEvents();

    /// <summary>Очки в пределах шкалы.</summary>
    public double Clamp(double value) => Math.Clamp(value, -Limit, Limit);

    /// <summary>Номер ступени по очкам; 0 — худшая. Без уровней — тоже 0.</summary>
    public int Index(double value)
    {
        var levels = LevelList;
        var index = 0;
        for (var i = 0; i < levels.Count; i++) if (value >= levels[i].From) index = i;
        return index;
    }

    /// <summary>Ступень по очкам; без уровней — нейтральная заглушка, которая ничего не меняет.</summary>
    public RepLevel Level(double value) => LevelList.Count > 0 ? LevelList[Index(value)] : new RepLevel("neutral", "Нейтрал", 0);

    /// <summary>Номер ступени по её id; −1 — такой нет.</summary>
    public int IndexOf(string? id)
    {
        var levels = LevelList;
        for (var i = 0; i < levels.Count; i++) if (levels[i].Id == id) return i;
        return -1;
    }

    /// <summary>Дотянул ли пилот до этой ступени. Неизвестная ступень никого не пускает.</summary>
    public bool AtLeast(double value, string? level)
    {
        var want = IndexOf(level);
        return want >= 0 && Index(value) >= want;
    }

    /// <summary>
    /// Репутация за столько суток без событий. Тянет к нулю с обеих сторон и через ноль не перескакивает.
    /// Отрицательные сутки (перевели часы назад) не делают ничего.
    /// </summary>
    public double Decay(double value, double days)
    {
        if (!(days > 0) || !(DecayPerDay > 0)) return value;
        var step = DecayPerDay * days;
        return value > 0 ? Math.Max(0, value - step) : Math.Min(0, value + step);
    }

    /// <summary>
    /// Цена с учётом отношения; округляется как в магазине, чтобы дока и сервер сошлись до кредита.
    /// Множитель ровно 1 (нейтрал, шкалы нет) возвращает цену как есть: округление — часть скидки,
    /// а не бесплатная добавка, иначе ремонт за 101 кр стоил бы 100 и без всякой репутации.
    /// </summary>
    public int Price(int basePrice, double value)
    {
        var mul = Level(value).Price;
        return mul == 1 ? basePrice : ShopRules.Round(basePrice * mul);
    }

    /// <summary>Продаётся ли это только своим: старший тир снаряжения или корпус из списка.</summary>
    public bool Gated(string id, bool hull)
    {
        if (Gate is not { Level: not null } gate) return false;
        return hull ? gate.HullList.Contains(id) : gate.TierList.Contains(Tiers.Split(id).Tier);
    }

    /// <summary>Пускают ли пилота с такими очками к этому товару.</summary>
    public bool Allows(double value, string id, bool hull) => !Gated(id, hull) || AtLeast(value, Gate?.Level);

    /// <summary>Сколько заданий на доске при таком отношении; не оговорено — сколько обычно.</summary>
    public int Offers(double value, int fallback)
    {
        var table = Missions?.Offers;
        if (table is null) return fallback;
        return table.TryGetValue(Level(value).Id, out var count) ? count : fallback;
    }

    /// <summary>Есть ли на доске особый контракт.</summary>
    public bool Elite(double value) => Missions?.EliteFrom is { } from && AtLeast(value, from);

    /// <summary>Во сколько раз особый контракт дороже обычного.</summary>
    [JsonIgnore] public double EliteReward => Missions?.EliteReward ?? 1;

    public string? Validate(IReadOnlyDictionary<string, HullParams>? hulls = null)
    {
        if (!(Limit > 0)) return "limit must be positive";
        if (DecayPerDay < 0) return "decayPerDay must not be negative";

        var levels = LevelList;
        if (levels.Count == 0) return null; // репутации нет — остальное проверять не на чем

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            if (string.IsNullOrWhiteSpace(level.Id)) return "a level has no id";
            if (!seen.Add(level.Id)) return $"duplicate level {level.Id}";
            if (!(level.Price > 0)) return $"level {level.Id}: price must be positive";
            if (i == 0)
            {
                if (level.From > -Limit) return $"the first level must start at -{Limit}";
            }
            else if (level.From <= levels[i - 1].From)
            {
                return $"level {level.Id}: from must be above {levels[i - 1].Id}";
            }
        }

        if (Gate is { } gate)
        {
            if (gate.Level is not null && IndexOf(gate.Level) < 0) return $"gate: unknown level {gate.Level}";
            if (hulls is not null)
                foreach (var id in gate.HullList) if (!hulls.ContainsKey(id)) return $"gate: unknown hull {id}";
            foreach (var tier in gate.TierList) if (tier < 1) return "gate: tiers must be at least 1";
        }

        if (Missions is { } missions)
        {
            if (missions.EliteFrom is not null && IndexOf(missions.EliteFrom) < 0)
                return $"missions: unknown level {missions.EliteFrom}";
            if (!(missions.EliteReward > 0)) return "missions: eliteReward must be positive";
            foreach (var (level, count) in missions.Offers ?? new Dictionary<string, int>())
            {
                if (IndexOf(level) < 0) return $"missions.offers: unknown level {level}";
                if (count < 0) return $"missions.offers: {level} must not be negative";
            }
        }

        return null;
    }

    public static bool TryParse(string json, IReadOnlyDictionary<string, HullParams>? hulls, out ReputationRules rules, out string? error)
    {
        rules = None;
        ReputationRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ReputationRules>(json, JsonCatalog.Options);
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
        error = parsed.Validate(hulls);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
