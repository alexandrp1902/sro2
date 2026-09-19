using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Ракеты в полёте (боевой документ §37): каждая доворачивает к своей цели и бьёт при касании — без броска
/// на попадание, зато от неё можно уйти манёвром. Цель ушла из системы, в док или погибла — ракета гаснет.
/// </summary>
public sealed class MissileSystem(Func<int> newId)
{
    public sealed class Missile(int id, int ownerId, int targetId, string weaponId, WeaponParams weapon, MissileState state, long expiresAtTick)
    {
        public int Id { get; } = id;
        public int OwnerId { get; } = ownerId;
        public int TargetId { get; } = targetId;
        public string WeaponId { get; } = weaponId;
        /// <summary>Параметры на момент пуска — с уроном уровня у пирата; правка баланса летящую ракету не меняет.</summary>
        public WeaponParams Weapon { get; } = weapon;
        public MissileState State = state;
        public long ExpiresAtTick { get; } = expiresAtTick;
    }

    private readonly List<Missile> _alive = [];
    private readonly List<MissileDto> _dtos = [];

    public IReadOnlyList<Missile> Alive => _alive;

    /// <summary>Пуск от носа стрелка — туда же и смотрит ракета: к цели она доворачивает уже в полёте.</summary>
    public void Launch(ShipEntity shooter, ShipEntity target, int slot, WeaponParams weapon, long tick)
    {
        var p = weapon.Missile!;
        var state = new MissileState { X = shooter.Ship.X, Y = shooter.Ship.Y, Rot = shooter.Ship.Rot };
        _alive.Add(new Missile(newId(), shooter.Id, target.Id, shooter.WeaponIds[slot] ?? "", weapon, state, tick + p.LifetimeTicks));
    }

    /// <summary>Шаг: полёт, попадания, погасшие. Попадание — выстрел в общем списке: урон, цифра и лента те же, что у пушек.</summary>
    public void Step(long tick, IReadOnlyDictionary<int, ShipEntity> ships, Balance balance, List<ShotDto> shots)
    {
        for (var i = _alive.Count - 1; i >= 0; i--)
        {
            var missile = _alive[i];
            var p = missile.Weapon.Missile!;
            if (tick >= missile.ExpiresAtTick || !ships.TryGetValue(missile.TargetId, out var target) || target.IsDead)
            {
                _alive.RemoveAt(i);
                continue;
            }
            Missiles.Step(ref missile.State, p, target.Ship.X, target.Ship.Y, SimConfig.Dt);
            var radius = target is Meteor meteor ? meteor.Size.Radius : target.Hull(balance.Hulls).Size;
            if (!Missiles.Hits(missile.State, p, target.Ship.X, target.Ship.Y, radius)) continue;

            _alive.RemoveAt(i);
            var damage = Combat.ApplyDamage(ref target.Hp, ref target.Shield, missile.Weapon.Damage);
            target.LastDamageTick = tick;
            target.LastAttackerId = missile.OwnerId;
            if (target.Hp <= 0 && target.KilledBy == 0) target.KilledBy = missile.OwnerId;
            target.Stats.Record(tick, true, 100);
            shots.Add(new ShotDto(
                missile.OwnerId,
                target.Id,
                missile.WeaponId,
                true,
                (int)Math.Round(damage.Shield + damage.Hull),
                (int)Math.Round(damage.Shield),
                100));
        }
    }

    public IReadOnlyList<MissileDto> ToDtos()
    {
        _dtos.Clear();
        foreach (var m in _alive) _dtos.Add(new MissileDto(m.Id, m.State.X, m.State.Y, m.State.Rot, m.OwnerId, m.TargetId, m.WeaponId));
        return _dtos;
    }
}
