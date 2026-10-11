using System.Text.Json;

namespace Sro.Sim.Mech;

public sealed record TerritoryObjectDef(string Id, string Name, string Type, int X, int Y, string Biome,
    string[] Links, int StartControl, string[][] Fields);
public sealed record TerritoryOperationDef(string Name, string Goal, int WinControl, int LoseControl, int Contribution,
    int Credits, string Objective, string[] Solo, string[] Coop, string[] Emergency);
public sealed record TerritoryConfig(DateTimeOffset SeasonEpoch, int SeasonSeconds, int DaySeconds, int StrikeSeconds,
    int ActivationSeconds, int DisconnectSeconds, int TransitionSeconds, string Place, string[] BaseLinks,
    TerritoryObjectDef[] Objects, Dictionary<string, TerritoryOperationDef> Operations, Sro.Sim.StoryCampaign? Eva = null)
{
    public static TerritoryConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<TerritoryConfig>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Empty territory configuration");
        if (config.Validate() is { } error) throw new InvalidDataException(error);
        return config;
    }

    public string? Validate()
    {
        if (Objects is null || BaseLinks is null || Operations is null || string.IsNullOrWhiteSpace(Place)
            || Objects.Any(o=>o is null || o.Links is null || o.Fields is null || o.Fields.Any(m=>m is null || m.Any(r=>r is null)))) return "Incomplete territory configuration";
        if (SeasonSeconds <= 0 || DaySeconds <= 0 || SeasonSeconds % DaySeconds != 0 || StrikeSeconds <= 0
            || ActivationSeconds <= 15 || DisconnectSeconds <= 0 || TransitionSeconds <= 0) return "Invalid territory intervals";
        if (Objects.Length != 12 || Objects.Select(o => o.Id).Distinct().Count() != Objects.Length) return "Expected 12 unique territory objects";
        var ids = Objects.Select(o => o.Id).ToHashSet();
        if (BaseLinks.Length != 2 || BaseLinks.Distinct().Count() != 2 || BaseLinks.Any(id => !ids.Contains(id))) return "Invalid base roads";
        foreach (var o in Objects)
        {
            if (string.IsNullOrWhiteSpace(o.Name) || o.StartControl is not (0 or 100) || o.Type is not ("mine" or "factory" or "power" or "warehouse" or "relay" or "repair" or "fort")) return $"Invalid object {o.Id}";
            if (o.Links.Any(id => id == o.Id || !ids.Contains(id) || !Objects.First(p => p.Id == id).Links.Contains(o.Id))) return $"Invalid road {o.Id}";
            if (o.Biome is not ("desert" or "ice" or "jungle" or "station-interior" or "barren") || o.Fields.Length != 2) return $"Invalid biome/fields {o.Id}";
            foreach (var map in o.Fields)
            {
                if (map.Length != 12 || map.Any(row => row.Length != 12 || row.Any(c => !".,rcwb".Contains(c)))) return $"Invalid field {o.Id}";
                var field = new MechField(map);
                var reachable = field.Reach(1, 10, 200, new HashSet<int>());
                if (Enumerable.Range(0, 144).Any(i => field.Passable(i % 12, i / 12) && !reachable.ContainsKey(i) && i != field.Index(1, 10))) return $"Disconnected field {o.Id}";
                if (new[] { new MechPoint(5,5), new(2,9), new(8,2), new(1,10), new(3,10), new(1,8), new(3,8), new(7,1), new(9,1), new(8,3), new(10,3) }.Any(p => !field.Passable(p.X,p.Y))) return $"Blocked objective/spawn {o.Id}";
            }
        }
        var visited = new HashSet<string>(); var queue = new Queue<string>(); queue.Enqueue(Objects[0].Id);
        while (queue.TryDequeue(out var id)) if (visited.Add(id)) foreach (var link in Objects.First(o => o.Id == id).Links) queue.Enqueue(link);
        if (visited.Count != ids.Count || Objects.Count(o => o.StartControl == 100) != 4) return "Disconnected graph/start ownership";
        foreach (var key in new[] { "assault", "sabotage", "defense", "escort", "rescue" })
            if (!Operations.TryGetValue(key, out var op) || op is null || op.Solo is null || op.Coop is null || op.Emergency is null || op.Credits < 0 || op.Contribution <= 0 || op.WinControl <= 0 || op.LoseControl >= 0
                || op.Solo.Length < 1 || op.Coop.Length <= op.Solo.Length || op.Emergency.Length < 1) return $"Invalid operation {key}";
        return null;
    }
    public string? ValidateRules(MechRules rules)
    {
        foreach(var pair in Operations)
        {
            var op=pair.Value;
            if(op.Solo.Length>2||op.Coop.Length>4||op.Emergency.Length>1||op.Objective is not ("capture" or "sabotage" or "defend" or "escort" or "rescue")) return $"Invalid territory roster/objective {pair.Key}";
            foreach(var unit in op.Solo.Concat(op.Coop).Concat(op.Emergency)) if(!rules.UnitMap.ContainsKey(unit)) return $"Unknown territory unit {unit}";
        }
        if(Eva is not null&&(Eva.MissionList.Count!=3||Eva.MissionList.Select(m=>m.Id).Distinct().Count()!=3||Eva.MissionList.Any(m=>m.Reward<0||m.Place!=Place)))return "Invalid Eva chain";
        return null;
    }
}

