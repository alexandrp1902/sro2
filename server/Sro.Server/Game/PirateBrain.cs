using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// ИИ пирата — конечный автомат: патруль → бой → возврат в логово (GDD §31). Раз в тик выставляет пирату вход, огонь
/// и цель — ровно то, что игрок присылает с клиента; сектор, дальность и шанс проверяет Battle, как для всех.
/// </summary>
internal static class PirateBrain
{
    private const double DegToRad = Math.PI / 180;

    /// <summary>Цель дальше этого угла от носа — сначала разворот на месте, без тяги: так быстрее, чем по дуге.</summary>
    private const double TurnFirstAngle = 45 * DegToRad;
    /// <summary>Тяга растёт от 0 на своей дистанции до 1 на столько дальше.</summary>
    private const double ThrottleRamp = 250;
    /// <summary>Издалека пират заходит под углом к цели — она остаётся в секторе, а пираты логова не летят гуськом.</summary>
    private const double ApproachAngle = 25 * DegToRad;
    private const double ApproachFrom = 150;
    /// <summary>Пираты ближе этого расталкиваются, пока летят.</summary>
    private const double SeparationRadius = 80;
    /// <summary>Сам пират не подлетает к укрытию ближе этого запаса.</summary>
    private const double SafeMargin = 100;

    /// <summary>Пушка с таким сектором бьёт вбок: пират не разворачивается к цели, а кружит вокруг неё.</summary>
    private const double OrbitArc = 90;
    /// <summary>
    /// Запас к сектору самой узкой пушки на круге: с ракетницей (±90°) пират летит не строго по касательной,
    /// а чуть к цели — иначе она всё время на самой границе сектора.
    /// </summary>
    private const double OrbitArcMargin = 20;
    /// <summary>Тяга на круге: пират в движении — по нему труднее попасть (боевой документ §40).</summary>
    private const double OrbitThrottle = 0.6;
    private const double MinOrbitThrottle = 0.3;
    /// <summary>Насколько сильно пират тянется к своей дистанции, если его снесло с круга.</summary>
    private const double OrbitPull = 1.5;

    /// <summary>Точка патруля достигнута. Больше радиуса разворота на патрульной тяге — иначе пират кружил бы вокруг точки.</summary>
    private const double ArriveRadius = 50;
    /// <summary>Ближе этого к точке пират сбавляет тягу.</summary>
    private const double SlowRadius = 200;
    private const double MinArriveThrottle = 0.2;
    /// <summary>Не долетел до точки патруля за это время — выбирает другую.</summary>
    private const int WaypointTicks = 20 * SimConfig.TickRate;
    /// <summary>Возврат окончен — пират в логове.</summary>
    private const double HomeRadius = 60;
    /// <summary>Налётчик ближе этого к вратам или базе — на месте: готовит прыжок или садится.</summary>
    private const double ExitRadius = 120;

