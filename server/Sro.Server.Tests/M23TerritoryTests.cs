using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;
using Sro.Sim.Mech;

namespace Sro.Server.Tests;

public sealed class M23TerritoryTests : IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"sro-m23-"+Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly MechRules _rules;
    private readonly TerritoryConfig _config;
    private TerritoryStore _store;
    private Galaxy _galaxy;
    private DateTimeOffset _now;
    private int _connection;
    public M23TerritoryTests()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!Directory.Exists(Path.Combine(root.FullName,"shared")))root=root.Parent;
        _config=TerritoryConfig.Load(Path.Combine(root!.FullName,"shared","mech-territory.json")) with {Place="st:home"};
        _config=_config with {Eva=_config.Eva! with {Missions=_config.Eva.MissionList.Select(m=>m with {Place="st:home"}).ToArray()}};
        Assert.True(MechRules.TryParse(File.ReadAllText(Path.Combine(root.FullName,"shared","mechs.json")),out _rules,out var error),error);
        _rules=_rules with {Workshop=_rules.Workshop! with {Place="st:home"}};
        _now=_config.SeasonEpoch.AddSeconds(100);
        _accounts=new(Path.Combine(_dir,"accounts"),NullLogger.Instance,iterations:1000,autoFlush:false);
        _store=Store();_galaxy=Galaxy();
    }
    private TerritoryStore Store()=>new(Path.Combine(_dir,"world"),_config,_accounts,NullLogger.Instance,_now);
    private Galaxy Galaxy()=>new(TestBalance.Create(loot:new LootRules(Items:MechCatalog.ResourceIds.ToDictionary(id=>id,id=>new LootItem(id,Volume:1,Price:10))))
        with {GalaxySet=new GalaxyRules(StartSystem:"home",Systems:new Dictionary<string,SystemDef>{{"home",new("Home")}}),MechSet=_rules},
        NullLogger.Instance,_accounts,random:seed=>new Random(seed),territory:_config,territoryStore:_store,territoryClock:()=>_now);
    public void Dispose(){_accounts.Dispose();Directory.Delete(_dir,true);}
    private (FakeConnection C,Player P) Pilot(string name,bool unlocked=true)
    {
        var login=_accounts.Login(name,"secret");
        if(_accounts.Profile(login.Id) is null)
        {
            var w=MechCatalog.Prototype(_rules,MechWorkshopState.Empty);
            var first=w.Mechs[0];var parts=w.Parts.Select(p=>p with {Id=Guid.NewGuid().ToString("N")}).ToArray();
            string? Other(string? id)=>id is null?null:parts[Array.FindIndex(w.Parts,p=>p.Id==id)].Id;
            var second=new OwnedMech(Guid.NewGuid().ToString("N"),"Second",new(Other(first.Assembly.Body),Other(first.Assembly.Chassis),Other(first.Assembly.Left),Other(first.Assembly.Right),Other(first.Assembly.Backpack)));
            w=w with {Mechs=[first,second],Parts=[..w.Parts,..parts],Completed=unlocked?["firstSortie","majorAssault"]:[],Resources=new Dictionary<string,int>{{"titanium",200},{"energy",100},{"rareMetal",20}}};
            _accounts.SaveDurable(login.Id,new(30000,"light","pulse",["light"],[],new Dictionary<string,int>{{"titanium",20},{"energy",10}},System:"home",Place:"st:home",Workshop:w));
        }
        var c=new FakeConnection(++_connection);_galaxy.JoinAccount(c,login.Id,login.Name);var p=_galaxy.RoomOf(c)!.PlayerOf(c)!;p.Docked=true;p.DockedPlace="st:home";return(c,p);
    }
    private TerritoryActMsg Act(FakeConnection c,string act,string? obj=null,string? op=null,string[]? squad=null,string? prep=null,string? partner=null,string? claim=null,string? mech=null)
    {
        var m=new TerritoryActMsg(act,Guid.NewGuid().ToString("N"),_store.World.Revision,_store.World.Season,obj,op,squad,prep,partner,claim,mech);
        _galaxy.Territory(c,m);return m;
    }
    private TerritoryFight Start((FakeConnection C,Player P) a,string obj="np-02",string op="assault",(FakeConnection C,Player P)? b=null)
    {
        Act(a.C,"prepare",obj,op,a.P.Workshop.Mechs.Select(m=>m.Id).ToArray(),partner:b?.P.Name);
        var prep=a.C.Last<TerritoryStateMsg>().Preparation;Assert.NotNull(prep);
        if(b is { } other)Act(other.C,"ready",squad:other.P.Workshop.Mechs.Select(m=>m.Id).ToArray(),prep:prep.Id);
        Act(a.C,"start",prep:prep.Id);
        return _galaxy.TerritoryOf(a.P.AccountId)!;
    }
    private void EndActivation((FakeConnection C,Player P) p,string? unit=null,string act="end")
    {
        var f=_galaxy.TerritoryOf(p.P.AccountId)!;
        _galaxy.Mech(p.C,new(act,Session:f.Id,Revision:f.Revision,Unit:unit??f.Battle.Current,Request:Guid.NewGuid().ToString("N")));
    }
    private void Win(TerritoryFight f,params string[] participants)
    {
        // Deterministic fixture reaches an objective; normal battle goals are tested in Sro.Sim.
        var b=MechBattle.Restore(f.Battle);foreach(var enemy in b.Living("enemy"))enemy.Hp[0]=0;
        if(f.Operation=="assault") {b.Units[0].X=5;b.Units[0].Y=5;}
        else if(f.Operation=="sabotage") {b.ObjectiveUnit!.Hp[0]=0;}
        else if(f.Operation=="rescue") {b.Units[0].X=4;b.Units[0].Y=5;Assert.Null(b.Apply(new("interact"),[]));b.Units[0].X=1;b.Units[0].Y=10;}
        while(!b.Over)Assert.Null(b.Apply(new("end"),[]));
        var finished=f with {Battle=b.Save(),Participants=f.Participants.Select(p=>p with {Participated=participants.Contains(p.Account)}).ToArray()};
        Assert.True(_store.Commit(_store.World with {Fights=_store.World.Fights.Select(a=>a.Id==f.Id?finished:a).ToArray()}));
        _galaxy.StepTerritory(true);
    }
    [Fact]
    public void LockedAccountAndDebugSkipCannotStartOrClaim()
    {
        var a=Pilot("locked",false);Act(a.C,"prepare","np-02","assault",a.P.Workshop.Mechs.Select(m=>m.Id).ToArray());Assert.Equal("territoryLocked",a.C.Last<TerritoryStateMsg>().Refused);
        a=Pilot("debug");_galaxy.StorySkip="quietWar";Act(a.C,"upgrade");Assert.Equal("territoryLocked",a.C.Last<TerritoryStateMsg>().Refused);
    }
    [Fact]
    public void PreparationDoesNotReserve_BusyAndStaleStartsAreRejected()
    {
        var a=Pilot("leader");var b=Pilot("other");var f=Start(a);Assert.NotNull(f);Assert.Equal(f.Id,_store.World.Objects.First(o=>o.Id=="np-02").Active);
        Act(b.C,"prepare","np-02","assault",b.P.Workshop.Mechs.Select(m=>m.Id).ToArray());Assert.Equal("objectBusy",b.C.Last<TerritoryStateMsg>().Refused);
        var m=new TerritoryActMsg("prepare","stale",0,1,"np-07","assault",b.P.Workshop.Mechs.Select(m=>m.Id).ToArray());_galaxy.Territory(b.C,m);Assert.Equal("staleRevision",b.C.Last<TerritoryStateMsg>().Refused);
        Assert.Single(_store.World.Fights);
    }
    [Fact]
    public void CoopControlsOnlyOwnMachines_AlternatesAccounts_AndDuplicatesDoNotExtendTimer()
    {
        var a=Pilot("leader");var b=Pilot("partner");var f=Start(a,b:b);Assert.Equal(4,f.Battle.Units.Count(u=>u.View.AccountOwner is not null));
        EndActivation(b,unit:"p1");Assert.Equal("ownership",b.C.Last<MechRefusedMsg>().Code);
        var command=new MechActMsg("end",Session:f.Id,Revision:f.Revision,Unit:"p1",Request:"repeat");_galaxy.Mech(a.C,command);
        var next=_galaxy.TerritoryOf(a.P.AccountId)!;Assert.Equal(b.P.AccountId,next.Battle.Units.First(u=>u.View.Id==next.Battle.Current).View.AccountOwner);
        var deadline=next.Deadline;_now=_now.AddSeconds(5);_galaxy.Mech(a.C,command);Assert.Equal(deadline,_galaxy.TerritoryOf(a.P.AccountId)!.Deadline);
        Assert.Equal(next.Revision,_galaxy.TerritoryOf(a.P.AccountId)!.Revision);
    }
    [Fact]
    public void CoopResult_ChangesControlOnce_OnlyParticipantGetsContribution_AndRepeatPaysNothing()
    {
        var a=Pilot("leader");var b=Pilot("passive");var f=Start(a,b:b);Win(f,a.P.AccountId!);
        Assert.Equal(25,_store.World.Objects.First(o=>o.Id==f.Object).Control);Assert.Equal(10,_store.World.Contributions[a.P.AccountId!]);Assert.False(_store.World.Contributions.ContainsKey(b.P.AccountId!));
        Assert.Single(a.P.Workshop.Results);Assert.Single(b.P.Workshop.Results);var credits=a.P.Credits;
        _galaxy.Mech(a.C,new("end",Session:f.Id,Request:"repeatResult"));Assert.Equal(credits,a.P.Credits);
        Assert.Empty(_store.World.Fights);
    }
    [Fact]
    public void Restart_RestoresReserveRngAndDeadline_AndFiveMinuteAbsenceRetreats()
    {
        var a=Pilot("leader");var f=Start(a);EndActivation(a);f=_galaxy.TerritoryOf(a.P.AccountId)!;
        _store=Store();_galaxy=Galaxy();var back=_galaxy.TerritoryOf(a.P.AccountId)!;Assert.Equal(f.Battle.Random,back.Battle.Random);Assert.Equal(f.Deadline,back.Deadline);
        _now=_now.AddMinutes(6);_galaxy.StepTerritory(true);Assert.Empty(_store.World.Fights);Assert.Null(_store.World.Objects.First(o=>o.Id==f.Object).Active);
        Assert.False(_accounts.Profile(a.P.AccountId!)!.Workshop!.Results.Last().Won);
    }
    [Fact]
    public void ActivationTimeout_AutoEndsWithoutContribution()
    {var a=Pilot("leader");var f=Start(a);_now=f.Deadline;_galaxy.StepTerritory(true);var next=_galaxy.TerritoryOf(a.P.AccountId)!;Assert.True(next.Revision>f.Revision);Assert.False(next.Participants[0].Participated);Assert.True(next.Deadline>_now);}
    [Fact]
    public void DailySupplyAndFinalReward_AreDurableAndClaimedExactlyOnce()
    {
        var a=Pilot("leader");Win(Start(a),a.P.AccountId!);_now=_config.SeasonEpoch.AddSeconds(_config.DaySeconds);_galaxy.StepTerritory(true);
        var claim=Assert.Single(_store.World.Claims.Where(c=>c.Kind=="supply"));var before=a.P.Credits;var command=Act(a.C,"claim",claim:claim.Id);Assert.Equal(before+claim.Reward.Credits,a.P.Credits);
        _galaxy.Territory(a.C,command);Assert.Equal(before+claim.Reward.Credits,a.P.Credits);
        _now=_config.SeasonEpoch.AddSeconds(_config.SeasonSeconds);_galaxy.StepTerritory(true);Assert.Equal(2,_store.World.Season);Assert.Single(_store.World.Archives);Assert.Contains(_store.World.Claims,c=>c.Kind=="final"&&c.Reward.Credits==1000);
        _store=Store();Assert.Single(_store.World.Archives);Assert.True(_store.World.Claims.First(c=>c.Id==claim.Id).Claimed);
    }
    [Fact]
    public void CrossingSeason_BlocksNewStarts_PaysOldSeason_ThenArchivesAndResets()
    {
        var a=Pilot("leader");var f=Start(a);_now=_store.World.SeasonEnd.AddSeconds(1);_galaxy.StepTerritory(true);Assert.Equal(1,_store.World.Season);
        var b=Pilot("other");Act(b.C,"prepare","np-07","assault",b.P.Workshop.Mechs.Select(m=>m.Id).ToArray());Assert.Equal("seasonTransition",b.C.Last<TerritoryStateMsg>().Refused);
        Win(_galaxy.TerritoryOf(a.P.AccountId)!,a.P.AccountId!);Assert.Equal(2,_store.World.Season);var archive=Assert.Single(_store.World.Archives);Assert.Equal(10,archive.Contributions[a.P.AccountId!]);Assert.Equal(25,archive.Objects.First(o=>o.Id==f.Object).Control);Assert.Equal(17,_store.World.Objects.First(o=>o.Id==f.Object).Control);
    }
    [Fact]
    public void LongIdle_OneStrikePenalty_AndNextAppointmentStartsNow()
    {
        _now=_store.World.NextStrike;_galaxy.StepTerritory(true);var target=Assert.Single(_store.World.Objects.Where(o=>o.DefenseAt is not null));
        _now=_now.AddDays(5);_galaxy.StepTerritory(true);Assert.Equal(80,_store.World.Objects.First(o=>o.Id==target.Id).Control);
        _galaxy.StepTerritory(true);Assert.Equal(80,_store.World.Objects.First(o=>o.Id==target.Id).Control);Assert.Equal(_now.AddSeconds(_config.StrikeSeconds),_store.World.NextStrike);
    }
    [Theory] [InlineData("intent")] [InlineData("firstProfile")] [InlineData("secondProfile")] [InlineData("world")]
    public void Wal_ReplaysEveryCrashStep_ExactlyOnceAcrossBothProfilesAndWorld(string checkpoint)
    {
        var a=Pilot("first");var b=Pilot("second");var fired=false;
        _store.Checkpoint=step=>{if(!fired&&(step==checkpoint||checkpoint=="firstProfile"&&step=="profile:"+a.P.AccountId||checkpoint=="secondProfile"&&step=="profile:"+b.P.AccountId)){fired=true;throw new IOException("simulated crash");}};
        var next=_store.World with {Revision=17};var creditsA=a.P.Credits;var creditsB=b.P.Credits;
        Assert.False(_store.Commit(next,new TerritoryProfilePatch(a.P.AccountId!,123,a.P.Workshop,a.P.Territory),new TerritoryProfilePatch(b.P.AccountId!,456,b.P.Workshop,b.P.Territory)));
        Assert.True(_store.Waiting);_store=Store();Assert.False(_store.Waiting);Assert.False(_store.Disabled);
        Assert.Equal(creditsA+123,_accounts.Profile(a.P.AccountId!)!.Credits);Assert.Equal(creditsB+456,_accounts.Profile(b.P.AccountId!)!.Credits);Assert.Equal(17,_store.World.Revision);
        _store=Store();Assert.Equal(creditsA+123,_accounts.Profile(a.P.AccountId!)!.Credits);
    }
    [Fact]
    public void CorruptWorld_IsPreservedAndMutationsAreDisabled()
    {File.WriteAllText(Path.Combine(_dir,"world","territory-world.json"),"{broken");_store=Store();Assert.True(_store.Disabled);Assert.Equal("{broken",File.ReadAllText(Path.Combine(_dir,"world","territory-world.json")));}
    [Fact]
    public void EvaEventsMustFollowAcceptance_AndDeliveryConsumesOnlyCargo()
    {
        var a=Pilot("leader");Win(Start(a),a.P.AccountId!);Assert.False(a.P.Territory.EvaDone);Act(a.C,"evaAccept");_now=_now.AddSeconds(1);Win(Start(a),a.P.AccountId!);Assert.True(a.P.Territory.EvaDone);Act(a.C,"evaClaim");Assert.Equal(1,a.P.Territory.EvaStep);
        a.P.Territory=a.P.Territory with {EvaStep=2,EvaAccepted=_now};_galaxy.RoomOf(a.C)!.CommitMech(a.P);
        var resources=a.P.Workshop.Resources["titanium"];Act(a.C,"evaDeliver");Assert.Equal(0,a.P.Cargo.Count("titanium"));Assert.Equal(resources,a.P.Workshop.Resources["titanium"]);
    }
    [Fact]
    public void EmergencyService_IsAccountWideDaily_AndDebtSurvivesRestart()
    {
        var a=Pilot("service");var ids=a.P.Workshop.Mechs.Select(m=>m.Id).ToArray();
        a.P.Territory=a.P.Territory with {Debts=ids.ToDictionary(id=>id,id=>new TerritoryDebt(4,2))};
        _galaxy.RoomOf(a.C)!.CommitMech(a.P);
        var titanium=a.P.Workshop.Resources["titanium"];Act(a.C,"emergency",mech:ids[0]);
        Assert.False(a.P.Territory.Debts!.ContainsKey(ids[0]));Assert.Equal(titanium,a.P.Workshop.Resources["titanium"]);
        Act(a.C,"emergency",mech:ids[1]);Assert.Equal("emergencyCooldown",a.C.Last<TerritoryStateMsg>().Refused);
        _store=Store();Assert.True(_accounts.Profile(a.P.AccountId!)!.Territory!.Debts!.ContainsKey(ids[1]));
        _galaxy=Galaxy();a=Pilot("service");
        _now=_now.AddHours(24);_galaxy.StepTerritory(true);Act(a.C,"emergency",mech:ids[1]);Assert.Empty(a.P.Territory.Debts!);
    }
    [Fact]
    public void PendingWal_FreezesOrdinaryProfileSaves_UntilRecovery()
    {
        var a=Pilot("freeze");var before=_accounts.Profile(a.P.AccountId!)!;
        _store.Checkpoint=step=>{if(step=="world")throw new IOException("disk failure");};
        Assert.False(_store.Commit(_store.World,new TerritoryProfilePatch(a.P.AccountId!,123,a.P.Workshop,a.P.Territory)));
        _accounts.Save(a.P.AccountId!,before);Assert.Equal(before.Credits+123,_accounts.Profile(a.P.AccountId!)!.Credits);
        _store.Checkpoint=null;Assert.True(_store.Recover());Assert.False(_store.Waiting);
        Assert.Equal(before.Credits+123,_accounts.Profile(a.P.AccountId!)!.Credits);
    }
    [Fact]
    public void Reload_OnlyNewFightsUseNewOperationBalance()
    {
        var a=Pilot("reload");var fight=Start(a);var operations=new Dictionary<string,TerritoryOperationDef>(_config.Operations);
        operations["assault"]=operations["assault"] with {Credits=777};
        Assert.True(_galaxy.ApplyTerritoryConfig(_config with {Operations=operations}));
        Assert.Equal(600,_galaxy.TerritoryOf(a.P.AccountId)!.FrozenConfig!.Operations["assault"].Credits);
        Assert.False(_galaxy.ApplyTerritoryConfig(_config with {SeasonSeconds=_config.SeasonSeconds*2}));
        Win(fight,a.P.AccountId!);Assert.Equal(777,Start(a).FrozenConfig!.Operations["assault"].Credits);
    }
    [Fact]
    public void CoopDisconnect_DelegatesToAi_ReturnsOnAnotherDevice_AndBothAbsentReleaseObject()
    {
        var a=Pilot("returning");var b=Pilot("partner");var f=Start(a,b:b);
        _galaxy.Disconnect(a.C);_now=_now.AddSeconds(2);_galaxy.StepTerritory(true);
        var delegated=_galaxy.TerritoryOf(a.P.AccountId)!;Assert.True(delegated.Revision>f.Revision);
        Assert.False(delegated.Participants.First(p=>p.Account==a.P.AccountId).Participated);
        a=Pilot("returning");var restored=_galaxy.TerritoryOf(a.P.AccountId)!;
        Assert.Equal(f.Id,restored.Id);Assert.Null(restored.Participants.First(p=>p.Account==a.P.AccountId).LostAt);
        Assert.Equal(f.Id,a.C.Last<MechStateMsg>().Session);
        _galaxy.Disconnect(a.C);_galaxy.Disconnect(b.C);_now=_now.AddMinutes(6);_galaxy.StepTerritory(true);
        Assert.Empty(_store.World.Fights);Assert.Null(_store.World.Objects.First(o=>o.Id==f.Object).Active);
        Assert.Empty(_store.World.Contributions);
    }
}
