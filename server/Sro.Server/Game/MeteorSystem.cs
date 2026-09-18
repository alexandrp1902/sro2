using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Метеориты системы: появление на трассе мимо укрытия, полёт по прямой, таран и минералы с расстрелянных.
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
    /// <summary>0 — отсчёт не начат: первый метеорит прилетает через интервал после входа игрока, а не сразу.</summary>
    private long _nextSpawnTick;

    public IReadOnlyList<Meteor> Alive => _alive;

    /// <summary>Полёт по прямой без тяги и без границы мира: камень прошивает систему насквозь.</summary>
    public void Move()
    {
        foreach (var meteor in _alive)
        {
            if (meteor.IsDead) continue;
            meteor.Ship.X += meteor.Ship.Vx * SimConfig.Dt;
            meteor.Ship.Y += meteor.Ship.Vy * SimConfig.Dt;
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

    /// <summary>Пора ли выпускать новый метеорит. Пустая система не копит камни к приходу первого игрока.</summary>
    public bool Due(MeteorRules rules, long tick, bool anyoneOnline)
    {
        if (!anyoneOnline || !rules.Enabled)
        {
            _nextSpawnTick = 0;
            return false;
        }
        if (_nextSpawnTick == 0) _nextSpawnTick = tick + NextInterval(rules);
        if (tick < _nextSpawnTick) return false;
        _nextSpawnTick = tick + NextInterval(rules);
        return _alive.Count < rules.MaxAlive;
    }

    private long NextInterval(MeteorRules rules) =>
        Math.Max(1, (long)Math.Round(rules.SpawnIntervalTicks * (1 + rules.SpawnJitter * (2 * rng.NextDouble() - 1))));

    /// <summary>
    /// Новый метеорит на краю мира. Трасса идёт через случайную точку круга AimRadius — то есть через обитаемую
    /// часть, — но никогда через укрытие у станции: там безопасно и от пиратов, и от камней.
    /// </summary>
    /// <returns>null — за SpawnAttempts попыток трасса мимо укрытия не нашлась, появление пропускается.</returns>
    public Meteor? Launch(MeteorRules rules, double stationSafeRadius, long tick)
    {
        if (rules.PickSize(rng.NextDouble()) is not { } sizeId) return null;
        var size = rules.SizeMap[sizeId];
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
            var aimRadius = rules.AimRadius * Math.Sqrt(rng.NextDouble());
            var aimAngle = rng.NextDouble() * 2 * Math.PI;
            var dx = SimConfig.StationX + aimRadius * Math.Cos(aimAngle) - x;
            var dy = SimConfig.StationY + aimRadius * Math.Sin(aimAngle) - y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) continue;
            dx /= length;
            dy /= length;
            if (PassDistance(x, y, dx, dy, SimConfig.StationX, SimConfig.StationY) < stationSafeRadius + size.Radius) continue;

            var speed = size.SpeedMin + (size.SpeedMax - size.SpeedMin) * rng.NextDouble();
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

    /// <summary>Как близко к точке (px, py) пройдёт луч из (x, y) по единичному направлению (dx, dy).</summary>
    public static double PassDistance(double x, double y, double dx, double dy, double px, double py)
    {
        var t = Math.Max(0, (px - x) * dx + (py - y) * dy);
        var cx = x + dx * t - px;
        var cy = y + dy * t - py;
        return Math.Sqrt(cx * cx + cy * cy);
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
