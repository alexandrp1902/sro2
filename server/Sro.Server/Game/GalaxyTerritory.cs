using Sro.Server.Accounts;
using Sro.Server.Net;
using Sro.Sim.Mech;

namespace Sro.Server.Game;

public sealed record TerritoryReady(string Account, string Name, string[] Squad, bool Ready);
public sealed record TerritoryPreparation(string Id, string Leader, string Object, string Operation,
    DateTimeOffset Expires, TerritoryReady[] Players);

public sealed partial class Galaxy
{
    private TerritoryConfig? _territoryConfig;
    private readonly TerritoryStore? _territoryStore;
    private readonly Func<DateTimeOffset> _territoryClock;
    private readonly Dictionary<string,TerritoryPreparation> _preparations = [];
    private DateTimeOffset _territoryStepped;
    public TerritoryStore? TerritoryStorage => _territoryStore;
    public bool ApplyTerritoryConfig(TerritoryConfig next)
    {
        if (next.Validate() is not null || next.ValidateRules(Balance.Mechs) is not null || _territoryConfig is not { } current) return false;
        // Calendar and persistent object identities are a migration, not a balance edit.
        if (next.SeasonEpoch != current.SeasonEpoch || next.SeasonSeconds != current.SeasonSeconds || next.DaySeconds != current.DaySeconds
            || !next.Objects.Select(o=>o.Id).Order().SequenceEqual(current.Objects.Select(o=>o.Id).Order()))
        { _log.LogWarning("Territory calendar/identity change requires a migration; keeping previous configuration"); return false; }
        _territoryConfig = next;
        BroadcastTerritory();
        return true;
    }
    private void RestoreTerritoryConnections()
    {
        if(_territoryStore is not {Disabled:false,Waiting:false} store || _territoryConfig is null || store.World.Fights.Length==0) return;
        var fights=store.World.Fights.Select(f=>f with {Participants=f.Participants.Select(p=>p with {LostAt=p.LostAt??f.Deadline.AddSeconds(-(f.FrozenConfig??_territoryConfig).ActivationSeconds)}).ToArray()}).ToArray();
        store.Commit(store.World with {Fights=fights});
    }
    public TerritoryFight? TerritoryOf(string? account) => account is null ? null : _territoryStore?.World.Fights.FirstOrDefault(f=>f.Participants.Any(p=>p.Account==account));
    private Player? TerritoryPlayer(string account) => Rooms.SelectMany(r=>r.Pilots).FirstOrDefault(p=>p.AccountId==account);
    private Room? TerritoryRoom(Player p) => Rooms.FirstOrDefault(r=>r.Pilots.Contains(p));
    private bool TerritoryUnlocked(Player p) => StorySkip is null && p.Workshop.Completed.Contains("majorAssault");
    private bool TerritoryDocked(Player p) => p.Docked && p.DockedPlace==_territoryConfig?.Place;

    private void SendTerritory(Player p,string? refused=null,string? request=null,string? receipt=null)
    {
        if(_territoryStore is not { } store || _territoryConfig is not { } config) return;
        // Active snapshots contain server RNG and frozen rules; never send the persistence model to a client.
        var visible=store.World with { Fights=[],Claims=store.World.Claims.Where(c=>c.Account==p.AccountId).ToArray(),Requests=[] };
        p.Connection?.Send(new TerritoryStateMsg(visible,config,p.Territory,TerritoryUnlocked(p),TerritoryDocked(p),
            _preparations.Values.FirstOrDefault(prep=>prep.Players.Any(a=>a.Account==p.AccountId)),refused,request,store.Waiting||store.Disabled,receipt,p.AccountId));
    }
    private void BroadcastTerritory()
    { foreach(var p in Rooms.SelectMany(r=>r.Pilots).Where(p=>p.Connection is not null && p.AccountId is not null)) SendTerritory(p); }
    private void SyncTerritoryProfiles()
    {
        foreach(var p in Rooms.SelectMany(r=>r.Pilots))
            if(p.AccountId is { } account && _territoryStore!.AppliedAccounts.Contains(account) && _accounts?.Profile(account) is { } profile)
            {
                p.Workshop=profile.Workshop??p.Workshop;p.Territory=profile.Territory??p.Territory;p.Credits=profile.Credits;
                if(_territoryStore.AppliedCargoAccounts.Contains(account)) {p.Cargo.Clear();foreach(var item in profile.Cargo.Where(i=>i.Value>0))p.Cargo.Add(item.Key,item.Value);}
                TerritoryRoom(p)?.SyncTerritoryStory(p,profile);
            }
    }
    private AccountProfile? TerritoryProfileOf(string account)
    {
        if(TerritoryPlayer(account) is { } player && TerritoryRoom(player) is { } room) return room.ProfileOf(player);
        return _accounts?.Profile(account);
    }
    private bool CommitTerritory(TerritoryWorld next,params TerritoryProfilePatch[] patches)
    {
        if(_territoryStore is not { } store) return false;
        // Bring live space state into the account store before applying a delta in the WAL.
        foreach(var patch in patches) if(TerritoryProfileOf(patch.Account) is { } profile) _accounts?.Save(patch.Account,profile);
        var ok=store.Commit(next,patches);
        if(ok) {SyncTerritoryProfiles();BroadcastTerritory();}
        return ok;
    }
    private static string RequestKey(string account,string request)=>account+":"+request;

