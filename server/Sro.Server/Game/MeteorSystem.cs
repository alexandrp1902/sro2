using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Метеориты системы: появление на трассе мимо укрытия, полёт по дуге, таран и минералы с расстрелянных.
/// Сами метеориты лежат ещё и в словаре кораблей комнаты — по ним стреляет обычный <see cref="Battle"/>;
/// добавляет и убирает их туда <see cref="Room"/>. Как и она, живёт только в потоке тика.
/// </summary>
/// <param name="nextId">Общий счётчик id комнаты.</param>
/// <param name="rng">Своя случайность: метеориты не сдвигают ни спаун, ни ИИ, ни дроп.</param>
internal sealed class MeteorSystem(Func<int> nextId, Random rng)
{
    private readonly List<Meteor> _alive = [];
    private readonly List<Meteor> _gone = [];
    private readonly List<MeteorDto> _dtos = [];

    public IReadOnlyList<Meteor> Alive => _alive;

    /// <summary>
    /// Полёт: тяготение центра системы гнёт курс, тяги и границы мира у камня нет — он прошивает систему насквозь.
    /// </summary>
    public void Move(MeteorRules rules)
    {
        foreach (var meteor in _alive)
        {
            if (meteor.IsDead) continue;
            rules.Step(ref meteor.Ship.X, ref meteor.Ship.Y, ref meteor.Ship.Vx, ref meteor.Ship.Vy, SimConfig.Dt);
        }
    }

    /// <summary>Улетевшие за мир и отжившие свой срок — исчезают молча, без обломков и без события.</summary>
    public IReadOnlyList<Meteor> Expired(MeteorRules rules, long tick)
    {
        _gone.Clear();
        var limit = Movement.WorldHalfSize + rules.DespawnMargin;
        foreach (var meteor in _alive)
        {
            if (meteor.IsDead) continue;
            var s = meteor.Ship;
            var outside = Math.Abs(s.X) > limit || Math.Abs(s.Y) > limit;
            // Только удаляющийся: на входе метеорит тоже снаружи мира, но летит внутрь.
            if ((outside && s.X * s.Vx + s.Y * s.Vy > 0) || tick >= meteor.ExpiresAtTick) _gone.Add(meteor);
        }
        foreach (var meteor in _gone) _alive.Remove(meteor);
        return _gone;
    }

    /// <summary>
    /// Выпускать ли камень в этом тике. Не расписание, а монета на каждый тик: интервал в файле — это среднее,
    /// а на деле камни идут неровно, как и положено небу. Пустая система их не копит к приходу первого игрока.
    /// </summary>
    public bool Due(MeteorRules rules, bool anyoneOnline) =>
        anyoneOnline && rules.Enabled && _alive.Count < rules.MaxAlive && rng.NextDouble() < rules.SpawnChancePerTick;