    /// <param name="station">Где сейчас станция: укрытие ходит вместе с ней по орбите.</param>
    public static void Think(
        Pirate pirate,
        IReadOnlyDictionary<int, ShipEntity> ships,
        IReadOnlyList<Pirate> pirates,
        Balance balance,
        long tick,
        Random rng,
        ILogger log,
        (double X, double Y) station = default)
    {
        var npc = balance.Npc;
        var shelter = new Shelter(station.X, station.Y, npc.StationSafeRadius);
        var hull = pirate.Hull(balance.Hulls);
        var attacker = pirate.LastAttackerId;
        pirate.LastAttackerId = 0; // нападение учитывается один раз

        if (pirate.State == PirateState.Leave)
        {
            Leave(pirate, balance, tick, log);
            return;
        }

        if (pirate.State == PirateState.Return)
        {
            if (Distance(pirate.Ship.X, pirate.Ship.Y, pirate.HomeX, pirate.HomeY) > HomeRadius)
            {
                FlyTo(pirate, pirate.HomeX, pirate.HomeY, 1);
                return;
            }
            pirate.Repair(hull);
            pirate.State = PirateState.Patrol;
            pirate.HasWaypoint = false;
            // Налётчик долетел до места: отсюда и идёт время его патруля.
            if (pirate.IsRaider && pirate.PatrolUntilTick == 0) pirate.PatrolUntilTick = tick + pirate.PatrolTicks;
            log.LogInformation("{Pirate} is back home and repaired", pirate);
        }

        if (pirate.State == PirateState.Attack)
        {
            var target = ships.GetValueOrDefault(pirate.TargetId);
            if (target is null || !IsFair(target, tick) || Distance(pirate, target) > npc.DropRange)
            {
                pirate.State = PirateState.Patrol;
                pirate.TargetId = 0;
                log.LogInformation("{Pirate} lost its target", pirate);
            }
            else if (ReturnReason(pirate, target, hull, npc, shelter) is { } reason)
            {
                // Подбитый налётчик не чинится дома, а бежит из системы.
                if (pirate.IsRaider && reason == "retreat") StartLeave(pirate, reason, log);
                else StartReturn(pirate, reason, log);
                Think(pirate, ships, pirates, balance, tick, rng, log, station);
                return;
            }
            else
            {
                Attack(pirate, target, hull, balance, pirates);
                return;
            }
        }

        // Патруль: сначала — не пора ли в бой.
        if (Acquire(pirate, attacker, ships, pirates, npc, shelter, tick) is { } found)
        {
            if (pirate.Hp <= pirate.MaxHp(hull) * pirate.Type.RetreatHp)
            {
                if (pirate.IsRaider) StartLeave(pirate, "retreat", log);
                else StartReturn(pirate, "retreat", log);
                Think(pirate, ships, pirates, balance, tick, rng, log, station);
                return;
            }
            pirate.State = PirateState.Attack;
            pirate.TargetId = found.Id;
            log.LogInformation("{Pirate} attacks {Target}", pirate, found.Name);
            Attack(pirate, found, hull, balance, pirates);
            return;
        }
        // Налётчик отпатрулировал своё и никого не нашёл — пора домой, во врата.
        if (pirate.IsRaider && pirate.PatrolUntilTick > 0 && tick >= pirate.PatrolUntilTick)
        {
            StartLeave(pirate, "patrol is over", log);
            Leave(pirate, balance, tick, log);
            return;
        }
        Patrol(pirate, npc, tick, rng);
    }

    private static void StartLeave(Pirate pirate, string reason, ILogger log)
    {
        pirate.State = PirateState.Leave;
        pirate.TargetId = 0;
        log.LogInformation("{Pirate} leaves the system: {Reason}", pirate, reason);
    }

    /// <summary>
    /// Уход налётчика: к вратам на полной тяге, там — подготовка прыжка, как у игрока (её видно кольцом), и исчезновение.
    /// На базе пиратской системы — сразу, будто сел в ангар.
    /// </summary>
    private static void Leave(Pirate pirate, Balance balance, long tick, ILogger log)
    {
        pirate.FireHeld = false;
        if (pirate.LeaveAtTick > 0)
        {
            Set(pirate, pirate.LastInput.Dx, pirate.LastInput.Dy, 0);
            if (tick < pirate.LeaveAtTick) return;
            pirate.Gone = true;
            log.LogInformation("{Pirate} jumped away", pirate);
            return;
        }
        if (Distance(pirate.Ship.X, pirate.Ship.Y, pirate.ExitX, pirate.ExitY) > ExitRadius)
        {
            FlyTo(pirate, pirate.ExitX, pirate.ExitY, 1);
            return;
        }
        Set(pirate, pirate.LastInput.Dx, pirate.LastInput.Dy, 0);
        if (pirate.ExitIsGate)
        {
            pirate.LeaveAtTick = tick + balance.Galaxy.JumpTicks;
            return;
        }
        pirate.Gone = true;
        log.LogInformation("{Pirate} landed at the base", pirate);
    }

    /// <summary>
    /// Честная цель: игрок на связи, цел и без защиты после появления, или торговец (GDD §31). Дронов и корабли
    /// без связи пираты не трогают.
    /// </summary>
    private static bool IsFair(ShipEntity ship, long tick) => ship switch
    {
        Player player => player.Connection is not null && !player.IsDead && !player.IsProtected(tick),
        Trader trader => !trader.IsDead,
        _ => false,
    };

    /// <summary>Укрытие у станции в этот тик.</summary>
    private readonly record struct Shelter(double X, double Y, double Radius)
    {
        public bool Contains(ShipEntity ship, double margin = 0) => Distance(ship.Ship.X, ship.Ship.Y, X, Y) <= Radius + margin;
    }