    public void Territory(IClientConnection connection,TerritoryActMsg m)
    {
        if(RoomOf(connection)?.PlayerOf(connection) is not {AccountId:not null} p || _territoryStore is not { } store || _territoryConfig is not { } config) return;
        StepTerritory(force:true);
        void Refuse(string code)=>SendTerritory(p,code,m.RequestId);
        if(m.Act=="read") {SendTerritory(p);return;}
        if(store.Disabled||store.Waiting) {Refuse("saveFailed");return;}
        if(m.RequestId is not {Length:>0 and <=80} request) {Refuse("badAct");return;}
        var key=RequestKey(p.AccountId,request);
        if(store.World.Requests.TryGetValue(key,out var previous))
        {SendTerritory(p,request:request,receipt:previous);if(TerritoryOf(p.AccountId) is { } running) SendTerritoryFight(running);return;}
        if(m.Revision!=store.World.Revision||m.Season!=store.World.Season) {Refuse("staleRevision");return;}
        if(!TerritoryUnlocked(p)) {Refuse("territoryLocked");return;}
        if(!TerritoryDocked(p)) {Refuse("territoryDock");return;}
        if(InMech(p)) {Refuse("mechBusy");return;}
        if(m.Act is "prepare" or "ready" or "start" or "cancel")
        {
            PrepareTerritory(p,m,key,Refuse);return;
        }
        var world=TerritoryStore.Clone(store.World);var profile=p.Territory;var workshop=p.Workshop;
        var resources=new Dictionary<string,int>(workshop.Resources);var credits=0;
        IReadOnlyDictionary<string,int>? cargo=null;
        var story=TerritoryRoom(p)!.ProfileOf(p).Story;
        var evaMission=config.Eva?.MissionList.ElementAtOrDefault(profile.EvaStep);
        var receipt=request;
        switch(m.Act)
        {
            case "claim":
                var claim=world.Claims.FirstOrDefault(c=>c.Id==m.Claim&&c.Account==p.AccountId);
                if(claim is null) {Refuse("claimMissing");return;}
                receipt=claim.Id;
                if(!claim.Claimed)
                {
                    credits=claim.Reward.Credits;
                    resources["titanium"]=checked(resources.GetValueOrDefault("titanium")+claim.Reward.Titanium);
                    resources["energy"]=checked(resources.GetValueOrDefault("energy")+claim.Reward.Energy);
                    resources["rareMetal"]=checked(resources.GetValueOrDefault("rareMetal")+claim.Reward.RareMetal);
                    if(claim.Reward.Part is { } part) workshop=MechCatalog.Grant(Balance.Mechs,workshop,claim.Id,[part]);
                    if(claim.Reward.Title is { } title) profile=profile with {Titles=[..profile.Titles??[],title]};
                    world=world with {Claims=world.Claims.Select(c=>c.Id==claim.Id?c with {Claimed=true}:c).ToArray()};
                }
                break;
            case "service":
            case "emergency":
                var debts=new Dictionary<string,TerritoryDebt>(profile.Debts??[]);
                if(m.Mech is null||!workshop.Mechs.Any(mech=>mech.Id==m.Mech)||!debts.TryGetValue(m.Mech,out var debt)) {Refuse("serviceMissing");return;}
                if(m.Act=="emergency")
                {
                    if(profile.EmergencyAt is { } used && _territoryClock()<used.AddHours(24)) {Refuse("emergencyCooldown");return;}
                    profile=profile with {EmergencyAt=_territoryClock()};
                }
                else
                {
                    if(resources.GetValueOrDefault("titanium")<debt.Titanium||resources.GetValueOrDefault("energy")<debt.Energy) {Refuse("resources");return;}
                    resources["titanium"]-=debt.Titanium;resources["energy"]-=debt.Energy;
                }
                debts.Remove(m.Mech);profile=profile with {Debts=debts};break;
            case "upgrade":
                if(profile.Level>=3) {Refuse("maxLevel");return;}
                var high=profile.Level==2;var price=high?10000:5000;var titanium=high?80:40;var energy=high?40:20;var rare=high?10:0;
                if(p.Credits<price||resources.GetValueOrDefault("titanium")<titanium||resources.GetValueOrDefault("energy")<energy||resources.GetValueOrDefault("rareMetal")<rare) {Refuse("resources");return;}
                credits=-price;resources["titanium"]-=titanium;resources["energy"]-=energy;resources["rareMetal"]=resources.GetValueOrDefault("rareMetal")-rare;
                profile=profile with {Level=profile.Level+1};break;
            case "evaAccept":
                if(profile.EvaStep>=3||profile.EvaAccepted is not null) {Refuse("evaUnavailable");return;}
                if(config.Eva is { } chain && chain.Next(p.Story.GetValueOrDefault("novaFront")?.Done??[])?.Id!=evaMission?.Id) {Refuse("evaUnavailable");return;}
                profile=profile with {EvaAccepted=_territoryClock(),EvaDone=false};
                if(evaMission is not null) {var logs=new Dictionary<string,StoryProgress>(story??new Dictionary<string,StoryProgress>());logs["novaFront"]=new(logs.GetValueOrDefault("novaFront")?.Done??[],Lines:evaMission.Lines?.Accept);story=logs;}
                break;
            case "evaDeliver":
                if(profile.EvaStep!=2||profile.EvaAccepted is null||profile.EvaDone||p.Cargo.Count("titanium")<20||p.Cargo.Count("energy")<10) {Refuse("resources");return;}
                var delivered=new Dictionary<string,int>(p.Cargo.Items);delivered["titanium"]-=20;delivered["energy"]-=10;cargo=delivered;
                profile=profile with {EvaDone=true};break;
            case "evaClaim":
                if(!profile.EvaDone||profile.EvaStep>=3) {Refuse("evaUnavailable");return;}
                credits=evaMission?.Reward??new[]{1000,1500,2000}[profile.EvaStep];
                if(evaMission is not null) {var logs=new Dictionary<string,StoryProgress>(story??new Dictionary<string,StoryProgress>());logs["novaFront"]=new([..logs.GetValueOrDefault("novaFront")?.Done??[],evaMission.Id],Lines:evaMission.Lines?.Done);story=logs;}
                profile=profile with {EvaStep=profile.EvaStep+1,EvaAccepted=null,EvaDone=false};break;
            default: Refuse("badAct");return;
        }
        workshop=workshop with {Resources=resources,Revision=workshop.Revision+1};
        world.Requests[key]=receipt;world=world with {Revision=world.Revision+1};
        if(!CommitTerritory(world,new TerritoryProfilePatch(p.AccountId,credits,workshop,profile,cargo,story))) {Refuse("saveFailed");return;}
        if(evaMission is not null&&m.Act is "evaAccept" or "evaClaim") TerritoryRoom(p)?.TerritorySay(p,evaMission,m.Act=="evaClaim");
        TerritoryRoom(p)?.SendWorkshop(p);SendTerritory(p,request:request,receipt:receipt);
    }

