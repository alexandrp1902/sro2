using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>
/// Путь пилота (M15.5): с чего начинается новый аккаунт — корабль, кредиты, груз, место и первое отношение мира.
///
/// Это НЕ класс: ни запретов, ни бонусов дальше старта. Торговец волен воевать, рейнджер — торговать;
/// они просто входят в игру с разных концов. Всё, что путь решает, он решает один раз, в <c>JoinAccount</c>.
/// </summary>
/// <param name="Name">Как называется на экране входа.</param>
/// <param name="Hint">Строка под названием: чем этот путь отличается.</param>
/// <param name="Enabled">false — карточка показана серой и сервер такой путь отклоняет (пират до своего этапа).</param>
/// <param name="System">Система старта; null — стартовая система галактики.</param>
/// <param name="Place">Место старта, «st:sol» или «pl:terra»; null — место системы по умолчанию.</param>
/// <param name="Hull">Корпус; null — <see cref="SimConfig.DefaultHull"/>.</param>
/// <param name="Weapons">Пушки по слотам; null — стартовая пушка в первом слоте.</param>
/// <param name="Modules">Модули по видам слотов; null — стартовый комплект (<see cref="Fitting.Starter"/>).</param>
/// <param name="Credits">Кредиты на старте; null — общие стартовые из shop.json.</param>
/// <param name="Cargo">Что лежит в трюме с первой минуты: товар, который уже пора куда-то везти.</param>
/// <param name="Rep">Первое отношение мира: ключ места или системы → очки.</param>
public sealed record CareerDef(
    string Name = "",
    string Hint = "",
    bool Enabled = true,
    string? System = null,
    string? Place = null,
    string? Hull = null,
    IReadOnlyList<string>? Weapons = null,
    IReadOnlyDictionary<string, string>? Modules = null,
    int? Credits = null,
    IReadOnlyDictionary<string, int>? Cargo = null,
    IReadOnlyDictionary<string, double>? Rep = null)
{
    [JsonIgnore] public IReadOnlyList<string> WeaponList => Weapons ?? [];

    [JsonIgnore] public IReadOnlyDictionary<string, int> CargoMap => Cargo ?? new Dictionary<string, int>();

    [JsonIgnore] public IReadOnlyDictionary<string, double> RepMap => Rep ?? new Dictionary<string, double>();
}

