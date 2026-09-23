using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Обучение, за которым надо следить по ходу тика (M18): шаг «долетите до буя и остановитесь».
/// Остальные шаги обходятся событиями — продажей, прыжком, сбитым дроном (<see cref="Advance"/>).
/// </summary>
public sealed partial class Room
{
    /// <summary>
    /// Учебный буй пилота: висит у места, откуда он вылетел, и ходит с ним по орбите — как станция
    /// и поселение. Буй — только у того, чей текущий шаг «остановиться»; в мире его нет, он — метка
    /// этого пилота, поэтому и чужому не мешает.
    /// </summary>
    private (PlaceDef Place, TutorialBuoy Offset)? BuoyOf(Player player)
    {
        if (Balance.Missions.Step(player.Career, player.Missions.Tutorial) is not { What: MissionRules.StopStep } step) return null;
        return HomePlaceOf(player) is { } place ? (place, step.BuoyOrDefault) : null;
    }

    /// <summary>
    /// Засчитать остановку у буя: пилот разогнался после вылета, долетел и простоял у буя секунду.
    /// Разгон нужен затем, чтобы шаг не закрыл корабль, так и не тронувший газ у дока.
    /// </summary>
    private void StepTutorial()
    {
        foreach (var player in _players.Values)
        {
            if (player.Docked || player.IsDead || player.Connection is null) continue;
            if (BuoyOf(player) is not { } buoy) continue;
            var log = player.Missions;
            var speed = Math.Sqrt(player.Ship.Vx * player.Ship.Vx + player.Ship.Vy * player.Ship.Vy);
            if (speed > MissionRules.MovedSpeed) log.Moved = true;
            var (bx, by) = buoy.Place.Orbit.ToWorld(OrbitSeconds, buoy.Offset.X, buoy.Offset.Y);
            var dx = player.Ship.X - bx;
            var dy = player.Ship.Y - by;
            var still = log.Moved && speed < MissionRules.StillSpeed &&
                dx * dx + dy * dy <= MissionRules.BuoyRadius * MissionRules.BuoyRadius;
            if (!still)
            {
                log.StillSince = null;
                continue;
            }
            log.StillSince ??= OrbitSeconds;
            if (OrbitSeconds - log.StillSince.Value >= MissionRules.StillSeconds)
                Advance(player, new TutorialEvent(MissionRules.StopStep));
        }
    }

    /// <summary>Текущий шаг обучения для клиента; null — обучения нет.</summary>
    private TutorialDto? TutorialOf(Player player)
    {
        var rules = Balance.Missions;
        var log = player.Missions;
        var index = rules.IndexOf(player.Career, log.Tutorial);
        if (rules.Step(player.Career, index) is not { } step) return null;
        var buoy = BuoyOf(player) is { } b ? new BuoyDto(b.Place.Key, b.Offset.X, b.Offset.Y) : null;
        // Место продажи в другой системе — клиенту нужна система, чтобы указать на врата к ней.
        var system = step.System ?? Balance.Galaxy.SystemOfPlace(step.Place);
        return new TutorialDto(
            index, rules.StepsFor(player.Career).Count, step.Id, step.Title, step.Hint,
            step.What, step.HintTouch, step.Place, system, buoy);
    }
}