    private string? CheckTerritorySquad(Player p,string[] squad,bool coop)
    {
        if(!TerritoryUnlocked(p)||!TerritoryDocked(p)||InMech(p)) return "territoryDock";
        if(squad.Length!=(coop?2: squad.Length==1?1:2)||squad.Distinct().Count()!=squad.Length) return "squadRequired";
        foreach(var id in squad)
        {
            var mech=p.Workshop.Mechs.FirstOrDefault(m=>m.Id==id);
            if(mech is null) return "ownership";
            if(p.Territory.Debts?.ContainsKey(id)==true) return "serviceDebt";
            if(MechCatalog.Resolve(Balance.Mechs,p.Workshop,mech) is not { } def||MechCatalog.Fitting(Balance.Mechs,def) is not null) return "incomplete";
        }
        return null;
    }
    private void PrepareTerritory(Player p,TerritoryActMsg m,string requestKey,Action<string> refuse)
    {
        var store=_territoryStore!;var config=_territoryConfig!;var now=_territoryClock();
        var prep=_preparations.GetValueOrDefault(m.Preparation??"");
        if(m.Act=="prepare")
        {
            if(now>=store.World.SeasonEnd) {refuse("seasonTransition");return;}
            var obj=store.World.Objects.FirstOrDefault(o=>o.Id==m.Object);
            if(obj is null||!config.Operations.ContainsKey(m.Operation??"")||!TerritoryRules.Accessible(config,store.World.Objects,obj.Id)) {refuse("operationUnavailable");return;}
            if(obj.Active is not null) {refuse("objectBusy");return;}
            if(!OperationAvailable(obj,m.Operation!)) {refuse("operationUnavailable");return;}
            Player? partner=null;
            if(m.Partner is {Length:>0} wanted)
            {
                partner=Rooms.SelectMany(r=>r.Pilots).FirstOrDefault(other=>other.AccountId==wanted||other.Name==wanted);
                if(partner is null||partner==p||partner.AccountId is null||partner.Connection is null||!TerritoryUnlocked(partner)||!TerritoryDocked(partner)||InMech(partner)) {refuse("partnerUnavailable");return;}
            }
            if(CheckTerritorySquad(p,m.Squad??[],partner is not null) is { } error) {refuse(error);return;}
            foreach(var old in _preparations.Values.Where(a=>a.Players.Any(b=>b.Account==p.AccountId||b.Account==partner?.AccountId)).ToArray()) _preparations.Remove(old.Id);
            prep=new(Guid.NewGuid().ToString("N"),p.AccountId!,obj.Id,m.Operation!,now.AddSeconds(60),
                partner is null?[new(p.AccountId!,p.Name,m.Squad!,true)]:[new(p.AccountId!,p.Name,m.Squad!,true),new(partner.AccountId!,partner.Name,[],false)]);
            _preparations[prep.Id]=prep;
        }
        else
        {
            if(prep is null||now>=prep.Expires||!prep.Players.Any(a=>a.Account==p.AccountId)) {refuse("invitationExpired");return;}
            if(m.Act=="cancel") _preparations.Remove(prep.Id);
            else if(m.Act=="ready")
            {
                if(CheckTerritorySquad(p,m.Squad??[],prep.Players.Length==2) is { } error) {refuse(error);return;}
                prep=prep with {Players=prep.Players.Select(a=>a.Account==p.AccountId?a with {Squad=m.Squad!,Ready=true}:a).ToArray()};_preparations[prep.Id]=prep;
            }
            else if(m.Act=="start")
            {
                if(prep.Leader!=p.AccountId||prep.Players.Any(a=>!a.Ready)) {refuse("notReady");return;}
                if(now>=store.World.SeasonEnd) {refuse("seasonTransition");return;}
                var obj=store.World.Objects.First(o=>o.Id==prep.Object);
                if(obj.Active is not null) {refuse("objectBusy");return;}
                if(!OperationAvailable(obj,prep.Operation)||!TerritoryRules.Accessible(config,store.World.Objects,obj.Id)) {refuse("operationUnavailable");return;}
                foreach(var a in prep.Players)
                {
                    var pilot=TerritoryPlayer(a.Account);
                    if(pilot is null||pilot.Connection is null) {refuse("partnerUnavailable");return;}
                    if(CheckTerritorySquad(pilot,a.Squad,prep.Players.Length==2) is { } error) {refuse(error);return;}
                }
                var fight=BuildTerritoryFight(prep,m.RequestId!);
                var next=TerritoryStore.Clone(store.World);
                next.Requests[requestKey]=fight.Id;
                next=next with {Revision=next.Revision+1,Fights=[..next.Fights,fight],Objects=next.Objects.Select(o=>o.Id==obj.Id?o with {Active=fight.Id,Revision=o.Revision+1}:o).ToArray()};
                if(!CommitTerritory(next)) {refuse("saveFailed");return;}
                _preparations.Remove(prep.Id);SendTerritoryFight(fight);SendTerritory(p,request:m.RequestId,receipt:fight.Id);BroadcastTerritory();return;
            }
        }
        var world=TerritoryStore.Clone(store.World);world.Requests[requestKey]=prep!.Id;world=world with {Revision=world.Revision+1};
        if(!CommitTerritory(world)) {refuse("saveFailed");return;}
        SendTerritory(p,request:m.RequestId,receipt:prep.Id);BroadcastTerritory();
    }
    private static bool OperationAvailable(TerritoryObject obj,string operation) => operation switch
    {
        "assault" or "sabotage"=>obj.Owner==TerritoryRules.Corporation,
        "defense"=>obj.Owner==TerritoryRules.Resistance&&obj.DefenseAt is not null,
        "escort"=>obj.Owner==TerritoryRules.Resistance,
        "rescue"=>true,_=>false,
    };
    private TerritoryFight BuildTerritoryFight(TerritoryPreparation prep,string request)
    {
        var config=_territoryConfig!;var def=config.Objects.First(o=>o.Id==prep.Object);var op=config.Operations[prep.Operation];var id=Guid.NewGuid().ToString("N");
        var variant=Convert.ToInt32(id[..2],16)%2;var units=new Dictionary<string,MechUnitDef>(Balance.Mechs.UnitMap);
        var slots=new[]{new MechPoint(1,10),new(3,10),new(1,8),new(3,8)};
        var owned=prep.Players.SelectMany(p=>p.Squad.Select(mech=>(Player:p,Mech:TerritoryPlayer(p.Account)!.Workshop.Mechs.First(m=>m.Id==mech)))).ToArray();
        var spawns=owned.Select((a,i)=>{var pilot=TerritoryPlayer(a.Player.Account)!;units[a.Mech.Id]=MechCatalog.Resolve(Balance.Mechs,pilot.Workshop,a.Mech)!;return new MechSpawn(a.Mech.Id,slots[i].X,slots[i].Y,0);}).ToArray();
        var enemySlots=new[]{new MechPoint(7,1),new(9,1),new(8,3),new(10,3)};
        var roster=owned.Length==4?op.Coop:owned.Length==1?op.Emergency:op.Solo;
        var enemies=roster.Select((unit,i)=>new MechSpawn(unit,enemySlots[i].X,enemySlots[i].Y,4)).ToArray();
        var route=Enumerable.Range(2,8).Reverse().Select(y=>new MechPoint(2,y)).ToArray();
        var evacuation=new[]{new MechPoint(1,10),new(2,10),new(3,10)};
        var objective=op.Objective switch
        {
            "capture"=>new MechObjective("capture",2,Zone:[new(5,5),new(6,5),new(5,6)],FullRounds:true),
            "defend"=>new("defend",6,new("mine",2,9,0)),
            "escort"=>new("escort",Object:new("tractor",2,10,0),Route:route),
            "sabotage"=>new("sabotage",Object:new("mine",8,2,0),Evacuation:evacuation),
            _=>new("rescue",Object:new("mine",5,5,0),Evacuation:evacuation),
        };
        var mission=new MechMission(op.Name+" · "+def.Name,op.Goal,def.Fields[variant],spawns,enemies,op.Credits,
            Objective:objective,ObjectiveXp:20,Biome:def.Biome);
        var rules=Balance.Mechs with {Units=units,Missions=new Dictionary<string,MechMission>(Balance.Mechs.MissionMap){[id]=mission}};
        if(rules.Validate() is { } error) throw new InvalidDataException(error);
        var battle=new MechBattle(rules,id,(uint)_mechSeeds.Next(1,int.MaxValue));
        for(var i=0;i<owned.Length;i++)
        {var u=battle.Unit($"p{i+1}")!;u.Owner=owned[i].Mech.Id;u.AccountOwner=owned[i].Player.Account;u.Skills=owned[i].Mech.Skills??[];}
        if(owned.Length==4) battle.ActivationOrder=["p1","p3","p2","p4"];
        return new(id,prep.Object,prep.Operation,_territoryStore!.World.Season,battle.Save(),
            prep.Players.Select(p=>new TerritoryParticipant(p.Account,p.Name,p.Squad)).ToArray(),_territoryClock().AddSeconds(config.ActivationSeconds),
            _territoryStore.World.Objects.Any(o=>o.Owner==TerritoryRules.Resistance&&config.Objects.First(d=>d.Id==o.Id).Type=="repair"),
            Receipts:[],StartRequest:request,FrozenConfig:config);
    }
    private MechStateMsg TerritoryFightState(TerritoryFight f,string account) => new(MechBattle.Restore(f.Battle).View(),f.Battle.Rules,f.Id,f.Revision,account,f.Deadline,f.Participants);
    private void SendTerritoryFight(TerritoryFight fight,List<MechEvent>? events=null)
    {
        foreach(var participant in fight.Participants)
            if(TerritoryPlayer(participant.Account)?.Connection is { } c)
            {if(events is {Count:>0}) c.Send(new MechEventsMsg(events));c.Send(TerritoryFightState(fight,participant.Account));}
    }

