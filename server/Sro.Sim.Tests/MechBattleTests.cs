using Sro.Sim.Mech;

namespace Sro.Sim.Tests;

public class MechBattleTests
{
    private static MechRules Shared()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        return balance!.Mechs;
    }

    /// <summary>Доиграть бой: за игрока ходит тот же ИИ. Возвращает все события по порядку.</summary>
    private static List<MechEvent> PlayOut(MechBattle battle)
    {
        var events = new List<MechEvent>();
        for (var guard = 0; guard < 400 && !battle.Over; guard++)
        {
            if (battle.Turn == MechBattle.PlayerSide)
            {
                foreach (var command in MechAi.Decide(battle))
                {
                    Assert.Null(battle.Apply(command, events));
                    if (battle.Current is null || battle.Turn != MechBattle.PlayerSide) break;
                }
            }
            battle.RunEnemies(events);
        }
        return events;
    }

    [Fact]
    public void FirstSortie_PlaysToTheEnd()
    {
        foreach (var seed in new uint[] { 1, 2, 3, 42, 1000 })
        {
            var battle = new MechBattle(Shared(), MechRules.FirstSortie, seed);
            var events = PlayOut(battle);
            Assert.True(battle.Over, $"seed {seed}: not over after {battle.Round} rounds");
            Assert.Contains(events, e => e.Kind == MechEvent.MechDown);
            // Бой на пять минут, а не на полчаса: раундов не больше двадцати.
            Assert.True(battle.Round <= 20, $"seed {seed}: {battle.Round} rounds");
        }
    }

    [Fact]
    public void TheSameSeed_GivesTheSameBattle()
    {
        var a = PlayOut(new MechBattle(Shared(), MechRules.FirstSortie, 7));
        var b = PlayOut(new MechBattle(Shared(), MechRules.FirstSortie, 7));
        Assert.Equal(a.Select(e => $"{e.Kind}:{e.Unit}:{e.Target}:{e.Part}:{e.Dmg}"), b.Select(e => $"{e.Kind}:{e.Unit}:{e.Target}:{e.Part}:{e.Dmg}"));
    }

    [Fact]
    public void Rounds_GoPlayerThenEnemies_BackToBack()
    {
        var battle = new MechBattle(Shared(), MechRules.FirstSortie, 1);
        Assert.Equal(1, battle.Round);
        Assert.Equal("p1", battle.Current!.Id);
        var events = new List<MechEvent>();
        Assert.Null(battle.Apply(new MechCommand(MechCommand.EndAct), events));
        // У игрока мех один: после него оба налётчика ходят подряд (§12).
        Assert.Equal("e1", battle.Current!.Id);
        battle.RunEnemies(events);
        Assert.Equal(2, battle.Round);
        Assert.Equal("p1", battle.Current!.Id);
        Assert.Contains(events, e => e.Kind == MechEvent.Round && e.N == 2);
    }

    [Fact]
    public void Refusals_HaveCodes()
    {
        MechSpawn[] p = [new("proto", 0, 5, 2)];
        MechSpawn[] e = [new("raider", 10, 5, 6)];
        string[] map =
        [
            "............",
            "............",
            "............",
            "............",
            ".....w......",
            ".....w......",
            ".....w......",
            "............",
            "............",
            "............",
            "............",
            "............",
        ];
        var battle = new MechBattle(TestMechs.Rules(map, p, e), "test", 1);
        var events = new List<MechEvent>();
        Assert.Equal(MechCodes.OutOfRange, battle.Apply(new MechCommand(MechCommand.AttackAct, Target: "e1"), events));
        Assert.Equal(MechCodes.NoTarget, battle.Apply(new MechCommand(MechCommand.AttackAct, Target: "p1"), events));
        Assert.Equal(MechCodes.Unreachable, battle.Apply(new MechCommand(MechCommand.MoveAct, 9, 5), events));
        Assert.Equal(MechCodes.BadAct, battle.Apply(new MechCommand("fly"), events));
        Assert.Null(battle.Apply(new MechCommand(MechCommand.MoveAct, 4, 5), events));
        Assert.Equal(MechCodes.AlreadyMoved, battle.Apply(new MechCommand(MechCommand.MoveAct, 3, 5), events));
        // Шесть клеток по прямой — в дальности, но за стеной.
        Assert.Equal(MechCodes.NoLine, battle.Apply(new MechCommand(MechCommand.AttackAct, Target: "e1"), events));
        Assert.Equal(MechCodes.BadPart, battle.Apply(new MechCommand(MechCommand.AttackAct, Target: "e1", Part: "tail"), events));
        Assert.DoesNotContain(events, ev => ev.Kind == MechEvent.Shot);
    }

    [Fact]
    public void Move_TurnsTheMechToItsLastStep()
    {
        var battle = new MechBattle(TestMechs.Rules(TestMechs.Open, [new("proto", 0, 0, 4)], [new("raider", 11, 11, 0)]), "test", 1);
        var events = new List<MechEvent>();
        Assert.Null(battle.Apply(new MechCommand(MechCommand.MoveAct, 3, 0), events));
        Assert.Equal(2, battle.Current!.Dir);
        Assert.Equal(3, battle.Steps);
        var move = Assert.Single(events);
        Assert.Equal([1, 2, 3], move.Path!);
    }

    [Fact]
    public void Attack_TurnsTheShooterToTheTarget_AndEndsTheTurn()
    {
        var battle = new MechBattle(TestMechs.Rules(TestMechs.Open, [new("proto", 0, 0, 4)], [new("raider", 4, 4, 0)]), "test", 1);
        var events = new List<MechEvent>();
        Assert.Null(battle.Apply(new MechCommand(MechCommand.AttackAct, Target: "e1"), events));
        var shot = events.First(e => e.Kind == MechEvent.Shot);
        Assert.Equal(3, shot.Dir);
        Assert.Equal(3, battle.Unit("p1")!.Dir);
        Assert.Equal(MechBattle.EnemySide, battle.Turn);
    }

    [Fact]
    public void AimedShot_HitsTheChosenPart()
    {
        // Сид подобран так, чтобы шанс 95 − 25 сработал; часть не разыгрывается — она выбрана.
        for (uint seed = 1; seed < 50; seed++)
        {
            var battle = new MechBattle(
                TestMechs.Rules(TestMechs.Open, [new("proto", 0, 0, 4)], [new("bare", 0, 4, 4)]), "test", seed);
            var events = new List<MechEvent>();
            Assert.Null(battle.Apply(new MechCommand(MechCommand.AttackAct, Target: "e1", Part: "chassis"), events));
            if (events.Any(e => e.Kind == MechEvent.Miss)) continue;
            var hit = Assert.Single(events, e => e.Kind == MechEvent.Hit);
            Assert.Equal("chassis", hit.Part);
            Assert.True(battle.Unit("e1")!.Hp[(int)MechPart.Chassis] < 1100);
            return;
        }
        Assert.Fail("no seed hit in 50 tries");
    }

    [Fact]
    public void BrokenShield_PassesTheRestToThePart()
    {
        // Щит почти разбит: блок съедает остаток, перебор уходит в корпус (§43).
        for (uint seed = 1; seed < 400; seed++)
        {
            var battle = new MechBattle(
                TestMechs.Rules(TestMechs.Open, [new("proto", 0, 4, 2)], [new("raider", 4, 4, 6)]), "test", seed);
            battle.Unit("e1")!.Hp[(int)MechPart.Left] = 10;
            var events = new List<MechEvent>();
            Assert.Null(battle.Apply(new MechCommand(MechCommand.AttackAct, Target: "e1", Part: "body"), events));
            if (!events.Any(e => e.Kind == MechEvent.Block)) continue;
            var block = events.First(e => e.Kind == MechEvent.Block);
            Assert.Equal(10, block.Dmg);
            Assert.Contains(events, e => e.Kind == MechEvent.PartDown && e.Part == "left");
            var rest = events.First(e => e.Kind == MechEvent.Hit);
            Assert.Equal("body", rest.Part);
            Assert.True(rest.Dmg > 0);
            return;
        }
        Assert.Fail("no seed blocked in 400 tries");
    }

    [Fact]
    public void RearShot_IsNeverBlocked()
    {
        for (uint seed = 1; seed < 200; seed++)
        {
            // Цель смотрит на восток, стрелок — к западу от неё: выстрел в спину.
            var battle = new MechBattle(
                TestMechs.Rules(TestMechs.Open, [new("proto", 0, 4, 2)], [new("raider", 4, 4, 2)]), "test", seed);
            var events = new List<MechEvent>();
            Assert.Null(battle.Apply(new MechCommand(MechCommand.AttackAct, Target: "e1"), events));
            Assert.DoesNotContain(events, e => e.Kind == MechEvent.Block);
        }
    }

    [Fact]
    public void AMechWithoutAGun_CannotAttack_ButCanStillEndItsTurn()
    {
        var battle = new MechBattle(TestMechs.Rules(TestMechs.Open, [new("proto", 0, 0, 4)], [new("raider", 0, 4, 0)]), "test", 1);
        battle.Unit("p1")!.Hp[(int)MechPart.Right] = 0;
        var events = new List<MechEvent>();
        Assert.Equal(MechCodes.ArmDown, battle.Apply(new MechCommand(MechCommand.AttackAct, Target: "e1"), events));
        Assert.Null(battle.Apply(new MechCommand(MechCommand.EndAct, Dir: 4), events));
    }

    [Fact]
    public void Surrender_LosesTheBattle()
    {
        var battle = new MechBattle(Shared(), MechRules.FirstSortie, 1);
        battle.Surrender();
        Assert.Equal(MechBattle.EnemySide, battle.Winner);
        Assert.Equal(MechCodes.Over, battle.Apply(new MechCommand(MechCommand.EndAct), []));
    }
}
