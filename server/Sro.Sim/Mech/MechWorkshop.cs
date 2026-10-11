namespace Sro.Sim.Mech;

public sealed record MechPartInstance(string Id, string Category, string Part);
public sealed record MechAssembly(string? Body = null, string? Chassis = null, string? Left = null,
    string? Right = null, string? Backpack = null)
{
    public IEnumerable<string> Instances => new[] { Body, Chassis, Left, Right, Backpack }.OfType<string>();
}
public sealed record OwnedMech(string Id, string Name, MechAssembly Assembly, int Level = 1, int Xp = 0, string[]? Skills = null);
public sealed record MechExperience(string Id, string Name, int Gained, int Level, int Xp, string[] NewSkills);
public sealed record TerritoryOutcome(string ObjectName, int OldControl, int Control, string OldOwner, string Owner,
    int Contribution, Dictionary<string,TerritoryDebt> Debts);
public sealed record MechResult(string Session, string Mission, bool Won, string Reason, int Credits,
    string[] Parts, MechExperience[] Experience, string? StartRequest = null, TerritoryOutcome? Territory = null);
public sealed record MechWorkshopState(OwnedMech[] Mechs, MechPartInstance[] Parts,
    IReadOnlyDictionary<string, int> Resources, string[] Squad, string[] Grants, string[] Completed,
    MechResult[] Results, int Revision = 0, string[]? Requests = null)
{
    public static MechWorkshopState Empty => new([], [], new Dictionary<string, int>(), [], [], [], []);
}

/// <summary>Pure catalog, fitting and inventory operations. No partial mutations on a refusal.</summary>
public static class MechCatalog
{
    public static readonly string[] ResourceIds = ["titanium", "energy", "rareMetal"];
    public static bool Has(MechRules rules, string category, string part) => category switch
    {
        "body" => rules.BodyMap.ContainsKey(part), "chassis" => rules.ChassisMap.ContainsKey(part),
        "weapon" => rules.WeaponMap.ContainsKey(part), "shield" => rules.ShieldMap.ContainsKey(part),
        "backpack" => rules.BackpackMap.ContainsKey(part), _ => false,
    };

    public static MechPartInstance? FromKey(MechRules rules, string key, string? id = null)
    {
        if (key is null) return null;
        var split = key.Split(':', 2);
        return split.Length == 2 && Has(rules, split[0], split[1])
            ? new(id ?? Guid.NewGuid().ToString("N"), split[0], split[1]) : null;
    }

    public static MechUnitDef? Resolve(MechRules rules, MechWorkshopState state, OwnedMech mech)
    {
        string? Part(string? id) => state.Parts.FirstOrDefault(p => p.Id == id)?.Part;
        var a = mech.Assembly;
        return Part(a.Body) is { } body && Part(a.Chassis) is { } chassis
            ? new(mech.Name, body, chassis, Part(a.Left), Part(a.Right), Part(a.Backpack), mech.Level) : null;
    }

    public static int Weight(MechRules rules, MechUnitDef def) => rules.BodyMap[def.Body].Weight
        + (rules.Weapon(def.Left)?.Weight ?? rules.Shield(def.Left)?.Weight ?? 0)
        + (rules.Weapon(def.Right)?.Weight ?? rules.Shield(def.Right)?.Weight ?? 0)
        + (def.Backpack is { } pack ? rules.BackpackMap[pack].Weight : 0);

    public static string? Fitting(MechRules rules, MechUnitDef def, bool combat = true)
    {
        if (!rules.BodyMap.ContainsKey(def.Body) || !rules.ChassisMap.ContainsKey(def.Chassis)) return "badAssembly";
        if (def.Left is { } l && rules.Arm(l).Hp == 0 || def.Right is { } r && rules.Arm(r).Hp == 0
            || def.Backpack is { } b && !rules.BackpackMap.ContainsKey(b)) return "badAssembly";
        if (combat && (def.Left is null || def.Right is null || rules.Weapon(def.Left) is null && rules.Weapon(def.Right) is null)) return "incomplete";
        var max = rules.ChassisMap[def.Chassis].MaxLoad;
        return max > 0 && Weight(rules, def) > max ? "overweight" : null;
    }

