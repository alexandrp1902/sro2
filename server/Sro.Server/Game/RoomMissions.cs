using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Комната и задания, за которыми надо следить по ходу тика (M14): срок письма, гибель пилота и «живые»
/// задания с актёрами в системе — сопровождение конвоя и патруль со звеном рейнджеров.
/// Охота и письмо обходятся событиями (<see cref="CountKill"/>, <see cref="Dock"/>); здесь — всё остальное.
/// </summary>
public sealed partial class Room
{
    /// <summary>Срок письма проверяется не каждый тик: раз в полсекунды достаточно, а часы всё равно стенные.</summary>
    private const int DeadlineCheckTicks = SimConfig.TickRate / 2;

    /// <summary>На столько конвой и звено отходят от пилота при появлении — чтобы не толкаться в одной точке.</summary>
    private const double EscortOffset = 140;

    /// <summary>Засада встаёт на столько впереди конвоя по курсу: он входит в неё через несколько секунд.</summary>
    private const double AmbushAhead = 1700;

    /// <summary>И на столько вбок, чтобы не вырастать ровно на линии курса.</summary>
    private const double AmbushSide = 400;

    /// <summary>Звено считается дошедшим до точки, если хоть кто-то из него так близко.</summary>
    private const double WingArrive = 500;

    /// <summary>Точки маршрута патруля не ближе этого друг к другу: иначе маршрут вырождается в одно место.</summary>
    private const double PatrolLeg = 1800;

    /// <summary>Сколько раз искать следующую точку маршрута подальше от предыдущей, прежде чем взять любую.</summary>
    private const int PatrolAttempts = 12;

    private readonly Dictionary<int, MissionRun> _runs = [];
    private int _runCount;

    /// <summary>Идущее живое задание пилота; null — нет.</summary>
    private MissionRun? RunOf(Player player) => _runs.GetValueOrDefault(player.Id);

    /// <summary>
    /// Пилот вылетел с заданием, у которого есть актёры: конвой или звено выходит вместе с ним.
    /// Живое задание начинается именно на вылете, а не при взятии на доске: его актёрам нужен живой корабль рядом.
    /// </summary>
    private void StartRun(Player player)
    {
        if (player.Missions.Active?.Offer is not { } offer || !MissionRules.IsLive(offer.Kind)) return;
        if (_runs.ContainsKey(player.Id)) return;
        var run = new MissionRun(++_runCount, player.Id, offer.Kind);
        var started = offer.Kind switch
        {
            MissionRules.EscortKind => StartEscort(run, player, offer),
            MissionRules.PatrolKind => StartPatrol(run, player, offer),
            _ => false,
        };
        if (!started)
        {
            // Отыграть нечем: некого вести или некому лететь. Пилот в этом не виноват — задание просто снимаем.
            _log.LogWarning("Player {Id} cannot start a {Kind} mission here", player.Id, offer.Kind);
            Abandon(player);
            return;
        }
        _runs[player.Id] = run;
        BroadcastPlayers();
        SendMissions(player);
    }

    /// <summary>
    /// Шаг заданий. Зовётся из <see cref="Step"/> после боя, пока <c>_kills</c> ещё не очищен и пока
    /// <see cref="RemoveGoneTraders"/> не убрал долетевший конвой: по тому и другому видно, чем всё кончилось.
    /// </summary>
    private void StepMissions()
    {
        foreach (var kill in _kills)
        {
            if (_players.GetValueOrDefault(kill.Id) is { Missions.Active.Offer.Kind: { } kind } dead &&
                MissionRules.DiesWithTheShip(kind))
                Fail(dead, Protocol.DeadFail);
        }
        if (_runs.Count > 0)
        {
            foreach (var run in _runs.Values.ToList())
            {
                if (_players.GetValueOrDefault(run.PlayerId) is not { } player ||
                    player.Missions.Active is not { } active ||
                    active.Offer.Kind != run.Kind)
                {
                    // Пилота в комнате уже нет или задание кончилось мимо нас — актёров убираем.
                    EndRun(run.PlayerId);
                    continue;
                }
                // Корабль-призрак после обрыва связи никого не ведёт: 60 секунд ждать конвою незачем.
                if (player.Connection is null)
                {
                    Fail(player, Protocol.LeftFail);
                    continue;
                }
                if (run.Kind == MissionRules.EscortKind) StepEscort(run, player, active);
                else StepPatrol(run, player, active);
            }
        }
        if (Tick % DeadlineCheckTicks != 0) return;
        var now = NowSeconds;
        foreach (var player in _players.Values)
        {
            if (player.Missions.Active is { Until: > 0 } timed && now >= timed.Until) Fail(player, Protocol.TimeFail);
        }
    }