    private static bool IsCandidate(ShipEntity? ship, long tick, Shelter shelter) =>
        ship is not null && IsFair(ship, tick) && !shelter.Contains(ship);

    /// <summary>Кого атаковать: того, кто напал; иначе ближайшего игрока в радиусе агро; иначе цель собрата по бою рядом.</summary>
    private static ShipEntity? Acquire(
        Pirate pirate,
        int attackerId,
        IReadOnlyDictionary<int, ShipEntity> ships,
        IReadOnlyList<Pirate> pirates,
        NpcRules npc,
        Shelter shelter,
        long tick)
    {
        if (ships.GetValueOrDefault(attackerId) is { } attacker && IsCandidate(attacker, tick, shelter) && Distance(pirate, attacker) <= npc.DropRange)
            return attacker;

        ShipEntity? nearest = null;
        var nearestDistance = npc.AggroRange;
        foreach (var ship in ships.Values)
        {
            if (!IsCandidate(ship, tick, shelter)) continue;
            var distance = Distance(pirate, ship);
            if (distance > nearestDistance) continue;
            nearest = ship;
            nearestDistance = distance;
        }
        if (nearest is not null) return nearest;

        foreach (var other in pirates)
        {
            if (other == pirate || other.IsDead || other.State != PirateState.Attack || Distance(pirate, other) > npc.AssistRange) continue;
            if (ships.GetValueOrDefault(other.TargetId) is { } target && IsCandidate(target, tick, shelter) && Distance(pirate, target) <= npc.DropRange)
                return target;
        }
        return null;
    }

    /// <returns>Причина бросить бой и уйти в логово, или null — продолжать.</returns>
    private static string? ReturnReason(Pirate pirate, ShipEntity target, HullParams hull, NpcRules npc, Shelter shelter)
    {
        if (pirate.Hp <= pirate.MaxHp(hull) * pirate.Type.RetreatHp) return "retreat";
        if (shelter.Contains(target)) return "target in the shelter";
        if (shelter.Contains(pirate, SafeMargin))
            return "too close to the shelter";
        if (Distance(pirate.Ship.X, pirate.Ship.Y, pirate.HomeX, pirate.HomeY) > npc.LeashRange) return "too far from home";
        return null;
    }

    private static void StartReturn(Pirate pirate, string reason, ILogger log)
    {
        pirate.State = PirateState.Return;
        pirate.TargetId = 0;
        log.LogInformation("{Pirate} returns home: {Reason}", pirate, reason);
    }

    /// <summary>
    /// Бой. Пушка бьёт вбок (сектор ≥ 90°) — пират кружит вокруг цели на своей дистанции, как по орбите.
    /// Узкий сектор (боевой документ §34: держать цель в ±60° и лететь по касательной нельзя) — «сначала развернуться,
    /// потом держать дистанцию»: разворот носом к цели на месте, а тягой — своя дистанция: пропорционально отставанию
    /// плюс скорость, с которой цель удаляется.
    /// </summary>
    private static void Attack(Pirate pirate, ShipEntity target, HullParams hull, Balance balance, IReadOnlyList<Pirate> pirates)
    {
        // Кружить — если хоть одна пушка бьёт вбок; угол к цели — по самой узкой из них.
        var widest = 0.0;
        var narrowest = 180.0;
        foreach (var weapon in pirate.Weapons(balance))
        {
            widest = Math.Max(widest, weapon.Arc);
            narrowest = Math.Min(narrowest, weapon.Arc);
        }
        var s = pirate.Ship;
        var dx = target.Ship.X - s.X;
        var dy = target.Ship.Y - s.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 1e-6)
        {
            dx = Math.Sin(s.Rot);
            dy = -Math.Cos(s.Rot);
            distance = 1;
        }
        var ux = dx / distance;
        var uy = dy / distance;
        var away = target.Ship.Vx * ux + target.Ship.Vy * uy;
        double dirX, dirY, throttle;

