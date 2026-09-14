using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Sim;

/// <summary>Учебный дрон (GDD §54): не стреляет, стоит на месте или кружит вокруг своей точки.</summary>
/// <param name="OrbitRadius">0 — стоит на месте.</param>
/// <param name="Throttle">Тяга на орбите, 0…1.</param>
/// <param name="Hp">Своя прочность вместо корпусной; null — как у корпуса.</param>
/// <param name="Shield">Свой щит вместо корпусного; null — как у корпуса.</param>
public sealed record DroneSpec(
    string Name,
    string Hull,
    double X,
    double Y,
    double OrbitRadius = 0,
    double Throttle = 1,
    double? Hp = null,
    double? Shield = null)
{
    public string? Validate(IReadOnlyDictionary<string, HullParams> hulls)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "name is empty";
        if (Hull is null || !hulls.ContainsKey(Hull)) return $"unknown hull '{Hull}'";
        if (!(Math.Abs(X) <= Movement.WorldHalfSize) || !(Math.Abs(Y) <= Movement.WorldHalfSize)) return "x and y must be inside the world";
        if (!(OrbitRadius >= 0)) return "orbitRadius must not be negative";
        if (!(Throttle >= 0 && Throttle <= 1)) return "throttle must be within 0..1";
        if (Hp is { } hp && !(hp > 0)) return "hp must be positive";
        if (Shield is { } shield && !(shield >= 0)) return "shield must not be negative";
        return null;
    }
}

/// <summary>Правила боя из shared/combat.json.</summary>
/// <param name="RespawnSeconds">Через столько уничтоженный корабль появляется снова (GDD §24 — 30 с; на плейтесте короче).</param>
/// <param name="ProtectionSeconds">Защита после появления; снимается раньше, если корабль выстрелил (§25).</param>
/// <param name="ShieldRegenDelay">Щит восстанавливается, если столько секунд не было урона (§17).</param>
/// <param name="SpawnJitter">Разброс точки появления — корабли не появляются друг в друге.</param>
public sealed record CombatRules(
    double RespawnSeconds = 10,
    double ProtectionSeconds = 10,
    double ShieldRegenDelay = 5,
    double SpawnJitter = 120,
    IReadOnlyList<DroneSpec>? Drones = null)
{
    [JsonIgnore] public int RespawnTicks => Math.Max(1, Combat.SecondsToTicks(RespawnSeconds));
    [JsonIgnore] public int ProtectionTicks => Combat.SecondsToTicks(ProtectionSeconds);
    [JsonIgnore] public int ShieldRegenDelayTicks => Combat.SecondsToTicks(ShieldRegenDelay);
    [JsonIgnore] public IReadOnlyList<DroneSpec> DroneList => Drones ?? [];

    /// <param name="hulls">Корпуса дронов должны существовать.</param>
    public string? Validate(IReadOnlyDictionary<string, HullParams> hulls)
    {
        if (!(RespawnSeconds >= 0) || !(ProtectionSeconds >= 0) || !(ShieldRegenDelay >= 0))
            return "respawnSeconds, protectionSeconds and shieldRegenDelay must not be negative";
        if (!(SpawnJitter >= 0)) return "spawnJitter must not be negative";
        for (var i = 0; i < DroneList.Count; i++)
        {
            var problem = DroneList[i] is null ? "is null" : DroneList[i].Validate(hulls);
            if (problem is not null) return $"drones[{i}]: {problem}";
        }
        return null;
    }

    public static bool TryParse(string json, IReadOnlyDictionary<string, HullParams> hulls, out CombatRules rules, out string? error)
    {
        rules = new CombatRules();
        CombatRules? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CombatRules>(json, JsonCatalog.Options);
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

/// <summary>Весь баланс: корпуса, пушки, правила боя. Меняется только целиком.</summary>
public sealed record Balance(
    IReadOnlyDictionary<string, HullParams> Hulls,
    IReadOnlyDictionary<string, WeaponParams> Weapons,
    CombatRules Rules)
{
    public const string HullsFile = "hulls.json";
    public const string WeaponsFile = "weapons.json";
    public const string RulesFile = "combat.json";

    /// <summary>Разбирает три файла вместе: правила ссылаются на корпуса.</summary>
    public static bool TryParse(string hullsJson, string weaponsJson, string rulesJson, out Balance? balance, out string? error)
    {
        balance = null;
        if (!HullCatalog.TryParse(hullsJson, out var hulls, out error))
        {
            error = $"{HullsFile}: {error}";
            return false;
        }
        if (!WeaponCatalog.TryParse(weaponsJson, out var weapons, out error))
        {
            error = $"{WeaponsFile}: {error}";
            return false;
        }
        if (!CombatRules.TryParse(rulesJson, hulls, out var rules, out error))
        {
            error = $"{RulesFile}: {error}";
            return false;
        }
        balance = new Balance(hulls, weapons, rules);
        return true;
    }
}