public sealed record TerritoryObject(string Id, int Control, string Owner, int Revision = 0, string? Active = null, DateTimeOffset? DefenseAt = null);
public sealed record TerritoryDebt(int Titanium, int Energy);
public sealed record TerritoryReward(int Credits, int Titanium = 0, int Energy = 0, int RareMetal = 0, string? Part = null, string? Title = null);
public sealed record TerritoryProfile(int Level = 1, Dictionary<string, TerritoryDebt>? Debts = null,
    DateTimeOffset? EmergencyAt = null, string[]? Applied = null, string[]? Titles = null, int EvaStep = 0,
    DateTimeOffset? EvaAccepted = null, bool EvaDone = false);

/// <summary>Pure authoritative arithmetic, also mirrored in the client for previews.</summary>
public static class TerritoryRules
{
    public const string Resistance = "resistance", Corporation = "corporation";
    public static TerritoryObject Change(TerritoryObject o, int delta)
    {
        var control = Math.Clamp(o.Control + delta, 0, 100);
        return o with { Control = control, Owner = control == 100 ? Resistance : control == 0 ? Corporation : o.Owner, Revision = o.Revision + 1 };
    }
    public static TerritoryObject Reset(TerritoryObject o, int start)
    {
        var control = (int)Math.Floor(start + (o.Control - start) * 2d / 3 + .5);
        return o with { Control = control, Owner = control >= 50 ? Resistance : Corporation, Active = null, DefenseAt = null, Revision = o.Revision + 1 };
    }
    public static bool Accessible(TerritoryConfig c, IEnumerable<TerritoryObject> state, string id)
    {
        var owned = state.Where(o => o.Owner == Resistance).Select(o => o.Id).ToHashSet();
        return owned.Contains(id) || c.BaseLinks.Contains(id) || c.Objects.Any(o => owned.Contains(o.Id) && o.Links.Contains(id));
    }
    public static TerritoryDebt Service(int lost, int max, bool discount) => max <= 0 || lost <= 0 ? new(0,0)
        : new((int)Math.Ceiling(4d * Math.Min(lost,max) / max * (discount ? .8 : 1)), (int)Math.Ceiling(2d * Math.Min(lost,max) / max * (discount ? .8 : 1)));
    public static TerritoryReward FinalReward(int contribution, int season) => contribution >= 100
        ? new(5000, Part: "shield:heavy", Title: $"Защитник Нова-Прайм · сезон {season}")
        : contribution >= 40 ? new(2500, Part: "backpack:armor") : new(contribution > 0 ? 1000 : 0);
    public static Dictionary<string,int> Distribute(int pool, IReadOnlyDictionary<string,int> contributions)
    {
        var eligible = contributions.Where(p => p.Value > 0).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var sum = eligible.Sum(p => (long)p.Value);
        if (sum == 0) return [];
        var result = eligible.ToDictionary(p => p.Key, p => (int)((long)pool * p.Value / sum));
        var remainder = pool - result.Values.Sum();
        foreach (var p in eligible.OrderByDescending(p => (long)pool * p.Value % sum).ThenBy(p => p.Key, StringComparer.Ordinal).Take(remainder)) result[p.Key]++;
        return result;
    }
    public static double CargoVolume(string hull, string resource, double volume) => hull == "dropship" && MechCatalog.ResourceIds.Contains(resource) ? volume / 1.2 : volume;
}
