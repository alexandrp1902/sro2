using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Комната и задания, за которыми надо следить по ходу тика (M14): срок письма и гибель пилота.
/// Остальные виды обходятся событиями — <see cref="CountKill"/>, <see cref="Dock"/>, <see cref="Grab"/>.
/// </summary>
public sealed partial class Room
{
    /// <summary>Срок письма проверяется не каждый тик: раз в полсекунды достаточно, а часы всё равно стенные.</summary>
    private const int DeadlineCheckTicks = SimConfig.TickRate / 2;

    /// <summary>
    /// Шаг заданий. Зовётся из <see cref="Step"/> после боя, пока <c>_kills</c> ещё не очищен: по нему и видно,
    /// что пилот погиб. Письмо тонет вместе с кораблём — это решение M14, а не побочный эффект.
    /// </summary>
    private void StepMissions()
    {
        foreach (var kill in _kills)
        {
            if (_players.GetValueOrDefault(kill.Id) is { Missions.Active.Offer.Kind: { } kind } dead &&
                MissionRules.DiesWithTheShip(kind))
                Fail(dead, Protocol.DeadFail);
        }
        if (Tick % DeadlineCheckTicks != 0) return;
        var now = NowSeconds;
        foreach (var player in _players.Values)
        {
            if (player.Missions.Active is { Until: > 0 } timed && now >= timed.Until) Fail(player, Protocol.TimeFail);
        }
    }
}