    public static string? SaveAssembly(MechRules rules, MechWorkshopState state, string id, string? name,
        MechAssembly assembly, out MechWorkshopState next)
    {
        next = state;
        var mech = state.Mechs.FirstOrDefault(m => m.Id == id);
        if (mech is null || name is { Length: > 40 } || name is not null && string.IsNullOrWhiteSpace(name)) return "ownership";
        var ids = assembly.Instances.ToArray();
        if (ids.Distinct().Count() != ids.Length) return "ownership";
        var used = state.Mechs.Where(m => m.Id != id).SelectMany(m => m.Assembly.Instances).ToHashSet();
        var slots = new[] { (assembly.Body, "body"), (assembly.Chassis, "chassis"), (assembly.Left, "arm"),
            (assembly.Right, "arm"), (assembly.Backpack, "backpack") };
        foreach (var (instance, category) in slots)
        {
            if (instance is null) continue;
            var part = state.Parts.FirstOrDefault(p => p.Id == instance);
            if (part is null || used.Contains(instance) || !Has(rules, part.Category, part.Part)
                || (category == "arm" ? part.Category is not ("weapon" or "shield") : part.Category != category)) return "ownership";
        }
        var updated = mech with { Assembly = assembly, Name = name?.Trim() ?? mech.Name };
        if (Resolve(rules, state, updated) is { } def && Fitting(rules, def, combat: false) is { } error) return error;
        next = state with { Mechs = [.. state.Mechs.Select(m => m.Id == id ? updated : m)], Revision = state.Revision + 1 };
        return null;
    }