    private bool TerritoryMech(IClientConnection c,MechActMsg m,Player p)
    {
        var store=_territoryStore;
        if(store is null) return false;
        var fight=TerritoryOf(p.AccountId);
        if(fight is null) return false;
        if(store.Disabled||store.Waiting) {c.Send(new MechRefusedMsg("saveFailed"));return true;}
        if(m.Request is { } request&&fight.Receipts?.TryGetValue(RequestKey(p.AccountId!,request),out var known)==true) {c.Send(known);return true;}
        if(m.Session!=fight.Id||m.Revision!=fight.Revision||m.Request is not {Length:>0 and <=80})
        {c.Send(new MechRefusedMsg("staleRevision"));c.Send(TerritoryFightState(fight,p.AccountId!));return true;}
        var battle=MechBattle.Restore(fight.Battle);var events=new List<MechEvent>();var participants=fight.Participants.ToArray();
        if(m.Act is "quit" or "leave")
        {
            participants=participants.Select(a=>a.Account==p.AccountId?a with {Retreat=m.Act=="quit",Left=m.Act=="leave",LostAt=m.Act=="leave"?_territoryClock():a.LostAt}:a).ToArray();
            if(participants.All(a=>a.Retreat||a.Left||a.LostAt is not null)) battle.Surrender();
        }
        else
        {
            if(participants.First(a=>a.Account==p.AccountId).Left||battle.Current?.AccountOwner!=p.AccountId
                ||m.Act=="select"&&battle.Unit(m.Unit)?.AccountOwner!=p.AccountId)
            {c.Send(new MechRefusedMsg("ownership"));return true;}
            var oldCurrent=battle.Current!.Id;var oldRound=battle.Round;
            var error=battle.Apply(new(m.Act??"",m.X,m.Y,m.Dir,m.Target,m.Part,m.Unit,m.Hand),events);
            if(error is not null) {c.Send(new MechRefusedMsg(error));return true;}
            var activated=battle.Unit(oldCurrent)!.Activated||battle.Over||battle.Round!=oldRound;
            if(activated) participants=participants.Select(a=>a.Account==p.AccountId?a with {Participated=true}:a).ToArray();
            battle.RunEnemies(events);
            if(activated) fight=fight with {Deadline=_territoryClock().AddSeconds((fight.FrozenConfig??_territoryConfig!).ActivationSeconds)};
        }
        var nextFight=fight with {Battle=battle.Save(),Participants=participants,Revision=fight.Revision+1,Receipts=new(fight.Receipts??[])};
        nextFight.Receipts![RequestKey(p.AccountId!,m.Request!)]=TerritoryFightState(nextFight,p.AccountId!);
        SaveTerritoryFight(nextFight,events);
        return true;
    }
    private void SaveTerritoryFight(TerritoryFight fight,List<MechEvent> events)
    {
        var world=TerritoryStore.Clone(_territoryStore!.World);
        world=world with {Fights=world.Fights.Select(f=>f.Id==fight.Id?fight:f).ToArray(),Revision=world.Revision+1};
        if(!CommitTerritory(world)) {foreach(var a in fight.Participants) TerritoryPlayer(a.Account)?.Connection?.Send(new MechRefusedMsg("saveFailed"));return;}
        SendTerritoryFight(fight,events);
        if(fight.Battle.Winner is not null) EndTerritoryFight(fight);
    }
    private void EndTerritoryFight(TerritoryFight fight)
    {
        var store=_territoryStore!;var config=fight.FrozenConfig??_territoryConfig!;var world=TerritoryStore.Clone(store.World);
        var battle=MechBattle.Restore(fight.Battle);var won=battle.Winner==MechBattle.PlayerSide;var op=config.Operations[fight.Operation];
        var obj=world.Objects.First(o=>o.Id==fight.Object);
        var fortified=config.Objects.First(d=>d.Id==obj.Id).Links.Any(id=>world.Objects.Any(o=>o.Id==id&&o.Owner==TerritoryRules.Resistance&&config.Objects.First(d=>d.Id==id).Type=="fort"));
        var loss=fight.Operation=="defense"&&fortified?-15:op.LoseControl;
        world=world with {Objects=world.Objects.Select(o=>o.Id==obj.Id?TerritoryRules.Change(o,won?op.WinControl:loss) with {Active=null,DefenseAt=fight.Operation=="defense"?null:o.DefenseAt}:o).ToArray(),
            Fights=world.Fights.Where(f=>f.Id!=fight.Id).ToArray(),Revision=world.Revision+1};
        var patches=new List<TerritoryProfilePatch>();
        foreach(var participant in fight.Participants)
        {
            var profile=TerritoryProfileOf(participant.Account)!;var workshop=profile.Workshop!;var territory=profile.Territory??new();
            var debts=new Dictionary<string,TerritoryDebt>(territory.Debts??[]);var mechs=workshop.Mechs.ToArray();
            var experience=new List<MechExperience>();var random=new MechRandom(fight.Battle.Random^0xA5A5A5A5u);
            foreach(var unit in battle.Units.Where(u=>u.AccountOwner==participant.Account))
            {
                var index=Array.FindIndex(mechs,m=>m.Id==unit.Owner);if(index<0) continue;
                var gained=unit.Xp+(won&&unit.Alive?battle.Rules.ProgressionDef.VictoryXp+battle.Mission.ObjectiveXp:0);
                mechs[index]=MechDevelopment.Gain(battle.Rules,mechs[index],unit.Def,gained,random,out var skills);
                experience.Add(new(unit.Owner!,unit.Def.Name,gained,mechs[index].Level,mechs[index].Xp,skills));
                var debt=TerritoryRules.Service(unit.Alive?unit.Max.Sum()-unit.Hp.Sum():unit.Max.Sum(),unit.Max.Sum(),fight.Discount);
                if(debt.Titanium>0||debt.Energy>0) debts[unit.Owner!]=debt;
            }
            var eligible=won&&participant.Participated;
            var newObject=world.Objects.First(o=>o.Id==fight.Object);
            var result=new MechResult(fight.Id,fight.Operation,won,battle.Reason??(won?"objectiveComplete":"squadDestroyed"),eligible?op.Credits:0,[],experience.ToArray(),fight.StartRequest,
                new TerritoryOutcome(config.Objects.First(o=>o.Id==fight.Object).Name,obj.Control,newObject.Control,obj.Owner,newObject.Owner,eligible?op.Contribution:0,debts));
            workshop=workshop with {Mechs=mechs,Results=[..workshop.Results,result],Revision=workshop.Revision+1};
            territory=territory with {Debts=debts};
            if(eligible)
            {
                world.Contributions[participant.Account]=world.Contributions.GetValueOrDefault(participant.Account)+op.Contribution;
                var end=config.SeasonEpoch.AddSeconds((long)fight.Season*config.SeasonSeconds);
                var at=_territoryClock()<end?_territoryClock():end.AddTicks(-1);
                var day=$"{fight.Season}:{(int)((at-config.SeasonEpoch.AddSeconds((long)(fight.Season-1)*config.SeasonSeconds)).TotalSeconds/config.DaySeconds)}";
                if(!world.Days.TryGetValue(day,out var contributions)) world.Days[day]=contributions=[];
                contributions[participant.Account]=contributions.GetValueOrDefault(participant.Account)+op.Contribution;
                if(territory.EvaAccepted is { } accepted&&accepted<_territoryClock()&&
                    (territory.EvaStep==0&&fight.Operation=="assault"&&config.Objects.First(o=>o.Id==fight.Object).Type=="factory"
                    ||territory.EvaStep==1&&fight.Operation=="defense")) territory=territory with {EvaDone=true};
            }
            patches.Add(new(participant.Account,result.Credits,workshop,territory));
        }
        if(!CommitTerritory(world,patches.ToArray())) {foreach(var a in fight.Participants) TerritoryPlayer(a.Account)?.Connection?.Send(new MechRefusedMsg("saveFailed"));return;}
        foreach(var a in fight.Participants)
            if(TerritoryPlayer(a.Account) is { } p)
            {var result=p.Workshop.Results.First(r=>r.Session==fight.Id);p.Connection?.Send(new MechEndMsg(won,result.Credits,false,Result:result));TerritoryRoom(p)?.SendWorkshop(p);}
    }
    private void TerritoryResume(Player p)
    {
        if(_territoryStore is null||p.AccountId is null) return;
        StepTerritory(force:true);
        if(TerritoryOf(p.AccountId) is { } fight&&!_territoryStore.Waiting&&!_territoryStore.Disabled)
        {
            var participants=fight.Participants.Select(a=>a.Account==p.AccountId&&!a.Left?a with {LostAt=null}:a).ToArray();
            var next=fight with {Participants=participants};
            var world=_territoryStore.World with {Fights=_territoryStore.World.Fights.Select(f=>f.Id==next.Id?next:f).ToArray()};
            if(CommitTerritory(world)) SendTerritoryFight(next);
        }
        SendTerritory(p);
    }
    private void TerritoryLost(Player p)
    {
        if(TerritoryOf(p.AccountId) is not { } fight||_territoryStore!.Waiting||_territoryStore.Disabled) return;
        var next=fight with {Participants=fight.Participants.Select(a=>a.Account==p.AccountId?a with {LostAt=_territoryClock()}:a).ToArray()};
        CommitTerritory(_territoryStore.World with {Fights=_territoryStore.World.Fights.Select(f=>f.Id==next.Id?next:f).ToArray()});
    }

