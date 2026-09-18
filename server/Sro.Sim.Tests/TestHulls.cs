namespace Sro.Sim.Tests;

public enum TestHullId { Light, Medium, Heavy }

/// <summary>Параметры из боевого документа (§47). Не читаются из hulls.json, чтобы тюнинг не ломал тесты.</summary>
internal static class TestHulls
{
    public static readonly HullParams Light = new("Лёгкий", 330, 180, 220, 150, 0.65, 0, 16,
        Hp: 1500, Shield: 500, ShieldRegen: 20, Evasion: 25, MoveEvasion: 8);
    public static readonly HullParams Medium = new("Средний", 250, 120, 145, 100, 1.0, 0, 22,
        Hp: 3500, Shield: 1000, ShieldRegen: 35, Evasion: 14, MoveEvasion: 5);
    public static readonly HullParams Heavy = new("Тяжёлый", 170, 70, 80, 55, 1.5, 0, 30,
        Hp: 7000, Shield: 1800, ShieldRegen: 50, Evasion: 5, MoveEvasion: 2);

    public static HullParams Get(TestHullId id) => id switch
    {
        TestHullId.Light => Light,
        TestHullId.Medium => Medium,
        _ => Heavy,
    };

    /// <summary>Корень репозитория — там, где лежит shared/hulls.json.</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "shared", "hulls.json"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("Repository root with shared/hulls.json not found");
    }

    /// <summary>Настоящие файлы баланса из shared/ — порчу одного из них тест делает через with.</summary>
    public static BalanceSources SharedSources()
    {
        var dir = Path.Combine(RepoRoot(), "shared");
        string Read(string file) => File.ReadAllText(Path.Combine(dir, file));
        return new BalanceSources(
            Read(Balance.HullsFile), Read(Balance.WeaponsFile), Read(Balance.RulesFile),
            Read(Balance.NpcsFile), Read(Balance.LootFile), Read(Balance.MeteorsFile), Read(Balance.ShopFile));
    }
}

/// <summary>Пушки из GDD (§14, §47–48). Не читаются из weapons.json, чтобы тюнинг не ломал тесты.</summary>
internal static class TestWeapons
{
    public static readonly WeaponParams Pulse = new("Импульсная пушка Mk1", 100, 75, 1.0, 500, 700, 10, 60, "bolt");
    public static readonly WeaponParams Laser = new("Лазер Mk1", 40, 90, 0.5, 400, 600, 20, 60, "beam");
    // Снайперская: в упор мажет, зато достаёт дальше всех — на ней вектор проверяет оба склона штрафа за дистанцию.
    public static readonly WeaponParams Plasma = new("Плазма Mk1", 280, 60, 2.0, 450, 650, 15, 60, "orb", CloseRange: 250, ClosePenalty: 35);
}
