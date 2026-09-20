using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Урон по площади (M15.5): попадание площадного оружия рвётся в точке цели и задевает соседей.
/// Снаряд по дороге никого не трогает — взрыв только там, где он попал, и только если попал.
/// Сама цель осколков не получает: по ней уже прошёл прямой урон, и складывать одно с другим
/// значило бы усилить площадное оружие против одиночек, чего оно делать не должно.
/// </summary>
internal static class Blast
{
    /// <param name="target">Эпицентр — цель прямого попадания.</param>
    /// <param name="canSplash">Кого задевает; строже <c>Room.CanAttack</c>, мирных не пускает вовсе.</param>
    public static void Apply(
        long tick,
        ShipEntity shooter,
        ShipEntity target,
        WeaponParams weapon,
        IReadOnlyDictionary<int, ShipEntity> ships,
        Balance balance,
        List<ShotDto> shots,
        Func<ShipEntity, ShipEntity, bool>? canSplash)
    {
        if (!(weapon.BlastRadius > 0) || !(weapon.BlastShare > 0)) return;
        var (x, y) = (target.Ship.X, target.Ship.Y);

        foreach (var ship in ships.Values)
        {
            if (ReferenceEquals(ship, target) || ReferenceEquals(ship, shooter)) continue;
            if (ship.IsDead || ship.IsProtected(tick)) continue;
            if (canSplash is not null && !canSplash(shooter, ship)) continue;

            var dx = ship.Ship.X - x;
            var dy = ship.Ship.Y - y;
            // До брони, а не до центра: крупный корпус ловит осколки бортом.
            var radius = ship is Meteor meteor ? meteor.Size.Radius : ship.Hull(balance.Hulls).Size;
            var distance = Math.Sqrt(dx * dx + dy * dy) - radius;
            var raw = Combat.Splash(weapon, distance);
            if (!(raw > 0)) continue;

            var damage = Combat.ApplyDamage(ref ship.Hp, ref ship.Shield, raw, weapon.ShieldFactor, weapon.HullFactor);
            var total = (int)Math.Round(damage.Shield + damage.Hull);
            if (total <= 0) continue;

            ship.LastDamageTick = tick; // обстрел глушит регенерацию щита и ремонтный блок — как от прямого попадания
            if (ship.Hp <= 0 && ship.KilledBy == 0) ship.KilledBy = shooter.Id; // голова, добыча и зачёт задания — стрелку

            // Осколки намеренно НЕ делают стрелка обидчиком: LastAttackerId не трогаем.
            // Через это поле идут Room.Offend (репутация за обстрел торговца и рейнджера), вызов рейнджеров
            // и ответный огонь торговца. Попал в пирата рядом с торговцем — торговец цел и никого не зовёт.
            // По той же причине не пишем Stats.Record (кубик не бросался) и не зовём SlowDown
            // (замедление ионки — прицельный эффект, у площадного оружия его нет).
            shots.Add(new ShotDto(
                shooter.Id,
                ship.Id,
                Combat.SplashWeapon,
                true,
                total,
                (int)Math.Round(damage.Shield),
                100));
        }
    }
}