    /// <summary>
    /// Конвой задания: тот же торговец, но по маршруту «отсюда к названным вратам», без груза рынка
    /// и с пометкой прогона. <see cref="LoadTrader"/> ему не зовём: иначе взятие задания опустошало бы
    /// склад станции, а гибель конвоя била бы по ценам (M12).
    /// </summary>
    private bool StartEscort(MissionRun run, Player player, MissionOffer offer)
    {
        if (Balance.Traders is not { } rules || !Balance.Npc.TypeMap.TryGetValue(rules.Type, out var type)) return false;
        if (offer.System is not { } to || Balance.SystemDef.GateTo(to) is not { } gate) return false;

        // Из врат и во врата — чуть ближе к центру, как ходят обычные торговцы.
        var arrival = Balance.Galaxy.ArrivalOffset;
        var r = Math.Sqrt(gate.X * gate.X + gate.Y * gate.Y);
        var k = r > arrival ? (r - arrival) / r : 1;
        var (dx, dy) = (gate.X * k, gate.Y * k);

        var angle = _ai.NextDouble() * 2 * Math.PI;
        var x = player.Ship.X + EscortOffset * Math.Cos(angle);
        var y = player.Ship.Y + EscortOffset * Math.Sin(angle);
        var trader = new Trader(_newId(), rules.Type, type, Balance.Npc)
        {
            MissionId = run.Id,
            ToStation = false,
            DestX = dx,
            DestY = dy,
            Gate = to,
        };
        trader.Ship = new ShipState { X = x, Y = y, Rot = Math.Atan2(dx - x, -(dy - y)) };
        trader.Revive(trader.Effective(Balance), 0);
        _traders.Add(trader);
        _ships[trader.Id] = trader;

        run.TraderId = trader.Id;
        run.Route = Math.Sqrt((dx - x) * (dx - x) + (dy - y) * (dy - y));
        _log.LogInformation("Player {Id} escorts convoy #{Convoy} to the gate to {To}", player.Id, trader.Id, to);
        return true;
    }

    /// <summary>
    /// Звено рейнджеров: налётчики, которые не уходят сами и не чинятся, с домом на первой точке маршрута.
    /// Выходят вместе с пилотом от станции — не «ждут где-то», а действительно сопровождают.
    /// </summary>
    private bool StartPatrol(MissionRun run, Player player, MissionOffer offer)
    {
        if (Balance.Missions.PatrolFor(offer) is not { } template) return false;
        if (!Balance.Npc.TypeMap.TryGetValue(template.Npc, out var type) || !type.IsRanger) return false;

        run.Points.AddRange(PatrolRoute(offer.Count));
        if (run.Points.Count == 0) return false;

        var npc = Balance.Npc;
        var first = run.Points[0];
        var level = Math.Clamp(template.Rank + Math.Max(0, Balance.SystemDef.Danger - 1), 1, NpcSpawn.MaxLevel);
        var spot = new NpcSpawn(template.Npc, level, first.X, first.Y, template.Wing);
        var gates = Balance.SystemDef.GateList;
        var gate = gates.Count > 0 ? gates[_ai.Next(gates.Count)] : null;
        var raidId = ++_raidCount;
        for (var slot = 0; slot < template.Wing; slot++)
        {
            var ranger = new Pirate(_newId(), spot, slot, type, npc)
            {
                RaidId = raidId,
                MissionId = run.Id,
                ExitX = gate?.X ?? first.X,
                ExitY = gate?.Y ?? first.Y,
                ExitIsGate = gate is not null,
                PatrolTicks = long.MaxValue / 4,
            };
            SpawnHere(ranger);
            var angle = SlotAngle * slot;
            var x = player.Ship.X + EscortOffset * Math.Cos(angle);
            var y = player.Ship.Y + EscortOffset * Math.Sin(angle);
            ranger.Ship = new ShipState { X = x, Y = y, Rot = Math.Atan2(first.X - x, -(first.Y - y)) };
            ranger.HomeX = first.X;
            ranger.HomeY = first.Y;
            ranger.State = PirateState.Return;
            _pirates.Add(ranger);
            _ships[ranger.Id] = ranger;
        }
        player.Connection?.Send(new NoticeMsg(Protocol.WingNotice));
        _log.LogInformation(
            "Player {Id} patrols with {Wing} × {Type} Ур.{Level} over {Points} points",
            player.Id, template.Wing, template.Npc, level, run.Points.Count);
        return true;
    }

    /// <summary>Золотой угол: места звена вокруг пилота не совпадают при любом его размере.</summary>
    private const double SlotAngle = 2.39996;

