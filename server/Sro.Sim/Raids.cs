namespace Sro.Sim;

/// <summary>Состав налёта: столько пиратов такого типа и уровня прилетают вместе; вес — как часто именно эта группа.</summary>
public sealed record RaidGroup(string Type, int Level = 1, int Count = 1, double Weight = 1)
{
    public string? Validate(IReadOnlyDictionary<string, NpcType> types)
    {
        if (Type is null || !types.ContainsKey(Type)) return $"unknown type '{Type}'";
        if (Level is < 1 or > NpcSpawn.MaxLevel) return $"level must be within 1..{NpcSpawn.MaxLevel}";
        if (Count is < 1 or > NpcSpawn.MaxCount) return $"count must be within 1..{NpcSpawn.MaxCount}";
        if (!(Weight > 0)) return "weight must be positive";
        return null;
    }
}

/// <summary>Пиратская база: в пиратской системе налётчики появляются здесь и сюда же уходят — вместо врат.</summary>
public sealed record PirateBase(string Name, double X, double Y);

/// <summary>
/// Налёты пиратов на систему (galaxy.json, поле pirates). Группа прилетает через случайные врата — будто из соседней
/// системы, — летит в случайную точку, патрулирует там в поисках добычи, потом уходит во врата и исчезает.
/// В пиратской системе (есть <see cref="Base"/>) то же самое, только появляются и уходят они на базе.
/// </summary>
/// <param name="MaxGroups">Столько групп в системе одновременно.</param>
/// <param name="IntervalSeconds">В среднем через столько после ухода или гибели группы прилетает новая.</param>
/// <param name="PatrolMinSeconds">Сколько группа патрулирует, прежде чем уйти: случайно между min и max.</param>
public sealed record RaidRules(
    int MaxGroups = 2,
    double IntervalSeconds = 60,
    double PatrolMinSeconds = 120,
    double PatrolMaxSeconds = 240,
    PirateBase? Base = null,
    IReadOnlyList<RaidGroup>? Groups = null)
{
    public const int MaxMaxGroups = 10;

    public IReadOnlyList<RaidGroup> GroupList => Groups ?? [];

    public int IntervalTicks => Math.Max(1, Combat.SecondsToTicks(IntervalSeconds));

    /// <summary>Группа по весам; r — случайное из [0, 1).</summary>
    public RaidGroup? Pick(double r)
    {
        var total = GroupList.Sum(g => g.Weight);
        if (total <= 0) return null;
        var x = r * total;
        foreach (var group in GroupList)
        {
            x -= group.Weight;
            if (x < 0) return group;
        }
        return GroupList[^1];
    }

    public string? Validate(IReadOnlyDictionary<string, NpcType> types)
    {
        if (MaxGroups is < 0 or > MaxMaxGroups) return $"maxGroups must be within 0..{MaxMaxGroups}";
        if (!(IntervalSeconds > 0)) return "intervalSeconds must be positive";
        if (!(PatrolMinSeconds > 0) || !(PatrolMaxSeconds >= PatrolMinSeconds)) return "patrol seconds must satisfy 0 < patrolMinSeconds <= patrolMaxSeconds";
        if (MaxGroups > 0 && GroupList.Count == 0) return "groups must not be empty";
        if (Base is { } b)
        {
            if (string.IsNullOrWhiteSpace(b.Name)) return "base: name is empty";
            if (!(Math.Abs(b.X) <= NpcRules.WorldLimit) || !(Math.Abs(b.Y) <= NpcRules.WorldLimit))
                return $"base: x and y must be within ±{NpcRules.WorldLimit}";
        }
        for (var i = 0; i < GroupList.Count; i++)
        {
            var problem = GroupList[i] is null ? "is null" : GroupList[i].Validate(types);
            if (problem is not null) return $"groups[{i}]: {problem}";
        }
        return null;
    }
}
