using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Бой за один тик (GDD §45): выстрелы, урон по щиту и корпусу, уничтожение, регенерация щита, респаун.
/// Выстрелы одновременные: стрелки выбираются по состоянию на начало тика, и только после всего урона
/// ищутся уничтоженные — порядок кораблей в словаре на исход не влияет, взаимное уничтожение возможно.
/// Каждый оружейный слот стреляет сам, со своей перезарядкой (GDD §12). Ракетница не бросает кубик,
/// а запускает ракету (боевой документ §37) — дальше её ведёт <see cref="MissileSystem"/>.
/// </summary>
internal sealed class Battle(Func<double> roll, ILogger log)
{
    private readonly record struct Volley(ShipEntity Shooter, ShipEntity Target, int Slot, WeaponParams Weapon, double Chance);

    private readonly List<Volley> _volleys = [];

    /// <param name="respawn">Возвращает уничтоженный корабль в систему, когда вышел его срок.</param>
    /// <param name="canAttack">Можно ли стрелку бить эту цель — PvP по правилам системы; null — можно всех.</param>
    /// <param name="launch">Запуск ракеты: стрелок, цель, слот, ракетница; null — ракетницы молчат.</param>
    public void Run(
        long tick,
        Dictionary<int, ShipEntity> ships,
        Balance balance,
        List<ShotDto> shots,
        List<KillDto> kills,
        Action<ShipEntity> respawn,
        Func<ShipEntity, ShipEntity, bool>? canAttack = null,
        Action<ShipEntity, ShipEntity, int, WeaponParams>? launch = null)
    {
        foreach (var shooter in ships.Values) Aim(tick, shooter, ships, balance, canAttack, launch is not null);
        foreach (var volley in _volleys)
        {
            if (volley.Weapon.Missile is not null) Launch(tick, volley, launch!);
            else Fire(tick, volley, shots);
        }
        _volleys.Clear();

        var rules = balance.Rules;
        foreach (var ship in ships.Values)
        {
            if (ship.IsDead || ship.Hp > 0) continue;
            ship.DeadUntilTick = tick + ship.RespawnTicks(balance);
            // Обломки разлетаются по инерции убитого, поэтому её надо запомнить до обнуления.
            ship.DeathVx = ship.Ship.Vx;
            ship.DeathVy = ship.Ship.Vy;
            ship.Ship.Vx = 0;
            ship.Ship.Vy = 0;
            ship.FireHeld = false;
            kills.Add(new KillDto(ship.Id, ship.KilledBy));
            if (ship is not Meteor) LogKill(tick, ship, ships); // TTK камней плейтесту не нужен, а лог забил бы
        }

        foreach (var ship in ships.Values)
        {
            if (ship.IsDead)
            {
                if (tick >= ship.DeadUntilTick) respawn(ship);
            }
            else
            {
                RegenerateShield(tick, ship, ship.Effective(balance), rules);
            }
        }
    }

    /// <summary>
    /// Проверки перед выстрелом (§45) для каждого слота: перезарядка, цель, дальность, сектор (боевой документ §35).
    /// Готовые слоты — в залп этого тика.
    /// </summary>
    private void Aim(
        long tick,
        ShipEntity shooter,
        Dictionary<int, ShipEntity> ships,
        Balance balance,
        Func<ShipEntity, ShipEntity, bool>? canAttack,
        bool missiles)
    {
        if (shooter.IsDead || !shooter.FireHeld) return;
        if (!ships.TryGetValue(shooter.TargetId, out var target) || target == shooter) return;
        if (target.IsDead || target.IsProtected(tick)) return;

        var dx = target.Ship.X - shooter.Ship.X;
        var dy = target.Ship.Y - shooter.Ship.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var speed = Math.Sqrt(target.Ship.Vx * target.Ship.Vx + target.Ship.Vy * target.Ship.Vy);
        var allowed = (bool?)null;
        var slots = Math.Min(shooter.WeaponIds.Count, Fitting.MaxWeaponSlots);
        for (var slot = 0; slot < slots; slot++)
        {
            if (tick < shooter.NextFireTicks[slot] || shooter.WeaponAt(balance, slot) is not { } weapon) continue;
            if (weapon.Missile is not null && !missiles) continue;
            if (!Combat.InRange(weapon, distance) || !Combat.InArc(shooter.Ship.Rot, dx, dy, weapon.Arc)) continue;
            allowed ??= canAttack is null || canAttack(shooter, target);
            if (allowed == false) return;
            var chance = weapon.Missile is null ? Combat.HitChance(weapon, distance, target.Evasion(balance, speed)) : 100;
            _volleys.Add(new Volley(shooter, target, slot, weapon, chance));
        }
    }