    /// <summary>
    /// Сопровождение: конвой идёт к вратам, пилот держится рядом, по дороге его ждут засады.
    /// Конвой ушёл в прыжок — работа сделана; погиб или пилот отстал надолго — провал.
    /// </summary>
    private void StepEscort(MissionRun run, Player player, ActiveMission active)
    {
        if (_ships.GetValueOrDefault(run.TraderId) is not Trader { IsDead: false } trader)
        {
            Fail(player, Protocol.TraderFail);
            return;
        }
        if (trader.Gone)
        {
            Complete(player);
            return;
        }

        var offer = active.Offer;
        // Засады идут по долям пути: при трёх волнах — на четверти, половине и трёх четвертях.
        if (run.Wave < offer.Count && run.Route > 1)
        {
            var left = Math.Sqrt(
                (trader.DestX - trader.Ship.X) * (trader.DestX - trader.Ship.X) +
                (trader.DestY - trader.Ship.Y) * (trader.DestY - trader.Ship.Y));
            var done = 1 - left / run.Route;
            if (done >= (run.Wave + 1) / (double)(offer.Count + 1))
            {
                SpawnAmbush(run, trader);
                run.Wave++;
                player.Missions.Active = active with { Progress = run.Wave };
                player.Connection?.Send(new NoticeMsg(Protocol.AmbushNotice));
                BroadcastPlayers();
                SendMissions(player);
                return;
            }
        }

        // Пока пилот в доке или мёртв, «рядом» ему быть нечем — отсчёт отставания идёт всё равно:
        // конвой не станет ждать, пока его охрана торгуется на станции.
        var dx = player.Ship.X - trader.Ship.X;
        var dy = player.Ship.Y - trader.Ship.Y;
        var near = !player.Docked && !player.IsDead && dx * dx + dy * dy <= offer.Radius * offer.Radius;
        if (near)
        {
            run.AwaySince = 0;
            return;
        }
        if (run.AwaySince == 0)
        {
            run.AwaySince = Tick;
            player.Connection?.Send(new NoticeMsg(Protocol.MissionAwayNotice));
            return;
        }
        var patience = Balance.Missions.EscortFor(offer)?.AwaySeconds ?? 0;
        if (patience > 0 && Tick - run.AwaySince >= Combat.SecondsToTicks(patience)) Fail(player, Protocol.AwayFail);
    }

    /// <summary>
    /// Патруль: точка засчитывается, только когда до неё дошли и звено, и пилот. Звено ждёт — это решение
    /// M14: со строгим поводком тяжёлый корпус не смог бы взять эту работу вовсе.
    /// </summary>
    private void StepPatrol(MissionRun run, Player player, ActiveMission active)
    {
        var wing = 0;
        var arrived = false;
        var (wx, wy) = run.Points[Math.Min(run.Point, run.Points.Count - 1)];
        foreach (var ranger in _pirates)
        {
            if (ranger.MissionId != run.Id || ranger.IsDead || ranger.Gone) continue;
            wing++;
            var rx = ranger.Ship.X - wx;
            var ry = ranger.Ship.Y - wy;
            if (rx * rx + ry * ry <= WingArrive * WingArrive) arrived = true;
        }
        if (wing == 0)
        {
            Fail(player, Protocol.WingFail);
            return;
        }
        if (!arrived || player.Docked || player.IsDead) return;

        var offer = active.Offer;
        var px = player.Ship.X - wx;
        var py = player.Ship.Y - wy;
        if (px * px + py * py > offer.Radius * offer.Radius) return;

        run.Point++;
        player.Missions.Active = active with { Progress = run.Point };
        if (run.Point >= offer.Count)
        {
            Complete(player);
            return;
        }
        MoveWing(run);
        SendMissions(player);
    }

    /// <summary>
    /// Звено идёт к следующей точке: дом переезжает, кто не в бою — разворачивается туда сразу, кто в бою —
    /// доведёт его и вернётся сам, поводок <see cref="NpcType.LeashRange"/> считается уже от новой точки.
    /// </summary>
    private void MoveWing(MissionRun run)
    {
        var (x, y) = run.Points[Math.Min(run.Point, run.Points.Count - 1)];
        foreach (var ranger in _pirates)
        {
            if (ranger.MissionId != run.Id || ranger.IsDead || ranger.Gone) continue;
            ranger.HomeX = x;
            ranger.HomeY = y;
            ranger.HasWaypoint = false;
            if (ranger.State != PirateState.Attack) ranger.State = PirateState.Return;
        }
    }

    /// <summary>Маршрут патруля: count точек по кольцу налётов, по возможности не жмущихся друг к другу.</summary>
    private List<(double X, double Y)> PatrolRoute(int count)
    {
        var route = new List<(double X, double Y)>();
        for (var i = 0; i < count; i++)
        {
            var best = RaidPoint();
            for (var attempt = 0; attempt < PatrolAttempts; attempt++)
            {
                var candidate = RaidPoint();
                var last = route.Count > 0 ? route[^1] : (X: 0.0, Y: 0.0);
                var dx = candidate.X - last.X;
                var dy = candidate.Y - last.Y;
                best = candidate;
                if (dx * dx + dy * dy >= PatrolLeg * PatrolLeg) break;
            }
            route.Add(best);
        }
        return route;
    }