    public static string? Create(MechWorkshopState state, string? name, out MechWorkshopState next)
    {
        next = state;
        if (state.Mechs.Length >= 4) return "mechLimit";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40) return "badAssembly";
        var mech = new OwnedMech(Guid.NewGuid().ToString("N"), name.Trim(), new());
        next = state with { Mechs = [.. state.Mechs, mech], Squad = [.. state.Squad, mech.Id], Revision = state.Revision + 1 };
        return null;
    }

    public static string? Buy(MechRules rules, MechWorkshopState state, string key, int credits,
        out MechWorkshopState next, out int remaining)
    {
        next = state; remaining = credits;
        if (!rules.RecipeMap.TryGetValue(key, out var recipe) || !Has(rules, recipe.Category, recipe.Part)) return "badPart";
        if (credits < recipe.Credits || recipe.Resources.Any(r => state.Resources.GetValueOrDefault(r.Key) < r.Value)) return "resources";
        var resources = new Dictionary<string, int>(state.Resources);
        foreach (var (id, count) in recipe.Resources) resources[id] -= count;
        next = state with { Resources = resources,
            Parts = [.. state.Parts, new MechPartInstance(Guid.NewGuid().ToString("N"), recipe.Category, recipe.Part)], Revision = state.Revision + 1 };
        remaining -= recipe.Credits;
        return null;
    }

    public static MechWorkshopState Grant(MechRules rules, MechWorkshopState state, string grant, IEnumerable<string> parts)
    {
        if (state.Grants.Contains(grant)) return state;
        var added = parts.Select(key => FromKey(rules, key) ?? throw new InvalidOperationException($"Unknown grant part {key}")).ToArray();
        return state with { Parts = [.. state.Parts, .. added], Grants = [.. state.Grants, grant], Revision = state.Revision + 1 };
    }

    public static MechWorkshopState Prototype(MechRules rules, MechWorkshopState state)
    {
        if (rules.Workshop is not { } workshop || state.Grants.Contains("prototype")) return state;
        var def = rules.UnitMap[workshop.PrototypeUnit];
        var before = state.Parts.Length;
        string Arm(string id) => $"{(rules.Weapon(id) is null ? "shield" : "weapon")}:{id}";
        state = Grant(rules, state, "prototype", [$"body:{def.Body}", $"chassis:{def.Chassis}", Arm(def.Left!), Arm(def.Right!)]);
        var parts = state.Parts.Skip(before).ToArray();
        var mech = new OwnedMech(Guid.NewGuid().ToString("N"), def.Name, new(parts[0].Id, parts[1].Id, parts[2].Id, parts[3].Id));
        return state with { Mechs = [.. state.Mechs, mech], Squad = [.. state.Squad, mech.Id] };
    }

    public static string? Validate(MechRules rules)
    {
        foreach (var (id, b) in rules.BackpackMap)
            if (b is null || b.Weight < 0 || !double.IsFinite(b.Value) || b.Value <= 0 || b.Effect is not ("accuracy" or "armor" or "damage" or "mobility" or "repair") || string.IsNullOrWhiteSpace(b.Art)) return $"backpacks.{id}: invalid effect, weight, value or art";
        foreach (var (id, r) in rules.RecipeMap)
            if (r is null || r.Credits < 0 || r.Part is null || !Has(rules, r.Category, r.Part) || r.Resources is null || r.Resources.Any(p => !ResourceIds.Contains(p.Key) || p.Value <= 0)) return $"recipes.{id}: invalid recipe";
        foreach (var (id, s) in rules.SkillMap)
            if (s is null || s.Chance is < 0 or > 100 || s.Weight <= 0 || s.Cooldown < 0 || s.Bonus < 0
                || s.AccuracyPenalty < 0 || s.BlockBonus < 0
                || s.Requires is not (null or "twoWeapons" or "weapon" or "shield" or "biped")
                || (s.Bodies ?? []).Concat(s.PoolBodies ?? []).Any(b => !rules.BodyMap.ContainsKey(b))
                || (s.Chassis ?? []).Concat(s.PoolChassis ?? []).Any(c => !rules.ChassisMap.ContainsKey(c))) return $"skills.{id}: invalid skill";
        var progression=rules.ProgressionDef;
        if(!double.IsFinite(rules.AiDef.ThreatPriority) || rules.AiDef.ThreatPriority<0) return "ai: invalid threat priority";
        if(progression.KillXp<0 || progression.VictoryXp<0 || progression.LevelFactor<0 || progression.FactorMin<=0 || progression.FactorMax<progression.FactorMin
            || progression.UnlockBase<0 || progression.UnlockMax is <0 or >100 || progression.UnlockPerLevel<0 || progression.SkillLimit is <1 or >6) return "progression: invalid XP or skill settings";
        foreach (var (id, m) in rules.MissionMap)
        {
            if ((m.RewardParts ?? []).Any(p => FromKey(rules, p, "check") is null)) return $"missions.{id}: unknown reward part";
            var f = new MechField(m.Map);
            bool Cell(MechPoint p) => p is not null && f.Passable(p.X, p.Y);
            var o = m.Objective;
            if (o is not null && (o.Kind is not ("eliminate" or "defend" or "escort" or "capture" or "sabotage" or "rescue")
                || o.Kind == "defend" && (o.Object is null || o.Rounds < 1)
                || o.Kind == "escort" && (o.Object is null || o.Route is not { Length: > 0 })
                || o.Kind == "capture" && (o.Zone is not { Length: > 0 } || o.Rounds < 1)
                || o.Kind is "sabotage" or "rescue" && (o.Object is null || o.Evacuation is not { Length: > 0 })
                || (o.Route ?? []).Concat(o.Zone ?? []).Concat(o.FiringCells ?? []).Concat(o.Evacuation ?? []).Any(p => !Cell(p)))) return $"missions.{id}: invalid objective";
            if (o?.Route is { } route && o.Object is { } start)
            {
                var prev = new MechPoint(start.X, start.Y);
                foreach (var p in route) { if (MechField.Distance(prev.X, prev.Y, p.X, p.Y) != 1) return $"missions.{id}: route must use adjacent cells"; prev = p; }
            }
            foreach (var wave in m.Waves ?? [])
                if (wave is null || wave.Round < 1 || wave.Enemies is not {Length: > 0} || wave.Enemies.Any(s => s is null || s.Unit is null || !rules.UnitMap.ContainsKey(s.Unit) || !f.Passable(s.X, s.Y) || s.Dir is < 0 or > 7)) return $"missions.{id}: invalid wave";
        }
        if (rules.Workshop is { } w)
        {
            if (string.IsNullOrWhiteSpace(w.Place) || string.IsNullOrWhiteSpace(w.Campaign) || string.IsNullOrWhiteSpace(w.PrototypeMission)
                || w.PrototypeUnit is null || w.MissionOrder is null || w.StarterParts is null
                || !rules.UnitMap.TryGetValue(w.PrototypeUnit, out var proto) || Fitting(rules, proto) is not null
                || w.MissionOrder.Length != 4 || w.MissionOrder[0] != MechRules.FirstSortie || w.MissionOrder.Distinct().Count() != 4
                || w.MissionOrder.Any(id => rules.Mission(id) is null)
                || w.StarterParts.Any(p => FromKey(rules, p, "check") is null)) return "workshop: invalid prototype, mission order or starter grant";
            var starter = w.StarterParts.Select(p => FromKey(rules, p, "check")!).ToArray();
            string? First(string cat) => starter.FirstOrDefault(p => p.Category == cat)?.Part;
            var arms = starter.Where(p => p.Category is "weapon" or "shield").ToArray();
            if (First("body") is not { } body || First("chassis") is not { } chassis || arms.Length < 2
                || Fitting(rules, new("Starter", body, chassis, arms[0].Part, arms[1].Part)) is not null) return "workshop: starter must build a combat ready second mech";
            if (w.MissionOrder.Skip(1).Any(id => rules.MissionMap[id].RewardParts is not { Length: > 0 })) return "workshop: required mission rewards missing";
            foreach (var f in rules.BodyMap.Values.Concat(rules.ChassisMap.Values))
                if (f.Weight < 0 || string.IsNullOrWhiteSpace(f.Art)) return "workshop: frames need art and nonnegative weight";
            if (rules.ChassisMap.Values.Any(f => f.MaxLoad < 1)) return "workshop: chassis needs maxLoad";
            if (rules.WeaponMap.Values.Any(f => f.Weight < 0 || string.IsNullOrWhiteSpace(f.Art)) || rules.ShieldMap.Values.Any(f => f.Weight < 0 || string.IsNullOrWhiteSpace(f.Art))) return "workshop: arms need art and nonnegative weight";
        }
        return null;
    }

    public static MechWorkshopState CommitResult(MechRules rules, MechWorkshopState state, MechResult result, MechRandom random, out MechResult saved)
    {
        if (state.Results.FirstOrDefault(r=>r.Session==result.Session) is { } existing) {saved=existing;return state;}
        var first=result.Won && !state.Completed.Contains(result.Mission);
        saved=result with {Credits=first?result.Credits:0,Parts=first?result.Parts:[],Experience=result.Experience.ToArray()};
        var next=state;
        if(first)
        {
            next=Grant(rules,next,result.Mission,saved.Parts);
            next=next with {Completed=[..next.Completed,result.Mission]};
            if(result.Mission==MechRules.FirstSortie && rules.Workshop is { } workshop)next=Grant(rules,next,"starter",workshop.StarterParts);
        }
        var mechs=next.Mechs.ToArray();
        for(var i=0;i<mechs.Length;i++)
        {
            var index=Array.FindIndex(saved.Experience,e=>e.Id==mechs[i].Id);if(index<0)continue;
            var xp=saved.Experience[index];
            mechs[i]=MechDevelopment.Gain(rules,mechs[i],Resolve(rules,next,mechs[i])!,xp.Gained,random,out var unlocked);
            saved.Experience[index]=xp with {Level=mechs[i].Level,Xp=mechs[i].Xp,NewSkills=unlocked};
        }
        return next with {Mechs=mechs,Results=[..next.Results,saved],Revision=next.Revision+1};
    }
}