    public void StepTerritory(bool force=false)
    {
        if(_territoryStore is not { } store||_territoryConfig is not { } config||store.Disabled) return;
        var now=_territoryClock();if(!force&&(now-_territoryStepped).TotalSeconds<1) return;_territoryStepped=now;
        if(store.Waiting)
        {
            if(!store.Recover()) return;SyncTerritoryProfiles();BroadcastTerritory();
            foreach(var account in store.AppliedAccounts)
                if(TerritoryPlayer(account) is { } p && p.Workshop.Results.LastOrDefault(r=>store.EndedSessions.Contains(r.Session)) is { } result)
                {p.Connection?.Send(new MechEndMsg(result.Won,result.Credits,false,Result:result));TerritoryRoom(p)?.SendWorkshop(p);}
        }
        foreach(var expired in _preparations.Values.Where(p=>p.Expires<=now).ToArray()) _preparations.Remove(expired.Id);
        foreach(var saved in store.World.Fights.ToArray())
        {
            var frozen=saved.FrozenConfig??config;
            if(saved.Battle.Winner is not null) {EndTerritoryFight(saved);if(store.Waiting)return;continue;}
            var battle=MechBattle.Restore(saved.Battle);var fight=saved;var events=new List<MechEvent>();
            var participants=saved.Participants.Select(a=>
            {
                if(TerritoryPlayer(a.Account)?.Connection is null&&a.LostAt is null) a=a with {LostAt=now};
                if(a.LostAt is { } lost&&now>=lost.AddSeconds(frozen.DisconnectSeconds)) a=a with {Left=true};
                return a;
            }).ToArray();
            var allGone=participants.All(a=>a.Left);
            if(allGone||now>=store.World.SeasonEnd.AddSeconds(frozen.TransitionSeconds)) battle.Surrender();
            else if(battle.Current is { } current)
            {
                var owner=participants.FirstOrDefault(p=>p.Account==current.AccountOwner);
                if((owner?.LostAt is not null||owner?.Left==true)&&now>=fight.Deadline.AddSeconds(-frozen.ActivationSeconds+1))
                {
                    // One server activation per second; it never earns personal participation.
                    var before=current.Id;
                    foreach(var command in MechAi.Decide(battle))
                    {if(battle.Apply(command,events) is not null) battle.Apply(new("end"),events);if(battle.Current?.Id!=before||battle.Over)break;}
                    battle.RunEnemies(events);fight=fight with {Deadline=now.AddSeconds(frozen.ActivationSeconds)};
                }
                else if(now>=fight.Deadline)
                {battle.Apply(new("end",Unit:current.Id),events);battle.RunEnemies(events);fight=fight with {Deadline=now.AddSeconds(frozen.ActivationSeconds)};}
            }
            if(events.Count>0||battle.Over||!participants.SequenceEqual(saved.Participants))
            {
                fight=fight with {Battle=battle.Save(),Participants=participants,Revision=fight.Revision+1};SaveTerritoryFight(fight,events);if(store.Waiting)return;
            }
        }
        AdvanceTerritoryCalendar(now);
    }
    private void AdvanceTerritoryCalendar(DateTimeOffset now)
    {
        var store=_territoryStore!;var config=_territoryConfig!;var world=TerritoryStore.Clone(store.World);var changed=false;
        while(true)
        {
            var start=config.SeasonEpoch.AddSeconds((long)(world.Season-1)*config.SeasonSeconds);
            for(var day=0;day<config.SeasonSeconds/config.DaySeconds;day++)
            {
                var key=$"{world.Season}:{day}";var boundary=start.AddSeconds((long)(day+1)*config.DaySeconds);
                if(boundary>now||world.ProcessedDays.Contains(key)||boundary==world.SeasonEnd&&world.Fights.Length>0) continue;
                var contributions=world.Days.GetValueOrDefault(key)??[];
                var pool=new TerritoryReward(world.Objects.Count(o=>o.Owner==TerritoryRules.Resistance)*200);
                foreach(var obj in world.Objects.Where(o=>o.Owner==TerritoryRules.Resistance))
                    pool=config.Objects.First(d=>d.Id==obj.Id).Type switch
                    {"mine"=>pool with {Titanium=pool.Titanium+12},"factory"=>pool with {Credits=pool.Credits+400},"power"=>pool with {Energy=pool.Energy+8},"warehouse"=>pool with {RareMetal=pool.RareMetal+4},_=>pool};
                var credits=TerritoryRules.Distribute(pool.Credits,contributions);var titanium=TerritoryRules.Distribute(pool.Titanium,contributions);
                var energy=TerritoryRules.Distribute(pool.Energy,contributions);var rare=TerritoryRules.Distribute(pool.RareMetal,contributions);
                foreach(var account in credits.Keys) world=world with {Claims=[..world.Claims,new TerritoryClaim("supply:"+key+":"+account,account,world.Season,"supply",new(credits[account],titanium[account],energy[account],rare[account]))]};
                world=world with {ProcessedDays=[..world.ProcessedDays,key]};changed=true;
            }
            if(now<world.SeasonEnd||world.Fights.Length>0) break;
            var archived=world.Season;
            world=world with {Archives=[..world.Archives,new TerritoryArchive(archived,world.Objects.ToArray(),new(world.Contributions),world.SeasonEnd)]};
            foreach(var account in world.Contributions.Where(p=>p.Value>0)) world=world with {Claims=[..world.Claims,new TerritoryClaim($"final:{archived}:{account.Key}",account.Key,archived,"final",TerritoryRules.FinalReward(account.Value,archived))]};
            world=world with {Season=world.Season+1,SeasonEnd=world.SeasonEnd.AddSeconds(config.SeasonSeconds),Contributions=[],
                Objects=world.Objects.Select(o=>TerritoryRules.Reset(o,config.Objects.First(d=>d.Id==o.Id).StartControl)).ToArray(),NextStrike=now.AddSeconds(config.StrikeSeconds)};changed=true;
        }
        // At most one missed strike; the next appointment is based on current time.
        foreach(var obj in world.Objects.Where(o=>o.DefenseAt<=now&&o.Active is null).OrderBy(o=>o.Id).Take(1).ToArray())
        {
            var fortified=config.Objects.First(d=>d.Id==obj.Id).Links.Any(id=>world.Objects.Any(o=>o.Id==id&&o.Owner==TerritoryRules.Resistance&&config.Objects.First(d=>d.Id==id).Type=="fort"));
            world=world with {Objects=world.Objects.Select(o=>o.Id==obj.Id?TerritoryRules.Change(o,fortified?-15:-20) with {DefenseAt=null}:o).ToArray(),NextStrike=now.AddSeconds(config.StrikeSeconds)};changed=true;
        }
        if(now>=world.NextStrike&&!world.Objects.Any(o=>o.DefenseAt is not null))
        {
            var target=world.Objects.Where(o=>o.Owner==TerritoryRules.Resistance).OrderBy(o=>o.Control).ThenBy(o=>o.Id,StringComparer.Ordinal).FirstOrDefault();
            if(target is not null) world=world with {Objects=world.Objects.Select(o=>o.Id==target.Id?o with {DefenseAt=now.AddSeconds(config.StrikeSeconds),Revision=o.Revision+1}:o).ToArray()};
            world=world with {NextStrike=now.AddSeconds(config.StrikeSeconds)};changed=true;
        }
        if(changed) CommitTerritory(world with {Revision=world.Revision+1});
    }
}