    /// <summary>
    /// Новый метеорит на краю мира. Прицеливаемся в случайную точку круга AimRadius — то есть в обитаемую часть, —
    /// а потом проигрываем всю дугу вперёд: тяготение уводит камень с прямой, поэтому только по настоящей трассе
    /// и видно, не заденет ли он укрытие. Укрытие остаётся чистым и от пиратов, и от камней.
    /// </summary>
    /// <returns>null — за SpawnAttempts попыток трасса мимо укрытия не нашлась, появление пропускается.</returns>
    public Meteor? Launch(MeteorRules rules, double stationSafeRadius, long tick)
    {
        if (rules.PickSize(rng.NextDouble()) is not { } sizeId) return null;
        var size = rules.SizeMap[sizeId];
        // Тип траектории решает, насколько близко к центру камень целится и как быстро идёт: от почти прямого
        // пролёта по краю до медленной дуги, которую тяготение загибает вокруг центра системы.
        var track = rules.Track(rules.PickTrack(rng.NextDouble()));
        // Вход за границей мира, но ближе черты исчезновения: камень вылетает из-за края, а не из пустоты.
        var entry = Movement.WorldHalfSize + rules.DespawnMargin * 0.8;

        for (var attempt = 0; attempt < MeteorRules.SpawnAttempts; attempt++)
        {
            var along = (2 * rng.NextDouble() - 1) * entry;
            var (x, y) = rng.Next(4) switch
            {
                0 => (along, -entry),
                1 => (entry, along),
                2 => (along, entry),
                _ => (-entry, along),
            };
            var aimRadius = rules.AimRadius * track.AimFactor * Math.Sqrt(rng.NextDouble());
            var aimAngle = rng.NextDouble() * 2 * Math.PI;
            var dx = SimConfig.StationX + aimRadius * Math.Cos(aimAngle) - x;
            var dy = SimConfig.StationY + aimRadius * Math.Sin(aimAngle) - y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) continue;
            dx /= length;
            dy /= length;
            var speed = (size.SpeedMin + (size.SpeedMax - size.SpeedMin) * rng.NextDouble()) * track.SpeedFactor;
            // Мимо укрытия, но всё-таки через обитаемую часть: тяготение могло и увести дугу по краю мира.
            var closest = Trace(rules, x, y, dx * speed, dy * speed);
            if (closest < stationSafeRadius + size.Radius || closest > rules.AimRadius) continue;
            return Add(sizeId, size, x, y, dx * speed, dy * speed, tick + rules.LifetimeTicks);
        }
        return null;
    }

    /// <summary>Метеорит в заданной точке — для тестов и отладки; обычный путь — <see cref="Launch"/>.</summary>
    public Meteor Add(string sizeId, MeteorSize size, double x, double y, double vx, double vy, long expiresAtTick)
    {
        var meteor = new Meteor(nextId(), sizeId, size, expiresAtTick)
        {
            Ship = new ShipState { X = x, Y = y, Vx = vx, Vy = vy },
        };
        _alive.Add(meteor);
        return meteor;
    }

    /// <summary>
    /// Проигрывает дугу до конца жизни камня тем же шагом и той же схемой, что и сама симуляция.
    /// </summary>
    /// <returns>Ближайший подход к центру системы за всю жизнь камня.</returns>
    public static double Trace(MeteorRules rules, double x, double y, double vx, double vy)
    {
        // Шаг — ровно тик симуляции: тогда отбраковка идёт по той самой дуге, по которой камень и полетит.
        const double step = SimConfig.Dt;
        // Дуга проверяется целиком, до выхода из мира: срок жизни — предохранитель от вечных орбит,
        // а не часть замысла трассы, и укладывать в него проверку значило бы мерить не то.
        const double maxSeconds = 240;
        var limit = Movement.WorldHalfSize + rules.DespawnMargin;
        var closest = double.MaxValue;
        for (var t = 0.0; t < maxSeconds; t += step)
        {
            rules.Step(ref x, ref y, ref vx, ref vy, step);
            closest = Math.Min(closest, Math.Sqrt(x * x + y * y));
            if ((Math.Abs(x) > limit || Math.Abs(y) > limit) && x * vx + y * vy > 0) break;
        }
        return closest;
    }

    /// <summary>
    /// Таран: метеорит бьёт ближайший корабль, с которым перекрылся, и разрушается. Защищённый после появления
    /// и корабль без связи не таранятся — первый обходил бы GDD §25, второй не может увернуться.
    /// Урон идёт через щит и уходит в общий свод смертей Battle, поэтому столкновения — до боя.
    /// </summary>
    public void Collide(IReadOnlyDictionary<int, ShipEntity> ships, Balance balance, long tick, List<ShotDto> shots)
    {
        foreach (var meteor in _alive)
        {
            if (meteor.IsDead || meteor.Hp <= 0) continue;
            ShipEntity? hit = null;
            var best = double.MaxValue;
            foreach (var ship in ships.Values)
            {
                if (!CanBeRammed(ship, tick)) continue;
                var dx = ship.Ship.X - meteor.Ship.X;
                var dy = ship.Ship.Y - meteor.Ship.Y;
                var distanceSq = dx * dx + dy * dy;
                var reach = meteor.Size.Radius + ship.Hull(balance.Hulls).Size;
                if (distanceSq > reach * reach || distanceSq >= best) continue;
                best = distanceSq;
                hit = ship;
            }
            if (hit is not null) Ram(meteor, hit, balance.Meteors, tick, shots);
        }
    }

    private static bool CanBeRammed(ShipEntity ship, long tick) =>
        ship is not Meteor && !ship.IsDead && !ship.IsProtected(tick) && ship is not Player { Connection: null };

    private static void Ram(Meteor meteor, ShipEntity ship, MeteorRules rules, long tick, List<ShotDto> shots)
    {
        // Скорость сближения вдоль линии центров: лоб в лоб — больше скорости камня, вдогонку — меньше.
        var dx = ship.Ship.X - meteor.Ship.X;
        var dy = ship.Ship.Y - meteor.Ship.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var closing = distance > 1e-9
            ? ((meteor.Ship.Vx - ship.Ship.Vx) * dx + (meteor.Ship.Vy - ship.Ship.Vy) * dy) / distance
            : meteor.Speed * rules.RamMaxFactor;
        var damage = meteor.Size.RamDamage * rules.RamFactor(closing, meteor.Speed);

        var dealt = Combat.ApplyDamage(ref ship.Hp, ref ship.Shield, damage);
        ship.LastDamageTick = tick;
        if (ship.Hp <= 0 && ship.KilledBy == 0) ship.KilledBy = meteor.Id;
        // Событие тарана — обычный выстрел с псевдо-пушкой: клиент бесплатно рисует цифры, вспышку щита и искры.
        shots.Add(new ShotDto(
            meteor.Id,
            ship.Id,
            MeteorRules.RamWeapon,
            true,
            (int)Math.Round(dealt.Shield + dealt.Hull),
            (int)Math.Round(dealt.Shield),
            100));

        meteor.Hp = 0;
        meteor.Rammed = true;
    }

    /// <summary>
    /// Уничтоженные в этом тике: расстрелянный роняет минералы по таблице размера, разбившийся о корабль — ничего.
    /// Возвращает их, чтобы комната убрала камни из словаря кораблей.
    /// </summary>
    public IReadOnlyList<Meteor> Shatter(LootSystem loot, LootRules lootRules, long tick)
    {
        _gone.Clear();
        foreach (var meteor in _alive)
        {
            if (!meteor.IsDead) continue;
            _gone.Add(meteor);
            if (meteor.Rammed || meteor.Size.Table is not { } tableId) continue;
            if (!lootRules.TableMap.TryGetValue(tableId, out var table)) continue;
            loot.DropAt(lootRules, table, 1, meteor.Ship.X, meteor.Ship.Y, meteor.DeathVx, meteor.DeathVy, tick);
        }
        foreach (var meteor in _gone) _alive.Remove(meteor);
        return _gone;
    }

    /// <returns>null — метеоритов нет, поле в снапшот не пишется.</returns>
    public IReadOnlyList<MeteorDto>? ToDtos()
    {
        if (_alive.Count == 0) return null;
        _dtos.Clear();
        foreach (var meteor in _alive)
        {
            if (meteor.IsDead) continue;
            var s = meteor.Ship;
            _dtos.Add(new MeteorDto(meteor.Id, s.X, s.Y, s.Vx, s.Vy, meteor.SizeId, (int)Math.Ceiling(meteor.Hp)));
        }
        return _dtos.Count > 0 ? _dtos : null;
    }
}