/// <summary>Пути пилота из careers.json. Нет файла — путей нет, и все новые пилоты одинаковы, как до M15.5.</summary>
public sealed record CareerRules(
    string Default = CareerRules.DefaultCareer,
    IReadOnlyDictionary<string, CareerDef>? Careers = null)
{
    public const string File = "careers.json";

    /// <summary>Путь, который выбран на экране входа заранее: не тронув ничего, игрок получает его.</summary>
    public const string DefaultCareer = "ranger";

    public static readonly CareerRules None = new(Careers: null);

    [JsonIgnore]
    public IReadOnlyDictionary<string, CareerDef> CareerMap => Careers ?? new Dictionary<string, CareerDef>();

    [JsonIgnore] public bool Any => CareerMap.Count > 0;

    /// <summary>В этот путь пускают: он есть и не заперт. Пират до своего этапа — заперт.</summary>
    public bool IsPlayable(string? id) => id is not null && CareerMap.TryGetValue(id, out var career) && career.Enabled;

    /// <summary>
    /// Набор пути; null — путей нет вовсе. Незнакомый или запертый читается как путь по умолчанию:
    /// так профиль старше M15.5 (у него пути нет) получает ровно то, что получал раньше.
    /// </summary>
    public CareerDef? Of(string? id)
    {
        if (!Any) return null;
        if (IsPlayable(id)) return CareerMap[id!];
        return CareerMap.TryGetValue(Default, out var fallback) ? fallback : null;
    }

    /// <summary>Оснащение пути: пушки по слотам и модули; чего нет в файле — из стартового комплекта.</summary>
    public static ShipFit FitOf(CareerDef career)
    {
        var modules = career.Modules ?? new Dictionary<string, string>();
        string Slot(string slot, string fallback) => modules.TryGetValue(slot, out var id) ? id : fallback;
        return new ShipFit(
            career.WeaponList.Count > 0 ? [.. career.WeaponList] : [SimConfig.DefaultWeapon],
            Slot(Fitting.EngineSlot, Fitting.StarterEngine),
            Slot(Fitting.ShieldSlot, Fitting.StarterShield),
            Slot(Fitting.RadarSlot, Fitting.StarterRadar),
            Slot(Fitting.GeneratorSlot, Fitting.StarterGenerator));
    }

    /// <returns>Описание ошибки или null, если файл годится.</returns>
    public string? Validate(
        IReadOnlyDictionary<string, HullParams> hulls,
        IReadOnlyDictionary<string, WeaponParams> weapons,
        IReadOnlyDictionary<string, ModuleParams>? modules,
        LootRules? loot,
        GalaxyRules? galaxy)
    {
        if (CareerMap.Count == 0) return "no careers";
        if (!CareerMap.TryGetValue(Default, out var byDefault)) return $"default: unknown career {Default}";
        if (!byDefault.Enabled) return $"default: career {Default} is locked";

        foreach (var (id, career) in CareerMap)
        {
            if (string.IsNullOrWhiteSpace(career.Name)) return $"{id}: name is empty";
            // Запертый путь — только карточка на экране входа, набора у него нет и проверять нечего.
            if (!career.Enabled) continue;

            var hullId = career.Hull ?? SimConfig.DefaultHull;
            if (!hulls.TryGetValue(hullId, out var hull)) return $"{id}: unknown hull {hullId}";
            if (career.WeaponList.Count > hull.Slots.Count)
                return $"{id}: {career.WeaponList.Count} weapons for {hull.Slots.Count} slots of '{hullId}'";
            foreach (var weapon in career.WeaponList)
                if (!weapons.ContainsKey(weapon)) return $"{id}: unknown weapon {weapon}";
            foreach (var (slot, moduleId) in career.Modules ?? new Dictionary<string, string>())
            {
                if (modules is null || !modules.TryGetValue(moduleId, out var module)) return $"{id}: unknown module {moduleId}";
                if (module.Slot != slot) return $"{id}: module {moduleId} does not go into the '{slot}' slot";
            }

            // Пилот обязан взлететь: то же требование, что и к стартовому комплекту.
            if (modules is not null)
            {
                var fit = FitOf(career);
                var fitted = Fitting.Refit(hull, fit, weapons, modules);
                if (!fitted.Items().SequenceEqual(fit.Items()))
                    return $"{id}: the kit does not fit the '{hullId}' hull (class, slots or generator power)";
            }

            if (career.Credits is < 0) return $"{id}: credits must not be negative";
            var volume = 0.0;
            foreach (var (item, count) in career.CargoMap)
            {
                if (count <= 0) return $"{id}: cargo {item} must be positive";
                if (loot is null || !loot.ItemMap.TryGetValue(item, out var definition)) return $"{id}: unknown cargo {item}";
                volume += definition.Volume * count;
            }
            if (volume > hull.Cargo) return $"{id}: starting cargo does not fit the hold of '{hullId}'";

            if (galaxy is not null)
            {
                if (career.Place is { } place)
                {
                    if (!galaxy.HasPlace(place)) return $"{id}: unknown place {place}";
                    // Иначе пилот появился бы в одной системе, а домом считал бы место из другой.
                    var system = career.System ?? galaxy.StartSystem;
                    if (galaxy.SystemOfPlace(place) != system) return $"{id}: place {place} is not in system {system}";
                }
                if (career.System is { } systemId && galaxy.System(systemId) is null) return $"{id}: unknown system {systemId}";
                foreach (var key in career.RepMap.Keys)
                {
                    var (kind, target) = PlaceKey.Split(key);
                    var known = kind == PlaceKey.SystemKind ? galaxy.System(target) is not null : galaxy.HasPlace(key);
                    if (!known) return $"{id}: unknown reputation key {key}";
                }
            }
        }
        return null;
    }

    public static bool TryParse(
        string json,
        IReadOnlyDictionary<string, HullParams> hulls,
        IReadOnlyDictionary<string, WeaponParams> weapons,
        IReadOnlyDictionary<string, ModuleParams>? modules,
        LootRules? loot,
        GalaxyRules? galaxy,
        out CareerRules rules,
        out string? error)
    {
        rules = None;
        CareerRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CareerRules>(json, JsonCatalog.Options);
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
        error = parsed.Validate(hulls, weapons, modules, loot, galaxy);
        if (error is not null) return false;
        rules = parsed;
        return true;
    }
}
