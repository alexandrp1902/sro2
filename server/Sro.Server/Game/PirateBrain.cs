using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// ИИ пирата и рейнджера — конечный автомат: патруль → бой → возврат в логово (GDD §31). Пират ищет пилотов,
/// торговцев и рейнджеров; рейнджер — пиратов и тех, кто недавно напал на торговца (offenders в <see cref="Think"/>).
/// Пираты и рейнджеры бросаются друг на друга, как только заметят, — если у чужой стороны рядом нет явного перевеса
/// (<see cref="NpcRules.OutmatchRatio"/>); при перевесе не нападают, а из боя уходят. Раз в тик выставляет пирату вход, огонь
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
    /// <param name="offenders">Кто напал на торговца — id и до какого тика рейнджеры это помнят; null — никто.</param>
    public static void Think(
        Pirate pirate,
        IReadOnlyDictionary<int, ShipEntity> ships,
        IReadOnlyList<Pirate> pirates,
        Balance balance,
        long tick,
        Random rng,
        ILogger log,
        (double X, double Y) station = default,
        IReadOnlyDictionary<int, long>? offenders = null,
        IReadOnlySet<int>? outlaws = null)
    {
        var npc = balance.Npc;
        var shelter = new Shelter(station.X, station.Y, npc.StationSafeRadius);
        var hull = pirate.Hull(balance.Hulls);
        var heat = balance.Sun?.BurnRadius ?? 0;
        var attacker = pirate.LastAttackerId;
        pirate.LastAttackerId = 0; // нападение учитывается один раз

        // По пирату в пути стреляют. Целый налётчик, который летит к точке или уходит, разворачивается и дерётся;
        // подбитый, пират логова на поводке и тот, кто уже готовит прыжок, огрызаются на ходу, не сворачивая.
        if (pirate.State is PirateState.Leave or PirateState.Return &&
            ships.GetValueOrDefault(attacker) is { } foe && IsCandidate(pirate, foe, tick, shelter) && Distance(pirate, foe) <= npc.DropRange)
        {
            // Уходит от превосходящих сил — не разворачивается, иначе каждый тик то в бой, то снова прочь.
            if (pirate.IsRaider && pirate.LeaveAtTick == 0 && pirate.Hp > pirate.MaxHp(hull) * pirate.RetreatHp &&
                !IsOutmatched(pirate, foe, pirates, balance))
            {
                pirate.State = PirateState.Attack;
                pirate.TargetId = foe.Id;
                log.LogInformation("{Pirate} turns on {Target} on its way", pirate, foe.Name);
            }
            else pirate.Avenge = foe.Id;
        }

        if (pirate.State == PirateState.Leave)
        {
            Leave(pirate, balance, tick, log);
            ReturnFire(pirate, ships, npc.DropRange, tick);
            return;
        }

        if (pirate.State == PirateState.Return)
        {
            if (Distance(pirate.Ship.X, pirate.Ship.Y, pirate.HomeX, pirate.HomeY) > HomeRadius)
            {
                FlyTo(pirate, pirate.HomeX, pirate.HomeY, 1, heat);
                ReturnFire(pirate, ships, npc.DropRange, tick);
                return;
            }
            pirate.Avenge = 0;
            // Пират вторжения на точке сбора не чинится: иначе его можно было бы увести на поводке и вылечить.
            // Звено задания — тоже: его дом переезжает по маршруту, и оно чинилось бы на каждой точке (M14).
            if (pirate.HealsAtHome) pirate.Repair(hull);
            pirate.State = PirateState.Patrol;
            pirate.HasWaypoint = false;
            // Налётчик долетел до места: отсюда и идёт время его патруля.
            if (pirate.IsRaider && pirate.PatrolUntilTick == 0) pirate.PatrolUntilTick = tick + pirate.PatrolTicks;
            log.LogInformation("{Pirate} is back home and repaired", pirate);
        }

        if (pirate.State == PirateState.Attack)
        {
            var target = ships.GetValueOrDefault(pirate.TargetId);
            // Рейнджер идёт на обидчика издалека (defendRange) — и бросать его должен не ближе, иначе дёргался бы каждый тик.
            // Добивает торговца, а по нему самому стреляют пилот или рейнджер — отвечает тому, кто опаснее.
            if (target is Trader && ships.GetValueOrDefault(attacker) is { } rival and not Trader &&
                IsCandidate(pirate, rival, tick, shelter) && Distance(pirate, rival) <= npc.DropRange)
            {
                target = rival;
                pirate.TargetId = rival.Id;
                log.LogInformation("{Pirate} leaves the trader for {Target}", pirate, rival.Name);
            }
            if (target is null || !CanFight(pirate, target, tick) || Distance(pirate, target) > Math.Max(npc.DropRange, pirate.Type.DefendRange))
            {
                pirate.State = PirateState.Patrol;
                pirate.TargetId = 0;
                log.LogInformation("{Pirate} lost its target", pirate);
                // Налётчика перехватили в пути — бой окончен, и он летит дальше, куда летел.
                if (pirate.IsRaider && pirate.PatrolUntilTick == 0)
                {
                    pirate.State = PirateState.Return;
                    Think(pirate, ships, pirates, balance, tick, rng, log, station, offenders);
                    return;
                }
            }
            else if ((ReturnReason(pirate, target, hull, npc, shelter, OnTheWay(pirate, tick)) ??
                      (IsOutmatched(pirate, target, pirates, balance) ? Outmatched : null)) is { } reason)
            {
                // Подбитый налётчик не чинится дома, а бежит из системы; уходивший — уходит дальше.
                if (pirate.IsRaider && (reason is "retreat" or Outmatched || Leaving(pirate, tick))) StartLeave(pirate, reason, log);
                else StartReturn(pirate, reason, log);
                Think(pirate, ships, pirates, balance, tick, rng, log, station, offenders);
                return;
            }
            else
            {
                Attack(pirate, target, hull, balance, pirates);
                return;
            }
        }

        // Патруль: сначала — не пора ли в бой.
        if (Acquire(pirate, attacker, ships, pirates, balance, shelter, tick, offenders, outlaws) is { } found)
        {
            if (pirate.Hp <= pirate.MaxHp(hull) * pirate.RetreatHp)
            {
                if (pirate.IsRaider) StartLeave(pirate, "retreat", log);
                else StartReturn(pirate, "retreat", log);
                Think(pirate, ships, pirates, balance, tick, rng, log, station, offenders);
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
        Patrol(pirate, npc, tick, rng, heat);
    }

    /// <summary>Налётчик ещё летит к точке патруля или уже уходит: его поводок — не логово, а маршрут.</summary>
    private static bool OnTheWay(Pirate pirate, long tick) => pirate.IsRaider && (pirate.PatrolUntilTick == 0 || Leaving(pirate, tick));

    private static bool Leaving(Pirate pirate, long tick) => pirate.IsRaider && pirate.PatrolUntilTick > 0 && tick >= pirate.PatrolUntilTick;

    /// <summary>Огонь на ходу по тому, кто стрелял в пути, пока тот цел и рядом; курс не меняется.</summary>
    private static void ReturnFire(Pirate pirate, IReadOnlyDictionary<int, ShipEntity> ships, double range, long tick)
    {
        if (ships.GetValueOrDefault(pirate.Avenge) is { } foe && CanFight(pirate, foe, tick) && Distance(pirate, foe) <= range)
        {
            pirate.TargetId = foe.Id;
            pirate.FireHeld = true;
            return;
        }
        pirate.Avenge = 0;
        pirate.TargetId = 0;
        pirate.FireHeld = false;
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
        // Стоя на месте курс не правим: поправка от звезды только крутила бы налётчика, пока он заряжает прыжок.
        if (pirate.LeaveAtTick > 0)
        {
            Set(pirate, pirate.LastInput.Dx, pirate.LastInput.Dy, 0, 0);
            if (tick < pirate.LeaveAtTick) return;
            pirate.Gone = true;
            log.LogInformation("{Pirate} jumped away", pirate);
            return;
        }
        if (Distance(pirate.Ship.X, pirate.Ship.Y, pirate.ExitX, pirate.ExitY) > ExitRadius)
        {
            FlyTo(pirate, pirate.ExitX, pirate.ExitY, 1, balance.Sun?.BurnRadius ?? 0);
            return;
        }
        Set(pirate, pirate.LastInput.Dx, pirate.LastInput.Dy, 0, 0);
        if (pirate.ExitIsGate)
        {
            pirate.LeaveAtTick = tick + balance.Galaxy.JumpTicks;
            return;
        }
        pirate.Gone = true;
        log.LogInformation("{Pirate} landed at the base", pirate);
    }

    /// <summary>
    /// С кем этот NPC вообще воюет. Пилот — если на связи, цел и без защиты после появления; дронов и корабли без связи
    /// не трогает никто. Торговец — добыча только пиратов. Пираты и рейнджеры — враги друг другу.
    /// </summary>
    private static bool CanFight(Pirate self, ShipEntity ship, long tick) => ship != self && !ship.IsDead && ship switch
    {
        Player player => player.Connection is not null && !player.IsProtected(tick),
        Trader => self.Type.IsPirate,
        Pirate other => other.Type.IsRanger != self.Type.IsRanger,
        _ => false,
    };

    /// <summary>
    /// Кого NPC ищет сам, без нападения на него: пират — пилотов и торговцев (GDD §31); рейнджер — тех, кто недавно
    /// напал на торговца, пилот это или пират.
    /// </summary>
    private static bool Wants(Pirate self, ShipEntity ship, long tick, IReadOnlyDictionary<int, long>? offenders, IReadOnlySet<int>? outlaws)
    {
        if (!CanFight(self, ship, tick)) return false;
        // Вызванный за одним пилотом ищет только его (M20b): в комнате летают посторонние, и сюжетная
        // сцена не повод расстреливать тех, кто в ней не участвует.
        if (self.OwnerId != 0) return ship.Id == self.OwnerId;
        if (ship is Pirate) return true; // CanFight уже проверил: чужая фракция — враг с первого взгляда
        if (self.Type.IsRanger) return offenders is not null && offenders.GetValueOrDefault(ship.Id) > tick;
        // Кого власти объявили врагом, того пираты считают своим и не трогают (M13).
        if (ship is Player && outlaws?.Contains(ship.Id) == true) return false;
        return ship is Player or Trader;
    }

    private const string Outmatched = "outmatched";

    /// <summary>
    /// У чужой фракции рядом явный перевес (<see cref="NpcRules.OutmatchRatio"/>): бой с NPC этой фракции не начинать,
    /// а начатый — бросить. Считаются NPC обеих сторон: свои — в радиусе помощи от себя, чужие — в радиусе потери цели.
    /// Кто бьётся до конца — вторжение и корабли задания — не отступает никогда; у своего логова NPC тоже
    /// держится: отступать дальше некуда.
    /// </summary>
    private static bool IsOutmatched(Pirate self, ShipEntity target, IReadOnlyList<Pirate> pirates, Balance balance)
    {
        if (target is not Pirate || self.NeverRetreats) return false;
        var npc = balance.Npc;
        if (Distance(self.Ship.X, self.Ship.Y, self.HomeX, self.HomeY) <= npc.PatrolRadius && !self.IsRaider) return false;
        double ownHp = 0, ownDps = 0, foeHp = 0, foeDps = 0;
        foreach (var ship in pirates)
        {
            if (ship.IsDead) continue;
            var distance = Distance(self, ship);
            if (ship.Type.IsRanger == self.Type.IsRanger)
            {
                if (distance > npc.AssistRange) continue;
                ownHp += ship.Hp + ship.Shield;
                ownDps += Dps(ship, balance);
            }
            else if (distance <= npc.DropRange)
            {
                foeHp += ship.Hp + ship.Shield;
                foeDps += Dps(ship, balance);
            }
        }
        return foeHp * foeDps > npc.OutmatchRatio * ownHp * ownDps;
    }

    /// <summary>Урон в секунду всех пушек с учётом точности — без дистанции и сектора.</summary>
    private static double Dps(ShipEntity ship, Balance balance)
    {
        var dps = 0.0;
        foreach (var weapon in ship.Weapons(balance))
        {
            if (weapon.Cooldown > 0) dps += weapon.Damage * weapon.Accuracy / 100 / weapon.Cooldown;
        }
        return dps;
    }

    /// <summary>Укрытие у станции в этот тик.</summary>
    private readonly record struct Shelter(double X, double Y, double Radius)
    {
        public bool Contains(ShipEntity ship, double margin = 0) => Distance(ship.Ship.X, ship.Ship.Y, X, Y) <= Radius + margin;
    }

    private static bool IsCandidate(Pirate self, ShipEntity? ship, long tick, Shelter shelter) =>
        ship is not null && CanFight(self, ship, tick) && (self.HoldsGround || !shelter.Contains(ship));

    /// <summary>
    /// Кого атаковать: того, кто напал; иначе ближайшего, кого ищет (пират — в радиусе агро, рейнджер — в радиусе
    /// защиты торговцев); иначе цель собрата по фракции, который рядом в бою.
    /// </summary>
    private static ShipEntity? Acquire(
        Pirate pirate,
        int attackerId,
        IReadOnlyDictionary<int, ShipEntity> ships,
        IReadOnlyList<Pirate> pirates,
        Balance balance,
        Shelter shelter,
        long tick,
        IReadOnlyDictionary<int, long>? offenders,
        IReadOnlySet<int>? outlaws)
    {
        var npc = balance.Npc;
        if (ships.GetValueOrDefault(attackerId) is { } attacker && IsCandidate(pirate, attacker, tick, shelter) && Distance(pirate, attacker) <= npc.DropRange)
            return attacker;

        ShipEntity? nearest = null;
        var nearestDistance = Math.Max(npc.AggroRange, pirate.Type.DefendRange);
        foreach (var ship in ships.Values)
        {
            if (!IsCandidate(pirate, ship, tick, shelter) || !Wants(pirate, ship, tick, offenders, outlaws)) continue;
            if (IsOutmatched(pirate, ship, pirates, balance)) continue; // на сильную стаю сам не лезет
            var distance = Distance(pirate, ship);
            // Маскировка (M19) сужает круг именно пирату: рейнджер видит всех, и тот, кто уже выстрелил,
            // тоже найден — эта ветка выше, по attackerId и DropRange.
            var reach = pirate.Type.IsPirate ? nearestDistance * ship.Stealth(balance) : nearestDistance;
            if (distance > reach) continue;
            nearest = ship;
            nearestDistance = distance;
        }
        if (nearest is not null) return nearest;

        foreach (var other in pirates)
        {
            if (other == pirate || other.IsDead || other.State != PirateState.Attack || Distance(pirate, other) > npc.AssistRange) continue;
            if (other.Type.IsRanger != pirate.Type.IsRanger) continue; // помогают только своим
            if (ships.GetValueOrDefault(other.TargetId) is { } target && IsCandidate(pirate, target, tick, shelter) && Distance(pirate, target) <= npc.DropRange)
                return target;
        }
        return null;
    }

    /// <returns>Причина бросить бой и уйти в логово, или null — продолжать.</returns>
    /// <param name="onTheWay">Налётчик в пути: поводка от точки патруля нет, бой держит только DropRange.</param>
    private static string? ReturnReason(Pirate pirate, ShipEntity target, HullParams hull, NpcRules npc, Shelter shelter, bool onTheWay)
    {
        if (pirate.Hp <= pirate.MaxHp(hull) * pirate.RetreatHp) return "retreat";
        // Кто стоит на посту, тот стоит и у станции (M20b): его туда и поставили — охранять шлюз.
        if (!pirate.HoldsGround)
        {
            if (shelter.Contains(target)) return "target in the shelter";
            if (shelter.Contains(pirate, SafeMargin))
                return "too close to the shelter";
        }
        if (!pirate.HoldsGround && !onTheWay && Distance(pirate.Ship.X, pirate.Ship.Y, pirate.HomeX, pirate.HomeY) > (pirate.Type.LeashRange ?? npc.LeashRange)) return "too far from home";
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

        // Даже в бою звезда важнее цели: гоняться за пилотом сквозь жар — верная смерть.
        Set(pirate, dirX, dirY, throttle, balance.Sun?.BurnRadius ?? 0);
        pirate.FireHeld = true;
    }

    private static void Patrol(Pirate pirate, NpcRules npc, long tick, Random rng, double heat)
    {
        var s = pirate.Ship;
        if (!pirate.HasWaypoint || tick >= pirate.WaypointUntilTick || Distance(s.X, s.Y, pirate.WaypointX, pirate.WaypointY) <= ArriveRadius)
        {
            // Равномерно по кругу патруля, но не за границей мира.
            var radius = npc.PatrolRadius * Math.Sqrt(rng.NextDouble());
            var angle = rng.NextDouble() * 2 * Math.PI;
            var (wx, wy) = Heat.SafePoint(pirate.HomeX + radius * Math.Cos(angle), pirate.HomeY + radius * Math.Sin(angle), heat);
            pirate.WaypointX = Math.Clamp(wx, -NpcRules.WorldLimit, NpcRules.WorldLimit);
            pirate.WaypointY = Math.Clamp(wy, -NpcRules.WorldLimit, NpcRules.WorldLimit);
            pirate.WaypointUntilTick = tick + WaypointTicks;
            pirate.HasWaypoint = true;
        }
        pirate.TargetId = 0;
        // Рядом лежит груз — пират летит за ним (подбирает его комната, когда он подлетит).
        if (pirate.LootId != 0) FlyTo(pirate, pirate.LootX, pirate.LootY, npc.PatrolThrottle, heat);
        else FlyTo(pirate, pirate.WaypointX, pirate.WaypointY, npc.PatrolThrottle, heat);
    }

    /// <summary>Лететь к точке, сбавляя тягу на подлёте; огня нет. Звезду на пути — огибать (M16a).</summary>
    private static void FlyTo(Pirate pirate, double x, double y, double maxThrottle, double heat)
    {
        var (aimX, aimY) = Heat.Detour(pirate.Ship.X, pirate.Ship.Y, x, y, heat);
        var dx = aimX - pirate.Ship.X;
        var dy = aimY - pirate.Ship.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var throttle = Math.Min(maxThrottle, Math.Clamp(distance / SlowRadius, MinArriveThrottle, 1));
        Set(pirate, dx, dy, throttle, heat);
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

    /// <summary>
    /// Единственный выход ИИ наружу: сюда сходятся патруль, погоня, бой и уход. Здесь же курс огибает жар
    /// звезды (M15.7) — до этого пираты летели напрямик через центр системы и сгорали по дороге.
    /// </summary>
    /// <param name="heat">Радиус зоны жара звезды; 0 — звезды нет.</param>
    private static void Set(Pirate pirate, double dx, double dy, double throttle, double heat)
    {
        var speed = Math.Sqrt(pirate.Ship.Vx * pirate.Ship.Vx + pirate.Ship.Vy * pirate.Ship.Vy);
        var (x, y) = Heat.Avoid(pirate.Ship.X, pirate.Ship.Y, dx, dy, heat, speed);
        MoveInput.TryCreate(x, y, throttle, out var input);
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
