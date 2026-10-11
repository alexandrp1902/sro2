using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;
using Sro.Sim.Mech;

namespace Sro.Server.Tests;

public sealed class M22WorkshopTests : IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"sro-m22-"+Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly Galaxy _galaxy;
    private readonly MechRules _rules;
    private int _connection;
    public M22WorkshopTests()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !Directory.Exists(Path.Combine(root.FullName,"shared")))root=root.Parent;
        Assert.NotNull(root);
        Assert.True(MechRules.TryParse(File.ReadAllText(Path.Combine(root.FullName,"shared","mechs.json")),out _rules,out var error),error);
        _rules=_rules with {Workshop=_rules.Workshop! with {Place="st:home"}, TrainingPlace="st:home"};
        _accounts=new(_dir,NullLogger.Instance,iterations:1000,autoFlush:false);
        var story=new StoryRules(new Dictionary<string,StoryCampaign>{["quietWar"]=new("Тихая война",[new("prototype","Прототип","st:home","Ева","инженер",Kind:"collect",Item:"titanium",Count:1,Dest:"st:home")])});
        var loot=new LootRules(Items:MechCatalog.ResourceIds.ToDictionary(id=>id,id=>new LootItem(id,Volume:1,Price:10)));
        var galaxy=new GalaxyRules(StartSystem:"home",Systems:new Dictionary<string,SystemDef>{["home"]=new("Дом")});
        _galaxy=new(TestBalance.Create(loot:loot) with {GalaxySet=galaxy,StorySet=story,MechSet=_rules},NullLogger.Instance,_accounts,random:seed=>new Random(seed));
    }
    public void Dispose(){_accounts.Dispose();Directory.Delete(_dir,true);}
    private (FakeConnection C,Player P,Room R) Pilot(bool won=false)
    {
        var login=_accounts.Login("pilot","secret");
        if(_accounts.Profile(login.Id) is null) _accounts.Save(login.Id,new(12345,"light","pulse",["light"],[],new Dictionary<string,int>(),System:"home",Place:"st:home",Story:new Dictionary<string,StoryProgress>{["quietWar"]=new(["prototype"],won?[Protocol.SortieWonFlag]:[])}));
        var c=new FakeConnection(++_connection);_galaxy.JoinAccount(c,login.Id,login.Name);
        var r=_galaxy.RoomOf(c)!;var p=r.PlayerOf(c)!;return(c,p,r);
    }
    private void Act(FakeConnection c,WorkshopActMsg m)=>_galaxy.With(c,r=>r.Workshop(c,m));
    private void Start(FakeConnection c,string mission="firstSortie")=>_galaxy.Mech(c,new("start",Mission:mission,Request:Guid.NewGuid().ToString()));
    private void EndVictory(FakeConnection c,Player p,Room r)
    {
        var session=_galaxy.MechOf(p.AccountId)!;
        foreach(var e in session.Battle.Living("enemy"))e.Hp[0]=0;
        _galaxy.Mech(c,new("end",Session:session.Id,Revision:session.Revision,Unit:session.Battle.Current!.Id,Request:Guid.NewGuid().ToString()));
    }
    [Fact]
    public void OldSortieFlag_MigratesWithoutRepaying_AndDoesNotDuplicateOnLogin()
    {
        var(c,p,r)=Pilot(won:true);Assert.Equal(12345,p.Credits);Assert.Single(p.Workshop.Mechs);Assert.Equal(8,p.Workshop.Parts.Length);
        Assert.Contains("firstSortie",p.Workshop.Completed);Assert.True(r.WorkshopOpen(p));Assert.Contains("mineDefense",r.MechAvailable(p));
        r.MigrateMechs(p);Assert.Equal(8,p.Workshop.Parts.Length);
        var(_,back,_)=Pilot();Assert.Equal(p.Workshop.Mechs[0].Id,back.Workshop.Mechs[0].Id);Assert.Equal(12345,back.Credits);
    }
    [Fact]
    public void SpaceCompletion_GrantsPrototypeButDoesNotCompleteGroundMissions()
    {
        var(_,p,r)=Pilot();Assert.Single(p.Workshop.Mechs);Assert.Empty(p.Workshop.Completed);Assert.False(r.WorkshopOpen(p));
        Assert.Equal(new[]{"firstSortie"},r.MechAvailable(p));
    }
    [Fact]
    public void CommandsCheckRevision_AndDuplicateRequestDoesNotCreateAnotherMech()
    {
        var(c,p,_)=Pilot(true);var request=new WorkshopActMsg("create",p.Workshop.Revision,"request-one",Name:"Вторая");
        Act(c,request);Assert.Equal(2,p.Workshop.Mechs.Length);Act(c,request);Assert.Equal(2,p.Workshop.Mechs.Length);
        Act(c,new("create",0,"stale",Name:"Третья"));Assert.Equal("staleRevision",c.Last<WorkshopStateMsg>().Refused);Assert.Equal(2,p.Workshop.Mechs.Length);
        Act(c,new("buy",p.Workshop.Revision,"purchase",Recipe:"weapon:autocannon"));Assert.Equal("resources",c.Last<WorkshopStateMsg>().Refused);Assert.Equal(12345,p.Credits);
    }
    [Fact]
    public void TransferChecksLocationCapacityAndQuantity_AndDuplicateIsSafe()
    {
        var(c,p,_)=Pilot(true);p.Cargo.Add("titanium",10);var request=new WorkshopActMsg("transfer",p.Workshop.Revision,"deposit",Resource:"titanium",Count:5);
        Act(c,request);Act(c,request);Assert.Equal(5,p.Cargo.Count("titanium"));Assert.Equal(5,p.Workshop.Resources["titanium"]);
        Act(c,new("transfer",p.Workshop.Revision,"negative",Resource:"titanium",Count:-1));Assert.Equal("resources",c.Last<WorkshopStateMsg>().Refused);
        p.Cargo.Add("energy",15);Act(c,new("transfer",p.Workshop.Revision,"withdraw",Resource:"titanium",Count:5,ToCargo:true));
        Assert.Equal("cargoFull",c.Last<WorkshopStateMsg>().Refused);Assert.Equal(5,p.Workshop.Resources["titanium"]);
        p.Docked=false;Act(c,new("create",p.Workshop.Revision,"elsewhere",Name:"Вторая"));Assert.Equal("workshopLocked",c.Last<WorkshopStateMsg>().Refused);
    }
    [Fact]
    public void SessionRejectsStaleAndDuplicateMoves_AndFreezesWorkshopAndRules()
    {
        var(c,p,_)=Pilot();Start(c);var session=_galaxy.MechOf(p.AccountId)!;
        var move=new MechActMsg("move",2,10,Session:session.Id,Revision:session.Revision,Unit:"p1",Request:"move-one");
        _galaxy.Mech(c,move);var revision=session.Revision;_galaxy.Mech(c,move);Assert.Equal(revision,session.Revision);
        _galaxy.Mech(c,new("end",Session:session.Id,Revision:0,Unit:"p1",Request:"stale"));Assert.Equal("staleRevision",c.Last<MechRefusedMsg>().Code);
        Act(c,new("create",p.Workshop.Revision,"during",Name:"Вторая"));Assert.Equal("mechBusy",c.Last<WorkshopStateMsg>().Refused);
        _galaxy.ApplyBalance(_galaxy.Balance with {MechSet=_rules with {Combat=new MechCombatDef(StillBonus:99)}});
        Assert.Equal(10,session.Battle.Rules.CombatDef.StillBonus);
    }
    [Fact]
    public void ResultCommitsXpAndProgressTogether_AndSurvivesRestartWithoutDuplicateReward()
    {
        var(c,p,r)=Pilot();Start(c);var session=_galaxy.MechOf(p.AccountId)!;var before=p.Credits;
        EndVictory(c,p,r);var end=c.Last<MechEndMsg>();Assert.True(end.Won);Assert.Equal(before+1500,p.Credits);Assert.Equal(25,p.Workshop.Mechs[0].Xp);
        Assert.Contains("starter",p.Workshop.Grants);Assert.Single(p.Workshop.Results);
        _galaxy.Mech(c,new("end",Session:session.Id,Revision:0,Unit:"p1",Request:"old"));Assert.Equal(before+1500,p.Credits);Assert.Equal(25,p.Workshop.Mechs[0].Xp);
        using var reopened=new AccountStore(_dir,NullLogger.Instance,iterations:1000,autoFlush:false);
        var profile=reopened.Profile(p.AccountId!)!;Assert.Equal(p.Credits,profile.Credits);Assert.Equal(session.Id,Assert.Single(profile.Workshop!.Results).Session);
        Start(c);EndVictory(c,p,r);Assert.Equal(0,c.Last<MechEndMsg>().Reward);Assert.Equal(before+1500,p.Credits);Assert.Equal(50,p.Workshop.Mechs[0].Xp);
    }
    [Fact]
    public void TrainingNeverGrantsXpOrOpensWorkshop()
    {
        var(c,p,r)=Pilot();var state=p.Workshop;_galaxy.Mech(c,new("train",Request:"training"));EndVictory(c,p,r);
        Assert.True(c.Last<MechEndMsg>().Training);Assert.Same(state,p.Workshop);Assert.False(r.WorkshopOpen(p));Assert.Equal(12345,p.Credits);
    }

    [Fact]
    public void DiskFailureKeepsResultPendingAndRetryCommitsExactlyOnce()
    {
        var(c,p,r)=Pilot();Start(c);var session=_galaxy.MechOf(p.AccountId)!;var before=p.Workshop;var credits=p.Credits;
        var temp=Path.Combine(_dir,p.AccountId+".json.tmp");Directory.CreateDirectory(temp);
        try
        {
            EndVictory(c,p,r);Assert.Equal("saveFailed",c.Last<MechRefusedMsg>().Code);
            Assert.Same(before,p.Workshop);Assert.Equal(credits,p.Credits);Assert.Same(session,_galaxy.MechOf(p.AccountId));
            Assert.Empty(_accounts.Profile(p.AccountId!)!.Workshop!.Results);
        }
        finally {Directory.Delete(temp);}
        _galaxy.Mech(c,new("end",Session:session.Id,Revision:session.Revision,Unit:"p1",Request:"retry-save"));
        Assert.Null(_galaxy.MechOf(p.AccountId));Assert.Single(p.Workshop.Results);Assert.Equal(credits+1500,p.Credits);
        _galaxy.Mech(c,new("start",Request:session.StartRequest));Assert.Equal(credits+1500,p.Credits);Assert.Null(_galaxy.MechOf(p.AccountId));
    }

    [Fact]
    public void FiveMinuteExpiryPersistsKillXpAndNoVictoryReward()
    {
        var(c,p,_)=Pilot();Start(c);var session=_galaxy.MechOf(p.AccountId)!;session.Battle.Unit("p1")!.Xp=75;
        _galaxy.Disconnect(c);
        for(var i=0;i<=Galaxy.MechGraceTicks;i++)_galaxy.Step();
        Assert.Null(_galaxy.MechOf(p.AccountId));var state=_accounts.Profile(p.AccountId!)!.Workshop!;
        Assert.Equal(75,state.Mechs[0].Xp);var result=Assert.Single(state.Results);
        Assert.False(result.Won);Assert.Equal("disconnected",result.Reason);Assert.Equal(0,result.Credits);Assert.Empty(state.Completed);
        var(_,back,_)=Pilot();Assert.Equal(75,back.Workshop.Mechs[0].Xp);Assert.Single(back.Workshop.Results);
    }

    [Fact]
    public void PendingVictorySurvivesDisconnectAndAStorageOutage()
    {
        var(c,p,r)=Pilot();Start(c);var session=_galaxy.MechOf(p.AccountId)!;var credits=p.Credits;
        var temp=Path.Combine(_dir,p.AccountId+".json.tmp");Directory.CreateDirectory(temp);
        try
        {
            EndVictory(c,p,r);Assert.Equal("saveFailed",c.Last<MechRefusedMsg>().Code);_galaxy.Disconnect(c);
            for(var i=0;i<=Galaxy.MechGraceTicks;i++)_galaxy.Step();
            Assert.Same(session,_galaxy.MechOf(p.AccountId));
        }
        finally {Directory.Delete(temp);}
        _galaxy.Step();Assert.Null(_galaxy.MechOf(p.AccountId));
        var profile=_accounts.Profile(p.AccountId!)!;Assert.Equal(credits+1500,profile.Credits);
        Assert.True(Assert.Single(profile.Workshop!.Results).Won);Assert.Contains("firstSortie",profile.Workshop.Completed);
        Assert.Contains(Protocol.SortieWonFlag,profile.Story!["quietWar"].Flags!);
    }
}
