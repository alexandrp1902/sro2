using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>Баланс для тестов комнаты. Не читается из shared/, чтобы тюнинг не ломал тесты.</summary>
internal static class TestBalance
{
    public static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams>
    {
        ["light"] = new("Лёгкий", 165, 180, 220, 150, 0.65, 0, 16, Hp: 400, Shield: 150, ShieldRegen: 20, Evasion: 25, MoveEvasion: 8),
        ["heavy"] = new("Тяжёлый", 85, 70, 80, 55, 1.5, 0, 30, Hp: 1800, Shield: 500, ShieldRegen: 50, Evasion: 5, MoveEvasion: 2),
    };

    public static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["pulse"] = new("Импульсная пушка Mk1", 100, 75, 1.0, 500, 700, 10),
        ["laser"] = new("Лазер Mk1", 40, 90, 0.5, 400, 600, 20, Kind: "beam"),
        // Убивает с одного попадания — для тестов уничтожения и респауна.
        ["doom"] = new("Тестовая пушка", 100_000, 100, 1.0, 500, 700, 0),
    };

    /// <summary>Без дронов и без разброса спауна — корабли появляются ровно в SpawnX, SpawnY.</summary>
    public static Balance Create(CombatRules? rules = null) => new(Hulls, Weapons, rules ?? new CombatRules(SpawnJitter: 0));
}