public static class MechDevelopment
{
    public static int Threshold(int level) => checked(100 + 75 * (level - 1) + 25 * (level - 1) * (level - 1));
    public static int KillXp(MechRules rules, int level, int enemy) => MechCombat.Round(rules.ProgressionDef.KillXp
        * Math.Clamp(1 + rules.ProgressionDef.LevelFactor * (enemy - level), rules.ProgressionDef.FactorMin, rules.ProgressionDef.FactorMax));

    public static bool Compatible(MechRules rules, MechUnitDef def, string id)
    {
        if (!rules.SkillMap.TryGetValue(id, out var s)) return false;
        if (s.Bodies is { Length: > 0 } && !s.Bodies.Contains(def.Body) || s.Chassis is { Length: > 0 } && !s.Chassis.Contains(def.Chassis)) return false;
        return s.Requires switch
        {
            "twoWeapons" => rules.Weapon(def.Left) is not null && rules.Weapon(def.Right) is not null,
            "shield" => rules.Shield(def.Left) is not null || rules.Shield(def.Right) is not null,
            "weapon" => rules.Weapon(def.Left) is not null || rules.Weapon(def.Right) is not null,
            "biped" => def.Chassis != "tracked", _ => true,
        };
    }

    public static OwnedMech Gain(MechRules rules, OwnedMech mech, MechUnitDef def, int gained, MechRandom random, out string[] unlocked)
    {
        var xp = checked(mech.Xp + gained);
        var level = mech.Level;
        var skills = (mech.Skills ?? []).ToList();
        var added = new List<string>();
        var p = rules.ProgressionDef;
        while (xp >= Threshold(level))
        {
            xp -= Threshold(level++);
            if (skills.Count >= p.SkillLimit || random.Next(100) >= Math.Min(p.UnlockMax, p.UnlockBase + p.UnlockPerLevel * (level - 1))) continue;
            var pool = rules.SkillMap.Where(s => !skills.Contains(s.Key) && Compatible(rules, def, s.Key)
                && (s.Value.PoolBodies is not {Length:>0} || s.Value.PoolBodies.Contains(def.Body))
                && (s.Value.PoolChassis is not {Length:>0} || s.Value.PoolChassis.Contains(def.Chassis))).OrderBy(s => s.Key, StringComparer.Ordinal).ToArray();
            if (pool.Length == 0) continue;
            var roll = random.Next(pool.Sum(s => s.Value.Weight));
            foreach (var s in pool)
            {
                roll -= s.Value.Weight;
                if (roll >= 0) continue;
                skills.Add(s.Key); added.Add(s.Key); break;
            }
        }
        unlocked = [.. added];
        return mech with { Level = level, Xp = xp, Skills = [.. skills] };
    }
}