    /// <summary>Ракета ушла: перезарядка, защита снята, цель знает о нападении — урон будет, когда ракета долетит.</summary>
    private static void Launch(long tick, Volley volley, Action<ShipEntity, ShipEntity, int, WeaponParams> launch)
    {
        var (shooter, target, slot, weapon, _) = volley;
        shooter.NextFireTicks[slot] = tick + Combat.CooldownTicks(weapon);
        shooter.ProtectedUntilTick = 0;
        target.LastAttackerId = shooter.Id;
        launch(shooter, target, slot, weapon);
    }

    private void Fire(long tick, Volley volley, List<ShotDto> shots)
    {
        var (shooter, target, slot, weapon, chance) = volley;
        shooter.NextFireTicks[slot] = tick + Combat.CooldownTicks(weapon);
        shooter.ProtectedUntilTick = 0; // выстрел снимает защиту после появления (GDD §25)
        target.LastAttackerId = shooter.Id; // и промах — нападение: пират ответит

        var hit = Combat.IsHit(chance, roll());
        var damage = default(DamageResult);
        if (hit)
        {
            damage = Combat.ApplyDamage(ref target.Hp, ref target.Shield, weapon.Damage);
            target.LastDamageTick = tick;
            if (target.Hp <= 0 && target.KilledBy == 0) target.KilledBy = shooter.Id;
        }
        target.Stats.Record(tick, hit, chance);
        shots.Add(new ShotDto(
            shooter.Id,
            target.Id,
            shooter.WeaponIds[slot] ?? "",
            hit,
            (int)Math.Round(damage.Shield + damage.Hull),
            (int)Math.Round(damage.Shield),
            Math.Round(chance, 2)));
    }

    private static void RegenerateShield(long tick, ShipEntity ship, HullParams hull, CombatRules rules)
    {
        var max = ship.MaxShield(hull);
        if (ship.Shield >= max || tick - ship.LastDamageTick < rules.ShieldRegenDelayTicks) return;
        ship.Shield = Math.Min(max, ship.Shield + hull.ShieldRegen * SimConfig.Dt);
    }

    /// <summary>Цифры для таблицы TTK в плейтесте: время от первого попадания, выстрелы, фактический и ожидаемый шанс.</summary>
    private void LogKill(long tick, ShipEntity victim, Dictionary<int, ShipEntity> ships)
    {
        var stats = victim.Stats;
        var killer = ships.GetValueOrDefault(victim.KilledBy);
        var ttk = stats.FirstHitTick is { } first ? (tick - first) * SimConfig.Dt : 0;
        log.LogInformation(
            "{Victim} ({Hull}) destroyed by {Killer} ({Weapon}): TTK {Ttk:0.0} s, shots {Shots}, hits {Hits} ({Rate:0}%), expected {Expected:0}%",
            victim.Name,
            victim.HullId,
            killer?.Name ?? "?",
            killer is null ? "?" : string.Join('+', killer.WeaponIds.Where(w => w is not null)),
            ttk,
            stats.Shots,
            stats.Hits,
            stats.Shots > 0 ? 100.0 * stats.Hits / stats.Shots : 0,
            stats.Shots > 0 ? stats.ChanceSum / stats.Shots : 0);
    }
}