        if (widest >= OrbitArc)
        {
            // По касательной в свою сторону, с поправкой к дистанции: далеко — внутрь, близко — наружу.
            var error = (distance - pirate.HoldRange) / ThrottleRamp;
            var tangent = Math.Clamp(narrowest - OrbitArcMargin, TurnFirstAngle / DegToRad, 90) * DegToRad;
            var (tx, ty) = Rotate(ux, uy, pirate.Side * tangent);
            var pull = Math.Clamp(error, -1, 1) * OrbitPull;
            dirX = tx + ux * pull;
            dirY = ty + uy * pull;
            throttle = Math.Clamp(OrbitThrottle + error + away / hull.MaxSpeed, MinOrbitThrottle, 1);
        }
        else
        {
            var off = Math.Abs(Movement.WrapAngle(Math.Atan2(ux, -uy) - s.Rot));
            throttle = off > TurnFirstAngle ? 0 : Math.Clamp((distance - pirate.HoldRange) / ThrottleRamp + away / hull.MaxSpeed, 0, 1);
            var angle = distance > pirate.HoldRange + ApproachFrom ? pirate.Side * ApproachAngle : 0;
            (dirX, dirY) = Rotate(ux, uy, angle);
        }
        if (throttle > 0) (dirX, dirY) = Separate(pirate, dirX, dirY, pirates);

        Set(pirate, dirX, dirY, throttle);
        pirate.FireHeld = true;
    }

    private static void Patrol(Pirate pirate, NpcRules npc, long tick, Random rng)
    {
        var s = pirate.Ship;
        if (!pirate.HasWaypoint || tick >= pirate.WaypointUntilTick || Distance(s.X, s.Y, pirate.WaypointX, pirate.WaypointY) <= ArriveRadius)
        {
            // Равномерно по кругу патруля, но не за границей мира.
            var radius = npc.PatrolRadius * Math.Sqrt(rng.NextDouble());
            var angle = rng.NextDouble() * 2 * Math.PI;
            pirate.WaypointX = Math.Clamp(pirate.HomeX + radius * Math.Cos(angle), -NpcRules.WorldLimit, NpcRules.WorldLimit);
            pirate.WaypointY = Math.Clamp(pirate.HomeY + radius * Math.Sin(angle), -NpcRules.WorldLimit, NpcRules.WorldLimit);
            pirate.WaypointUntilTick = tick + WaypointTicks;
            pirate.HasWaypoint = true;
        }
        pirate.TargetId = 0;
        FlyTo(pirate, pirate.WaypointX, pirate.WaypointY, npc.PatrolThrottle);
    }

    /// <summary>Лететь к точке, сбавляя тягу на подлёте; огня нет.</summary>
    private static void FlyTo(Pirate pirate, double x, double y, double maxThrottle)
    {
        var dx = x - pirate.Ship.X;
        var dy = y - pirate.Ship.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var throttle = Math.Min(maxThrottle, Math.Clamp(distance / SlowRadius, MinArriveThrottle, 1));
        Set(pirate, dx, dy, throttle);
        pirate.FireHeld = false;
    }

    /// <summary>Толчок от пиратов ближе SeparationRadius: чем ближе, тем сильнее.</summary>
    private static (double X, double Y) Separate(Pirate pirate, double dirX, double dirY, IReadOnlyList<Pirate> pirates)
    {
        foreach (var other in pirates)
        {
            if (other == pirate || other.IsDead) continue;
            var ox = pirate.Ship.X - other.Ship.X;
            var oy = pirate.Ship.Y - other.Ship.Y;
            var d = Math.Sqrt(ox * ox + oy * oy);
            if (d >= SeparationRadius || d < 1e-6) continue;
            var push = (SeparationRadius - d) / SeparationRadius;
            dirX += ox / d * push;
            dirY += oy / d * push;
        }
        return (dirX, dirY);
    }

    private static void Set(Pirate pirate, double dx, double dy, double throttle)
    {
        MoveInput.TryCreate(dx, dy, throttle, out var input);
        pirate.LastInput = input;
    }

    /// <summary>Поворот по часовой стрелке на экране (y вниз) на angle радиан.</summary>
    private static (double X, double Y) Rotate(double x, double y, double angle)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        return (x * cos - y * sin, x * sin + y * cos);
    }

    private static double Distance(ShipEntity a, ShipEntity b) => Distance(a.Ship.X, a.Ship.Y, b.Ship.X, b.Ship.Y);

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
