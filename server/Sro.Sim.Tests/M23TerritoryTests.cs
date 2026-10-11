using System.Text.Json;
using Sro.Sim.Mech;

namespace Sro.Sim.Tests;

public sealed class M23TerritoryTests
{
    public static string Root()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null&&!Directory.Exists(Path.Combine(dir.FullName,"shared")))dir=dir.Parent;
        return dir!.FullName;
    }
    public static TerritoryConfig Config()=>TerritoryConfig.Load(Path.Combine(Root(),"shared","mech-territory.json"));
    public static MechRules Rules()
    {Assert.True(MechRules.TryParse(File.ReadAllText(Path.Combine(Root(),"shared","mechs.json")),out var rules,out var error),error);return rules;}
    [Fact]
    public void SharedVectors_MatchClientPreviews()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(),"shared","test-vectors","territory.json")));
        foreach(var v in doc.RootElement.GetProperty("reset").EnumerateArray()) Assert.Equal(v.GetProperty("expected").GetInt32(),TerritoryRules.Reset(new("x",v.GetProperty("control").GetInt32(),"corporation"),v.GetProperty("start").GetInt32()).Control);
        foreach(var v in doc.RootElement.GetProperty("service").EnumerateArray()) Assert.Equal(new TerritoryDebt(v.GetProperty("titanium").GetInt32(),v.GetProperty("energy").GetInt32()),TerritoryRules.Service(v.GetProperty("lost").GetInt32(),v.GetProperty("max").GetInt32(),v.GetProperty("discount").GetBoolean()));
        foreach(var v in doc.RootElement.GetProperty("distribution").EnumerateArray()) Assert.Equal(v.GetProperty("expected").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetInt32()).OrderBy(p=>p.Key),TerritoryRules.Distribute(v.GetProperty("pool").GetInt32(),v.GetProperty("contributions").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetInt32())).OrderBy(p=>p.Key));
        foreach(var v in doc.RootElement.GetProperty("cargo").EnumerateArray()) Assert.Equal(v.GetProperty("expected").GetDouble(),TerritoryRules.CargoVolume(v.GetProperty("hull").GetString()!,v.GetProperty("resource").GetString()!,v.GetProperty("volume").GetDouble()));
    }
    [Fact]
    public void Map_Connected_WithTwoVariantsAndFiveBiomes_AndBaseSurvivesTotalLoss()
    {
        var c=Config();Assert.Null(c.Validate());Assert.Equal(5,c.Objects.Select(o=>o.Biome).Distinct().Count());
        var objects=c.Objects.Select(o=>new TerritoryObject(o.Id,0,TerritoryRules.Corporation)).ToArray();
        Assert.Equal(2,objects.Count(o=>TerritoryRules.Accessible(c,objects,o.Id)));
        objects[5]=objects[5] with {Owner=TerritoryRules.Resistance};Assert.True(TerritoryRules.Accessible(c,objects,objects[5].Id));
        Assert.NotNull((c with {Objects=[..c.Objects[..11],c.Objects[0]]}).Validate());
    }
    [Fact]
    public void Ownership_Hysteresis_Clamps_AndSeasonUsesDifferentThreshold()
    {
        var o=new TerritoryObject("one",0,"corporation");
        o=TerritoryRules.Change(o,99);Assert.Equal("corporation",o.Owner);
        o=TerritoryRules.Change(o,25);Assert.Equal(100,o.Control);Assert.Equal("resistance",o.Owner);
        o=TerritoryRules.Change(o,-99);Assert.Equal("resistance",o.Owner);
        o=TerritoryRules.Change(o,-20);Assert.Equal(0,o.Control);Assert.Equal("corporation",o.Owner);
        Assert.Equal(67,TerritoryRules.Reset(o with {Control=100},0).Control);
        Assert.Equal("resistance",TerritoryRules.Reset(o with {Control=75},0).Owner);
    }
    [Theory]
    [InlineData(0,0,0)] [InlineData(1,1,1)] [InlineData(25,1,1)] [InlineData(100,4,2)]
    public void Service_DamageAndCeil(int lost,int titanium,int energy)
    {Assert.Equal(new TerritoryDebt(titanium,energy),TerritoryRules.Service(lost,100,false));}
    [Fact]
    public void Shares_ConserveResources_TiesByAccount_AndExcludeZero()
    {
        var shares=TerritoryRules.Distribute(7,new Dictionary<string,int>{{"b",1},{"a",1},{"zero",0}});
        Assert.Equal(4,shares["a"]);Assert.Equal(3,shares["b"]);Assert.False(shares.ContainsKey("zero"));Assert.Equal(7,shares.Values.Sum());
        Assert.Empty(TerritoryRules.Distribute(7,new Dictionary<string,int>()));
        Assert.Equal(0,TerritoryRules.FinalReward(0,1).Credits);Assert.Equal(1000,TerritoryRules.FinalReward(39,1).Credits);
        Assert.Equal("backpack:armor",TerritoryRules.FinalReward(40,1).Part);Assert.Equal(5000,TerritoryRules.FinalReward(100,1).Credits);
    }
    private static MechBattle Battle(string kind)
    {
        var rules=Rules();var m=rules.MissionMap["firstSortie"];
        var map=Enumerable.Repeat(new string('.',12),12).ToArray();
        var obj=kind switch {
            "capture"=>new MechObjective(kind,2,Zone:[new(1,10)],FullRounds:true),
            "defend"=>new(kind,6,new("mine",5,5,0)),
            "escort"=>new(kind,Object:new("tractor",5,5,0),Route:[new(5,4),new(5,3)]),
            _=>new(kind,Object:new("mine",2,10,0),Evacuation:[new(1,10)]),
        };
        m=m with {Map=map,Player=[new("prototype",1,10,0)],Enemy=[new("banditRecruit",10,1,4)],Objective=obj,Waves=null,Allies=null};
        return new(rules with {Missions=new Dictionary<string,MechMission>{{"test",m}}},"test",123);
    }
    private static void Round(MechBattle b)
    {
        var round=b.Round;
        for(var guard=0;guard<20&&!b.Over&&b.Round==round;guard++)Assert.Null(b.Apply(new("end"),[]));
    }
    [Theory] [InlineData("capture",2)] [InlineData("defend",6)] [InlineData("escort",2)]
    public void Objectives_RequireFullRounds(string kind,int rounds)
    {var b=Battle(kind);for(var i=0;i<rounds-1;i++){Round(b);Assert.False(b.Over);}Round(b);Assert.Equal("player",b.Winner);}
    [Theory] [InlineData("rescue")] [InlineData("sabotage")]
    public void Interaction_ConsumesActivation_ThenEvacuatesAtRoundBoundary(string kind)
    {var b=Battle(kind);Assert.Null(b.Apply(new("interact"),[]));Assert.True(b.Interacted);Assert.False(b.Over);Round(b);Assert.Equal("player",b.Winner);}
    [Fact]
    public void RequiredObjectLoss_HasPriorityOverVictory_AndBlockedTractorDoesNotMove()
    {
        var b=Battle("defend");b.ObjectiveUnit!.Hp[0]=0;Round(b);Assert.Equal("enemy",b.Winner);
        b=Battle("escort");b.Units[0].X=5;b.Units[0].Y=4;Round(b);Assert.Equal(0,b.Progress);Assert.False(b.Over);
    }
    [Fact]
    public void Snapshot_PreservesRngActivationAndDamage()
    {
        var b=Battle("defend");Assert.Null(b.Apply(new("move",3,8),[]));
        var saved=JsonSerializer.Deserialize<MechBattleSave>(JsonSerializer.Serialize(b.Save()))!;
        var back=MechBattle.Restore(saved);Assert.Equal(JsonSerializer.Serialize(b.Save()),JsonSerializer.Serialize(back.Save()));
        Round(b);Round(back);Assert.Equal(JsonSerializer.Serialize(b.Save()),JsonSerializer.Serialize(back.Save()));
    }
    [Fact]
    public void Capture_EntryRoundDoesNotCount_AndSnapshotKeepsStartOfRoundHold()
    {
        var b=Battle("capture");b.Units[0].X=3;Round(b);Assert.Equal(0,b.Progress);
        b.Units[0].X=1;Round(b);Assert.Equal(0,b.Progress);
        b=MechBattle.Restore(b.Save());Round(b);Assert.Equal(1,b.Progress);Assert.False(b.Over);
        Round(b);Assert.Equal("player",b.Winner);
    }
}