    /// <summary>
    /// Засада на конвой: группа волны встаёт впереди по его курсу. Точка обязана быть вне жара звезды и вне
    /// укрытия станции — в укрытии пираты развернулись бы, не начав боя.
    /// </summary>
    private void SpawnAmbush(MissionRun run, Trader trader)
    {
        var wave = Balance.Missions.Wave(run.Wave);
        if (wave.Count == 0) return;
        SpawnWave(wave, AmbushPoint(trader), invasionId: 0, missionId: run.Id, onSite: true);
    }

    /// <summary>Точка засады: впереди конвоя по курсу и вбок, подальше от звезды и от укрытия станции.</summary>
    private (double X, double Y) AmbushPoint(Trader trader)
    {
        var dx = trader.DestX - trader.Ship.X;
        var dy = trader.DestY - trader.Ship.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var (ux, uy) = length > 1 ? (dx / length, dy / length) : (0.0, -1.0);
        var side = _ai.NextDouble() < 0.5 ? 1 : -1;
        var ahead = Math.Min(AmbushAhead, Math.Max(length * 0.5, 1));
        var x = trader.Ship.X + ux * ahead - uy * AmbushSide * side;
        var y = trader.Ship.Y + uy * ahead + ux * AmbushSide * side;

        var npc = Balance.Npc;
        var burn = (Balance.Sun?.BurnRadius ?? 0) + GalaxyRules.HeatMargin + npc.PatrolRadius;
        var shelter = Balance.HasStation ? Balance.StationPath.Radius + npc.StationSafeRadius + npc.PatrolRadius : 0;
        var min = Math.Max(burn, shelter);
        var radius = Math.Sqrt(x * x + y * y);
        if (radius < min)
        {
            // Внутрь запретного круга засаду не ставим — выталкиваем её наружу по тому же направлению.
            var (ox, oy) = radius > 1 ? (x / radius, y / radius) : (ux, uy);
            (x, y) = (ox * min, oy * min);
        }
        var limit = NpcRules.WorldLimit - npc.PatrolRadius;
        return (Math.Clamp(x, -limit, limit), Math.Clamp(y, -limit, limit));
    }

    /// <summary>
    /// Прогон окончен — чем бы он ни кончился. Конвой перестаёт быть заданием и долетает куда летел;
    /// звено уходит из системы, как пираты после отбитого вторжения.
    /// </summary>
    private void EndRun(int playerId)
    {
        if (!_runs.Remove(playerId, out var run)) return;
        if (_ships.GetValueOrDefault(run.TraderId) is Trader trader) trader.MissionId = 0;
        foreach (var ranger in _pirates)
        {
            if (ranger.MissionId != run.Id) continue;
            ranger.MissionId = 0;
            if (ranger.IsDead || ranger.State == PirateState.Leave) continue;
            ranger.State = PirateState.Leave;
            ranger.TargetId = 0;
            ranger.FireHeld = false;
            ranger.PatrolUntilTick = Tick;
        }
    }

    /// <summary>Баланс сменился на лету: прогон, чьих актёров больше нет, доигрывать нечем.</summary>
    private void ValidateRuns()
    {
        foreach (var run in _runs.Values.ToList())
        {
            if (_players.GetValueOrDefault(run.PlayerId) is not { } player) continue;
            var alive = run.Kind == MissionRules.EscortKind
                ? _ships.GetValueOrDefault(run.TraderId) is Trader { IsDead: false }
                : _pirates.Any(p => p.MissionId == run.Id && !p.IsDead && !p.Gone);
            if (!alive) Fail(player, run.Kind == MissionRules.EscortKind ? Protocol.TraderFail : Protocol.WingFail);
        }
    }

    /// <summary>
    /// Куда смотреть по живому заданию: за конвоем — по его кораблю (он ходит, и снапшот знает где),
    /// за патрулём — к текущей точке маршрута. null — метки нет.
    /// </summary>
    private MissionMarkDto? MarkOf(Player player)
    {
        if (RunOf(player) is not { } run) return null;
        if (run.Kind == MissionRules.EscortKind)
            return _ships.GetValueOrDefault(run.TraderId) is Trader { IsDead: false } ? new MissionMarkDto(run.TraderId, 0, 0) : null;
        if (run.Point >= run.Points.Count) return null;
        var (x, y) = run.Points[run.Point];
        return new MissionMarkDto(0, x, y);
    }
}
