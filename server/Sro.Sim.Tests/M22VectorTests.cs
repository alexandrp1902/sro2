using System.Text.Json;
using System.Text.Json.Nodes;
using Sro.Sim.Mech;

namespace Sro.Sim.Tests;

public sealed class M22VectorTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record ForecastCase(MechBattleView State,string Hand,string? Part,int? Chance,int Block,int Min,int Max,int Weight,int Load);
    private sealed record DevelopmentCase(int Level,int Threshold,int Enemy,int KillXp);
    private sealed record Extension(MechRules Rules,ForecastCase[] Forecast,DevelopmentCase[] Development);
    private static Extension Generate()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(),out var balance,out var error),error);var rules=balance!.Mechs;
        var cases=new List<ForecastCase>();
        foreach(var weapon in rules.WeaponMap.Keys)
        foreach(var pack in new string?[]{null,"accuracy","armor","damage","mobility","repair"})
        foreach(var hand in new[]{"left","right"})
        {
            var unit=new MechUnitDef("Тест","sniper","lightBiped","pistol-cannon",weapon,pack);
            var def=new MechUnitDef("Цель","medium","mediumBiped","light","autocannon","armor");
            var units=new Dictionary<string,MechUnitDef>{{"test",unit},{"target",def}};
            var mission=new MechMission("Тест","Цель",Enumerable.Repeat("............",12).ToArray(),
                [new("test",0,0,4)],[new("target",0,rules.WeaponMap[weapon].OptimalMin,0)]);
            var r=rules with {Units=units,Missions=new Dictionary<string,MechMission>{{"test",mission}},Workshop=null};
            var battle=new MechBattle(r,"test",7);var me=battle.Unit("p1")!;var target=battle.Unit("e1")!;
            target.Stance=pack=="armor";
            if(pack=="mobility") {Assert.Null(battle.Apply(new("move",1,0),[]));me.AccuracyPenalty=10;}
            var gun=battle.Gun(me,hand)!.Value;var from=MechCombat.Side(me.X,me.Y,target.X,target.Y,target.Dir);
            var w=gun.Weapon with {Accuracy=gun.Weapon.Accuracy+(int)battle.Pack(me,"accuracy")-me.AccuracyPenalty,
                Damage=MechCombat.Round(gun.Weapon.Damage*(battle.Pack(me,"damage") is var bonus && bonus>0?bonus:1))};
            var part=pack is null?null:"body";
            var weights=MechCombat.PartWeights(r.CombatDef,from,target.Hp);
            var armors=(part is null?Enumerable.Range(0,4).Where(i=>weights[i]>0).Select(i=>battle.Armor(target,(MechPart)i)):[battle.Armor(target,MechPart.Body)]).ToList();
            var shield=battle.ShieldOf(target)!.Value;var block=MechCombat.BlockChance(r.CombatDef,shield.Shield,shield.Arm,true,from);
            if(target.Stance&&block>0)block=Math.Min(r.CombatDef.BlockMax,block+r.SkillMap["defensiveStance"].BlockBonus);
            if(block>0)armors.Add(battle.Armor(target,shield.Arm));
            var range=MechCombat.DamageRange(r.CombatDef,w,armors);
            // Keep only formula inputs needed by the client, rather than duplicate the whole catalog in every case.
            cases.Add(new(battle.View(),hand,part,battle.Chance(me,me.X,me.Y,battle.Steps,battle.MoveRange,target,part is not null,hand),block,
                range.Min,range.Max,MechCatalog.Weight(r,unit),r.ChassisMap[unit.Chassis].MaxLoad));
        }
        return new(rules with {Units=null,Missions=null,Recipes=null,Workshop=null,Progression=null,Ai=null},[..cases],[..Enumerable.Range(1,10).Select(level=>new DevelopmentCase(level,MechDevelopment.Threshold(level),level+3,MechDevelopment.KillXp(rules,level,level+3)))]);
    }
    [Fact]
    public void M22ForecastAndXpMatchSharedVectors()
    {
        var path=Path.Combine(TestHulls.RepoRoot(),"shared","test-vectors","mech.json");var root=JsonNode.Parse(File.ReadAllText(path))!;
        var expected=JsonSerializer.SerializeToNode(Generate(),Json)!;
        if(Environment.GetEnvironmentVariable("SRO_UPDATE_VECTORS")=="1")
        {
            // Preserve M21 vector formatting and only append the extension.
            var original=File.ReadAllText(path);var end=original.LastIndexOf('}');
            var oldExtension=original.IndexOf(",\n  \"m22\":",StringComparison.Ordinal);
            File.WriteAllText(path,(oldExtension<0?original[..end].TrimEnd():original[..oldExtension])+",\n  \"m22\": "+expected.ToJsonString(Json)+"\n}\n");
            return;
        }
        Assert.NotNull(root["m22"]);Assert.True(JsonNode.DeepEquals(expected,root["m22"]),"M22 forecast vectors differ; regenerate with SRO_UPDATE_VECTORS=1");
    }
}
