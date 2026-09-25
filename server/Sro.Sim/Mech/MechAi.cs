namespace Sro.Sim.Mech;

/// <summary>
/// ИИ налётчика (M21): цель — ближайший мех другой стороны; из всех клеток, куда можно дойти (и своей), выбирается та,
/// откуда шанс попасть по ней выше всего — дальность, стоял ли, сторона цели уже учтены в шансе, поэтому
/// налётчик сам держит оптимум и заходит во фланг. Выстрелить неоткуда — идёт к цели кратчайшим путём.
/// Ничьи решаются числом шагов и индексом клетки: бой по одному сиду всегда один и тот же.
/// </summary>
public static class MechAi
{
    public static List<MechCommand> Decide(MechBattle battle)
    {
        var commands = new List<MechCommand>();
        if (battle.Current is not { } u) return commands;
        // Сторона цели — противоположная, а не всегда игрок: тем же ИИ тесты и смоук доигрывают бой за игрока.
        var enemies = u.Side == MechBattle.PlayerSide ? MechBattle.EnemySide : MechBattle.PlayerSide;
        var target = battle.Living(enemies)
            .OrderBy(t => MechField.Distance(u.X, u.Y, t.X, t.Y))
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (target is null)
        {
            commands.Add(new MechCommand(MechCommand.EndAct));
            return commands;
        }

        var field = battle.Field;
        var here = field.Index(u.X, u.Y);
        var reach = battle.Reach();
        var cells = new List<(int Cell, int Steps)> { (here, 0) };
        cells.AddRange(reach.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)));

        (int Cell, int Steps, int Chance)? best = null;
        foreach (var (cell, steps) in cells)
        {
            var chance = battle.Chance(u, cell % field.Width, cell / field.Width, steps, battle.MoveRange, target, aimed: false);
            if (chance is not { } c) continue;
            if (best is null || c > best.Value.Chance || (c == best.Value.Chance && steps < best.Value.Steps))
                best = (cell, steps, c);
        }

        if (best is { } shot)
        {
            if (shot.Cell != here) commands.Add(new MechCommand(MechCommand.MoveAct, shot.Cell % field.Width, shot.Cell / field.Width));
            commands.Add(new MechCommand(MechCommand.AttackAct, Target: target.Id));
            return commands;
        }

        // Выстрелить неоткуда: ближе к цели по земле, а не по прямой — иначе упрётся в стену.
        var toTarget = field.Distances(target.X, target.Y, battle.Occupied(u));
        var bestCell = here;
        var bestDist = toTarget.GetValueOrDefault(here, int.MaxValue);
        foreach (var (cell, _) in reach.OrderBy(p => p.Key))
        {
            var d = toTarget.GetValueOrDefault(cell, int.MaxValue);
            if (d < bestDist)
            {
                bestDist = d;
                bestCell = cell;
            }
        }
        var (x, y) = (bestCell % field.Width, bestCell / field.Width);
        if (bestCell != here) commands.Add(new MechCommand(MechCommand.MoveAct, x, y));
        commands.Add(new MechCommand(MechCommand.EndAct, Dir: MechField.Direction(target.X - x, target.Y - y)));
        return commands;
    }
}
