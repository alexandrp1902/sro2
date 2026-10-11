using Sro.Server.Net;
using Sro.Sim.Mech;

namespace Sro.Server.Game;

public sealed partial class Room
{
    public bool WorkshopOpen(Player p) => Balance.Mechs.Workshop is { } w
        && p.Docked && p.DockedPlace == w.Place && p.Workshop.Completed.Contains(MechRules.FirstSortie);

    public string[] MechAvailable(Player p) => Balance.Mechs.Workshop is { } w
        ? w.MissionOrder.Where((id, i) => i == 0 ? RelayHere(p) == w.Campaign : p.Workshop.Completed.Contains(w.MissionOrder[i - 1])).ToArray() : [];

    /// <summary>One-time migration; old space completion only grants the prototype, never ground victory.</summary>
    public void MigrateMechs(Player p)
    {
        if (Balance.Mechs.Workshop is not { } w) return;
        var log = p.Story.GetValueOrDefault(w.Campaign);
        if (log is null) return;
        var state = p.Workshop;
        if (log.Done.Contains(w.PrototypeMission) || log.Flags.Contains(Protocol.SortieWonFlag)) state = MechCatalog.Prototype(Balance.Mechs, state);
        if (log.Flags.Contains(Protocol.SortieWonFlag) && !state.Completed.Contains(MechRules.FirstSortie))
        {
            state = state with { Completed = [.. state.Completed, MechRules.FirstSortie] };
            log.Done.Add(MechRules.FirstSortie);
        }
        if (state.Completed.Contains(MechRules.FirstSortie)) state = MechCatalog.Grant(Balance.Mechs, state, "starter", w.StarterParts);
        if (!ReferenceEquals(p.Workshop, state)) { p.Workshop = state; Save(p); }
    }

    private static MechRules VisibleRules(MechRules rules) => rules with
    {
        Progression = null,
        Skills = rules.SkillMap.ToDictionary(s => s.Key, s => s.Value with { Weight = 1, Chance = 0, Bodies = null, Chassis = null, PoolBodies=null,PoolChassis=null }),
    };

    public void SendWorkshop(Player p, string? refused = null, string? request = null) => p.Connection?.Send(new WorkshopStateMsg(
        p.Workshop, VisibleRules(Balance.Mechs), p.Credits, WorkshopOpen(p), MechAvailable(p), refused, request, p.Territory));

    public void Workshop(IClientConnection connection, WorkshopActMsg m)
    {
        if (PlayerOf(connection) is not { AccountId: not null } p) return;
        MigrateMechs(p);
        var state = p.Workshop;
        void Refuse(string code) => SendWorkshop(p, code, m.Request);
        if (m.Act == "read") { SendWorkshop(p); return; }
        if (m.Request is null || m.Request.Length is < 1 or > 80) { Refuse("badAct"); return; }
        if ((state.Requests ?? []).Contains(m.Request)) { SendWorkshop(p, request: m.Request); return; }
        if (_host?.InMech(p) == true) { Refuse("mechBusy"); return; }
        if (!WorkshopOpen(p)) { Refuse("workshopLocked"); return; }
        if (m.Revision != state.Revision) { Refuse("staleRevision"); return; }
        var next = state;
        var credits = p.Credits;
        string? error;
        switch (m.Act)
        {
            case "create": error = MechCatalog.Create(state, m.Name, out next); break;
            case "save":
                error = m.Assembly is null || m.Mech is null ? "badAssembly" : MechCatalog.SaveAssembly(Balance.Mechs, state, m.Mech, m.Name, m.Assembly, out next);
                break;
            case "buy":
                if(Balance.Mechs.RecipeMap.GetValueOrDefault(m.Recipe??"")?.WorkshopLevel>p.Territory.Level) {error="workshopLevel";break;}
                error = MechCatalog.Buy(Balance.Mechs, state, m.Recipe ?? "", credits, out next, out credits); break;
            case "squad":
                error = m.Squad is null || m.Squad.Length > 4 || m.Squad.Distinct().Count() != m.Squad.Length
                    || m.Squad.Any(id => !state.Mechs.Any(mech => mech.Id == id)) ? "ownership" : null;
                if (error is null) next = state with { Squad = m.Squad!, Revision = state.Revision + 1 };
                break;
            case "transfer":
                if (m.Resource is not { } resource || !MechCatalog.ResourceIds.Contains(resource) || m.Count <= 0) { error = "resources"; break; }
                var resources = new Dictionary<string, int>(state.Resources);
                if (m.ToCargo)
                {
                    if (resources.GetValueOrDefault(resource) < m.Count) { error = "resources"; break; }
                    if (!p.Cargo.Fits(resource, m.Count, p.Effective(Balance).Cargo, Balance.Loot)) { error = "cargoFull"; break; }
                    resources[resource] -= m.Count;
                }
                else
                {
                    if (p.Cargo.Count(resource) < m.Count || resources.GetValueOrDefault(resource) > int.MaxValue - m.Count) { error = "resources"; break; }
                    resources[resource] = resources.GetValueOrDefault(resource) + m.Count;
                }
                next = state with { Resources = resources, Revision = state.Revision + 1 }; error = null;
                break;
            default: error = "badAct"; break;
        }
        if (error is not null) { Refuse(error); return; }
        next = next with { Requests = [.. state.Requests ?? [], m.Request] };
        p.Workshop = next; var oldCredits = p.Credits; p.Credits = credits;
        if (m.Act == "transfer")
        {
            if (m.ToCargo) p.Cargo.Add(m.Resource!, m.Count); else p.Cargo.Remove(m.Resource!, m.Count);
        }
        if (!CommitMech(p))
        {
            p.Workshop = state; p.Credits = oldCredits;
            if (m.Act == "transfer") { if (m.ToCargo) p.Cargo.Remove(m.Resource!, m.Count); else p.Cargo.Add(m.Resource!, m.Count); }
            Refuse("saveFailed"); return;
        }
        SendCargo(p); SendWorkshop(p, request: m.Request);
    }

    public bool CommitMech(Player p) => _accounts is null || p.AccountId is null || _accounts.SaveDurable(p.AccountId, ProfileOf(p));

    public bool RecordMechResult(Player p, MechResult result, MechRules rules, MechRandom random)
    {
        var state = p.Workshop;
        if (state.Results.Any(r => r.Session == result.Session)) return true;
        var first = result.Won && !state.Completed.Contains(result.Mission);
        var next = MechCatalog.CommitResult(rules,state,result,random,out result);
        var oldCredits = p.Credits;
        var campaign = rules.Workshop?.Campaign;
        var log = campaign is null ? null : p.StoryOf(campaign);
        var hadDone = log?.Done.Contains(result.Mission) == true;
        var hadFlag = log?.Flags.Contains(Protocol.SortieWonFlag) == true;
        if (first) { log?.Done.Add(result.Mission); if (result.Mission == MechRules.FirstSortie) log?.Flags.Add(Protocol.SortieWonFlag); }
        p.Workshop = next; p.Credits += result.Credits;
        if (!CommitMech(p))
        {
            p.Workshop = state; p.Credits = oldCredits;
            if (!hadDone) log?.Done.Remove(result.Mission);
            if (!hadFlag) log?.Flags.Remove(Protocol.SortieWonFlag);
            return false;
        }
        SendCargo(p); SendMissions(p); return true;
    }
}
