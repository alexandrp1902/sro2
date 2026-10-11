namespace Sro.Sim.Mech;

/// <summary>Stable search over legal cells, targets and both hands. Objective actors are real targets.</summary>
public static class MechAi
{
    public static List<MechCommand> Decide(MechBattle battle)
    {
        if (battle.Current is not { } u) return [];
        var side = u.Side == MechBattle.PlayerSide ? MechBattle.EnemySide : MechBattle.PlayerSide;
        var targets = battle.Living(side).OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
        var field = battle.Field;
        var here = field.Index(u.X, u.Y);
        var cells = new[] { (Cell: here, Steps: 0) }.Concat(battle.Reach().OrderBy(p => p.Key).Select(p => (Cell: p.Key, Steps: p.Value))).ToArray();
        var ai = battle.Rules.AiDef;
        // Shoot where we stand whenever possible; approach only to get a legal shot.
        var canShootHere = targets.Any(t => new[] { "right", "left" }.Any(hand =>
            battle.Chance(u, u.X, u.Y, 0, battle.MoveRange, t, false, hand) is not null));
        if (canShootHere && battle.Mission.Objective?.Kind != "capture") cells = cells.Where(c => c.Steps == 0).ToArray();
        bool Capture(int cell) => battle.Mission.Objective?.Kind == "capture" && battle.Mission.Objective.Zone!.Any(p => field.Index(p.X, p.Y) == cell);
        (int Cell, int Steps, string Target, string Hand, string? Part, double Score)? best = null;
        foreach (var (cell, steps) in cells)
        foreach (var target in targets)
        foreach (var hand in new[] { "right", "left" })
        {
            if (battle.Gun(u, hand) is not { } gun) continue;
            var x = cell % field.Width; var y = cell / field.Width;
            if (battle.Chance(u, x, y, steps, battle.MoveRange, target, false, hand) is not { } chance) continue;
            var from = MechCombat.Side(x, y, target.X, target.Y, target.Dir);
            var block = battle.ShieldOf(target) is { } shield ? MechCombat.BlockChance(battle.Rules.CombatDef, shield.Shield,
                shield.Arm, target.PartAlive(shield.Arm), from) : 0;
            var score = chance * gun.Weapon.Damage / 100.0 - block * ai.ShieldPenalty;
            score += (battle.Gun(target)?.Weapon.Damage ?? 0) * ai.ThreatPriority;
            if (target == battle.ObjectiveUnit) score += ai.ObjectivePriority;
            if (Capture(cell)) score += ai.CapturePriority;
            string? part = null;
            foreach (var arm in new[] { MechPart.Right, MechPart.Left })
            {
                var id = arm == MechPart.Right ? target.Def.Right : target.Def.Left;
                if (target.PartAlive(arm) && battle.Rules.Weapon(id) is not null && target.Hp[(int)arm] <= MechCombat.Damage(gun.Weapon.Damage, battle.Armor(target, arm)))
                { part = MechCombat.Name(arm); score += ai.FinishArmScore; break; }
            }
            if (best is null || Capture(cell) && !Capture(best.Value.Cell)
                || Capture(cell) == Capture(best.Value.Cell) && (steps < best.Value.Steps
                    || steps == best.Value.Steps && score > best.Value.Score))
                best = (cell, steps, target.Id, hand, part, score);
        }
        var commands = new List<MechCommand>();
        if (best is { } shot)
        {
            if (shot.Cell != here) commands.Add(new("move", shot.Cell % field.Width, shot.Cell / field.Width));
            commands.Add(new("attack", Target: shot.Target, Part: shot.Part, Hand: shot.Hand));
            return commands;
        }
        MechPoint? goal = battle.Mission.Objective?.Kind == "capture"
            ? battle.Mission.Objective.Zone!.OrderBy(p => MechField.Distance(u.X, u.Y, p.X, p.Y)).ThenBy(p => field.Index(p.X, p.Y)).First()
            : null;
        var preferred = targets.OrderBy(t => t == battle.ObjectiveUnit ? 0 : 1)
            .ThenBy(t => MechField.Distance(u.X, u.Y, t.X, t.Y)).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
        goal ??= preferred is not null ? new(preferred.X, preferred.Y) : null;
        if (goal is not null)
        {
            var dist = field.Distances(goal.X, goal.Y, battle.Occupied(u));
            var cell = cells.OrderBy(c => dist.GetValueOrDefault(c.Cell, int.MaxValue)).ThenBy(c => c.Steps).ThenBy(c => c.Cell).First().Cell;
            if (cell != here) commands.Add(new("move", cell % field.Width, cell / field.Width));
        }
        commands.Add(new("end"));
        return commands;
    }
}
