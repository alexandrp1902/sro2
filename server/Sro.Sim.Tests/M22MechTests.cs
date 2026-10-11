using System.Text.Json;
using Sro.Sim.Mech;

namespace Sro.Sim.Tests;

public sealed class M22MechTests
{
    private static MechRules Shared()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        return balance!.Mechs;
    }
    private static MechWorkshopState Inventory(MechRules rules, string[] keys)
    {
        var state = MechCatalog.Grant(rules, MechWorkshopState.Empty, "test", keys);
        Assert.Null(MechCatalog.Create(state, "Машина", out state));
        var p = state.Parts;
        Assert.Null(MechCatalog.SaveAssembly(rules, state, state.Mechs[0].Id, null,
            new(p[0].Id, p[1].Id, p[2].Id, p[3].Id, p.Length > 4 ? p[4].Id : null), out state));
        return state;
    }

    [Fact]
    public void Mass_DoesNotIncludeChassis_AndRejectsWholeOverloadedAssembly()
    {
        var rules = Shared();
        var def = new MechUnitDef("Мечник", "heavy", "heavyBiped", "heavy", "sword", "damage");
        Assert.Equal(134, MechCatalog.Weight(rules, def));
        var chassis = new Dictionary<string, MechFrame>(rules.ChassisMap) { ["heavyBiped"] = rules.ChassisMap["heavyBiped"] with { Weight = 999, MaxLoad = 134 } };
        Assert.Null(MechCatalog.Fitting(rules with { Chassis = chassis }, def));
        chassis["heavyBiped"] = chassis["heavyBiped"] with { MaxLoad = 133 };
        Assert.Equal("overweight", MechCatalog.Fitting(rules with { Chassis = chassis }, def));
        var state = Inventory(rules, ["body:heavy", "chassis:heavyBiped", "shield:heavy", "weapon:sword", "backpack:damage"]);
        Assert.Equal("overweight", MechCatalog.SaveAssembly(rules with { Chassis = chassis }, state, state.Mechs[0].Id,
            null, state.Mechs[0].Assembly, out var refused));
        Assert.Same(state, refused);
    }

    [Fact]
    public void Instances_CannotBeDuplicated_OrInstalledOnTwoMachines()
    {
        var rules=Shared(); var state=Inventory(rules,["body:medium","chassis:mediumBiped","shield:light","weapon:autocannon"]);
        Assert.Null(MechCatalog.Create(state,"Вторая",out state));
        Assert.Equal("ownership",MechCatalog.SaveAssembly(rules,state,state.Mechs[1].Id,null,state.Mechs[0].Assembly,out var refused));
        Assert.Same(state,refused);
        var duplicate=state.Mechs[0].Assembly with {Left=state.Mechs[0].Assembly.Right};
        Assert.Equal("ownership",MechCatalog.SaveAssembly(rules,state,state.Mechs[0].Id,null,duplicate,out refused));
        Assert.Same(state,refused);
        Assert.Null(MechCatalog.SaveAssembly(rules,state,state.Mechs[0].Id,null,new(),out var stripped));
        Assert.Equal(state.Parts,stripped.Parts);
        Assert.Null(MechCatalog.SaveAssembly(rules,stripped,state.Mechs[1].Id,null,state.Mechs[0].Assembly,out _));
    }

    [Fact]
    public void Buy_IsAtomic_AndFifthMachineIsRefused()
    {
        var rules=Shared(); var state=MechWorkshopState.Empty with {Resources=new Dictionary<string,int>{{"titanium",100},{"energy",0}}};
        Assert.Equal("resources",MechCatalog.Buy(rules,state,"weapon:autocannon",10000,out var refused,out var credits));
        Assert.Same(state,refused); Assert.Equal(10000,credits);Assert.Equal(100,state.Resources["titanium"]);
        state=state with {Resources=new Dictionary<string,int>{{"titanium",4},{"energy",2}}};
        Assert.Null(MechCatalog.Buy(rules,state,"weapon:autocannon",500,out var bought,out credits));
        Assert.Equal(0,credits); Assert.Equal(0,bought.Resources["energy"]);Assert.Single(bought.Parts);
        for(var i=0;i<4;i++) Assert.Null(MechCatalog.Create(state,$"Мех {i}",out state));
        Assert.Equal("mechLimit",MechCatalog.Create(state,"Пятый",out refused));Assert.Same(state,refused);
    }

    [Fact]
    public void GrantsAndPrototype_AreIdempotent_AndStarterBuildsASecondMech()
    {
        var rules=Shared();var state=MechCatalog.Prototype(rules,MechWorkshopState.Empty);
        Assert.Same(state,MechCatalog.Prototype(rules,state));Assert.Single(state.Mechs);
        state=MechCatalog.Grant(rules,state,"starter",rules.Workshop!.StarterParts);
        Assert.Same(state,MechCatalog.Grant(rules,state,"starter",rules.Workshop.StarterParts));
        Assert.Null(MechCatalog.Create(state,"Вторая",out state));var p=state.Parts.Skip(4).ToArray();
        Assert.Null(MechCatalog.SaveAssembly(rules,state,state.Mechs[1].Id,null,new(p[0].Id,p[1].Id,p[2].Id,p[3].Id),out state));
        Assert.Null(MechCatalog.Fitting(rules,MechCatalog.Resolve(rules,state,state.Mechs[1])!));
    }

    private static MechBattle Battle(string kind="eliminate", string? backpack=null, string? right=null, string? left=null,
        int chance=100,uint seed=7, string[]? map=null)
    {
        var rules=Shared();
        var units=new Dictionary<string,MechUnitDef>(rules.UnitMap)
        { ["testPlayer"]=new("Игрок","medium","mediumBiped",left ?? "autocannon",right ?? "autocannon",backpack),
          ["testEnemy"]=new("Враг","medium","mediumBiped","autocannon","autocannon") };
        var mission=new MechMission("Тест","Цель",map ?? Enumerable.Repeat("......",6).ToArray(),
            [new("testPlayer",0,0,2),new("prototype",0,5,2)], [new("testEnemy",4,0,6)],
            Objective:kind switch {
                "defend"=>new("defend",2,new("mine",3,3,0)),
                "escort"=>new("escort",Object:new("tractor",1,3,2),Route:[new(2,3),new(3,3)]),
                "capture"=>new("capture",2,Zone:[new(2,2)]), _=>new() });
        var skills=rules.SkillMap.ToDictionary(p=>p.Key,p=>p.Value with {Chance=chance});
        return new(rules with {Units=units,Missions=new Dictionary<string,MechMission>{{"test",mission}},Skills=skills,Workshop=null},"test",seed);
    }
    private static void Round(MechBattle b)
    {
        var round=b.Round;
        for(var guard=0;guard<100 && !b.Over && b.Round==round;guard++) Assert.Null(b.Apply(new("end"),[]));
    }

    [Fact]
    public void Capture_NeedsTwoConsecutiveRounds_EvenWithNoEnemies()
    {
        var b=Battle("capture");b.Unit("e1")!.Hp[0]=0;var p=b.Unit("p1")!;p.X=2;p.Y=2;
        Round(b);Assert.Equal(1,b.Progress);Assert.False(b.Over);
        p.X=1;Round(b);Assert.Equal(0,b.Progress);
        p.X=2;Round(b);Assert.Equal(1,b.Progress);Round(b);Assert.Equal("player",b.Winner);
    }
    [Fact]
    public void ContestedPointResets_AndLossHasPriority()
    {
        var b=Battle("capture");b.Unit("p1")!.X=2;b.Unit("p1")!.Y=2;Round(b);Assert.Equal(1,b.Progress);
        b.Unit("e1")!.X=2;b.Unit("e1")!.Y=2;Round(b);Assert.Equal(0,b.Progress);
        b.Unit("e1")!.Hp[0]=0;b.Unit("p1")!.Hp[0]=0;b.Unit("p2")!.Hp[0]=0;
        Assert.Null(b.Apply(new("end"),[]));Assert.Equal("enemy",b.Winner);
    }
    [Fact]
    public void TractorWaitsForOccupiedCell_ThenContinues_AndCannotBeRepaired()
    {
        var b=Battle("escort",backpack:"repair");var p=b.Unit("p2")!;p.X=2;p.Y=3;
        Round(b);Assert.Equal(0,b.Progress);Assert.Equal(1,b.ObjectiveUnit!.X);
        p.X=0;p.Y=5;Round(b);Assert.Equal(1,b.Progress);Round(b);Assert.Equal("player",b.Winner);
        var loss=Battle("escort");loss.ObjectiveUnit!.Hp[0]=0;Assert.Null(loss.Apply(new("end"),[]));Assert.Equal("tractorDestroyed",loss.Reason);
    }
    [Fact]
    public void DefenseChecksObject_AndWavesUseNearestFreeCell()
    {
        var b=Battle("defend");Round(b);Assert.False(b.Over);Round(b);Assert.Equal("player",b.Winner);
        b=Battle("defend");b.ObjectiveUnit!.Hp[0]=0;Assert.Null(b.Apply(new("end"),[]));Assert.Equal("enemy",b.Winner);
        var rules=Shared();var m=rules.MissionMap["mineDefense"];
        b=new(rules,"mineDefense",1);b.Unit("p1")!.X=13;b.Unit("p1")!.Y=1;b.Unit("e1")!.X=5;b.Unit("e1")!.Y=0;
        Round(b);Round(b);Assert.Equal(4,b.Units.Count(u=>u.Side=="enemy"));
        Assert.Equal(b.Units.Count(u=>u.Alive),b.Occupied().Count);
    }

    [Theory]
    [InlineData("accuracy",10)] [InlineData("armor",15)] [InlineData("damage",1.1)] [InlineData("mobility",1)] [InlineData("repair",.3)]
    public void FiveBackpacks_HaveTheirConfiguredEffects(string pack,double value)
    {
        var b=Battle(backpack:pack);var p=b.Unit("p1")!;Assert.Equal(value,b.Pack(p,pack));
        if(pack=="armor")Assert.Equal(40,b.Armor(p,MechPart.Body));
        if(pack=="mobility")Assert.Equal(5,b.MoveRangeOf(p));
        if(pack=="repair")
        {
            p.Hp[0]=100;Assert.Null(b.Apply(new("repair",Part:"body"),[]));Assert.Equal(760,p.Hp[0]);Assert.True(p.Repaired);
            Round(b);Assert.Equal("badRepair",b.Apply(new("repair",Part:"body"),[]));
        }
    }

    [Fact]
    public void BothHandsAndSword_UseRangeAndOnlyOneOrdinaryAttack()
    {
        var b=Battle(right:"sword");Assert.Equal("outOfRange",b.Apply(new("attack",Target:"e1",Hand:"right"),[]));
        var events=new List<MechEvent>();Assert.Null(b.Apply(new("attack",Target:"e1",Hand:"left"),events));Assert.Single(events.Where(e=>e.Kind=="shot"));
        b=Battle(right:"sword");b.Unit("e1")!.X=1;events=[];Assert.Null(b.Apply(new("attack",Target:"e1",Hand:"right"),events));Assert.Equal("sword",events.First(e=>e.Kind=="shot").Weapon);
    }

    [Fact]
    public void ExtraAttacksNeverChain_ReactionsNeverReact()
    {
        var b=Battle();b.Unit("p1")!.Skills=["doubleAttack","counterAttack"];b.Unit("e1")!.Skills=["doubleAttack","counterAttack"];
        var events=new List<MechEvent>();Assert.Null(b.Apply(new("attack",Target:"e1"),events));
        Assert.Equal(3,events.Count(e=>e.Kind=="shot"));Assert.Single(events.Where(e=>e.Kind=="skill" && e.Weapon=="doubleAttack"));Assert.Single(events.Where(e=>e.Kind=="skill" && e.Weapon=="counterAttack"));
    }

    [Theory]
    [InlineData("targetingExpert")] [InlineData("evade")] [InlineData("shieldMastery")] [InlineData("defensiveStance")]
    public void AutomaticSkills_AreObservableUnderControlledSeeds(string skill)
    {
        var happened=false;
        for(uint seed=1;seed<200 && !happened;seed++)
        {
            var b=Battle(seed:seed);var actor=skill is "evade" or "shieldMastery" ? b.Unit("e1")! : b.Unit("p1")!;
            actor.Skills=[skill];
            if(skill=="shieldMastery")
            {
                var rules=b.Rules;var units=new Dictionary<string,MechUnitDef>(rules.UnitMap){["testEnemy"]=rules.UnitMap["testEnemy"] with {Left="light"}};
                b=new(rules with {Units=units},"test",seed);b.Unit("e1")!.Skills=[skill];
            }
            var events=new List<MechEvent>();Assert.Null(b.Apply(skill=="defensiveStance" ? new("end") : new("attack",Target:"e1",Part:"body"),events));
            happened=events.Any(e=>e.Kind=="skill" && e.Weapon==skill);
        }
        Assert.True(happened,skill);
    }

    [Fact]
    public void DashAndJump_AttemptsConsumeMovementEvenOnFailure_AndInvalidRequestsDoNotRoll()
    {
        var map=new[]{".w....",".w....",".w....",".w....",".w....","......"};
        var b=Battle(map:map);b.Unit("p1")!.Skills=["jump"];Assert.Null(b.Apply(new("move",2,0),[]));Assert.Equal(2,b.Unit("p1")!.X);
        b=Battle(map:map,chance:0);b.Unit("p1")!.Skills=["jump"];Assert.Null(b.Apply(new("move",2,0),[]));Assert.Equal(0,b.Unit("p1")!.X);Assert.True(b.Moved);Assert.Equal("alreadyMoved",b.Apply(new("move",2,0),[]));
        b=Battle(chance:0);b.Unit("p1")!.Skills=["dash"];Assert.Null(b.Apply(new("move",5,5),[]));Assert.True(b.Moved);Assert.Equal(0,b.Unit("p1")!.X);
        var a=Battle(seed:42);var other=Battle(seed:42);a.Unit("p1")!.Skills=other.Unit("p1")!.Skills=["dash"];
        Assert.Equal("unreachable",a.Apply(new("move",99,99),[]));var ae=new List<MechEvent>();var be=new List<MechEvent>();
        Assert.Null(a.Apply(new("move",2,2),ae));Assert.Null(other.Apply(new("move",2,2),be));
        Assert.Equal(JsonSerializer.Serialize(ae),JsonSerializer.Serialize(be));
    }

    [Fact]
    public void XpCrossesAllThresholds_PreservesSkills_AndHasSixSkillLimit()
    {
        var rules=Shared();var p=new MechProgression(UnlockBase:100,UnlockMax:100);
        var mech=new OwnedMech("id","Тест",new(),Skills:[]);
        var def=new MechUnitDef("Тест","medium","mediumBiped","light","autocannon");
        var gained=MechDevelopment.Gain(rules with {Progression=p},mech,def,12000,new(42),out var unlocked);
        Assert.True(gained.Level>5);Assert.InRange(gained.Skills!.Length,1,6);Assert.NotEmpty(unlocked);
        var changed=gained with {Assembly=new("other")};Assert.Equal(gained.Skills,changed.Skills);
        Assert.Equal(73,MechDevelopment.KillXp(rules,5,8));Assert.Equal(100,MechDevelopment.Threshold(1));Assert.Equal(200,MechDevelopment.Threshold(2));
    }
    [Fact]
    public void MissingRewardOrStarter_InvalidatesTheCatalog()
    {
        var rules=Shared();var missions=new Dictionary<string,MechMission>(rules.MissionMap){["primeEscort"]=rules.MissionMap["primeEscort"] with {RewardParts=[]}};
        Assert.Contains("rewards",(rules with {Missions=missions}).Validate());
        Assert.Contains("starter",(rules with {Workshop=rules.Workshop! with {StarterParts=["body:light"]}}).Validate());
    }

    [Fact]
    public void PreviousDashPenaltyDoesNotLowerNextActivationsForecast()
    {
        var battle=Battle();var me=battle.Unit("p1")!;var target=battle.Unit("e1")!;
        var baseline=battle.Chance(me,me.X,me.Y,0,battle.MoveRange,target,false);
        me.AccuracyPenalty=10;
        Assert.Equal(baseline,battle.Chance(me,me.X,me.Y,0,battle.MoveRange,target,false));
        Assert.Equal(0,battle.View().Units.First(u=>u.Id==me.Id).AccuracyPenalty);
        var events=new List<MechEvent>();Assert.Null(battle.Apply(new("attack",Target:target.Id),events));
        Assert.Equal(baseline,events.First(e=>e.Kind==MechEvent.Shot).Chance);
    }

    [Theory]
    [InlineData("{\"backpacks\":{\"bad\":null}}")]
    [InlineData("{\"skills\":{\"bad\":null}}")]
    [InlineData("{\"recipes\":{\"bad\":null}}")]
    [InlineData("{\"missions\":{\"bad\":{\"name\":\"Bad\",\"goal\":\"Bad\",\"map\":[null]}}}")]
    public void InvalidCatalogNullsAreRefusedInsteadOfThrowing(string json)
    {
        Assert.False(MechRules.TryParse(json,out _,out var error));Assert.NotNull(error);
    }
}
