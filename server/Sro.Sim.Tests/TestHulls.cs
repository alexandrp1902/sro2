namespace Sro.Sim.Tests;

public enum TestHullId { Light, Medium, Heavy }

/// <summary>Параметры из боевого документа (§47). Не читаются из hulls.json, чтобы тюнинг не ломал тесты.</summary>
internal static class TestHulls
{
    public static readonly HullParams Light = new("Лёгкий", 330, 180, 220, 150, 0.65, 0, 16);
    public static readonly HullParams Medium = new("Средний", 250, 120, 145, 100, 1.0, 0, 22);
    public static readonly HullParams Heavy = new("Тяжёлый", 170, 70, 80, 55, 1.5, 0, 30);

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
}
