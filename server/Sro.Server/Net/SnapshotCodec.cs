using System.Buffers;
using System.Numerics;
using MessagePack;

namespace Sro.Server.Net;

/// <summary>
/// Бинарный снапшот (M7): MessagePack-массив вместо JSON, только то, что видит радар игрока, и только изменившиеся поля.
/// Кадр: <c>[1, tick, key, ships, goneShips, loot, goneLoot, meteors, goneMeteors, shots, kills, picks, missiles, goneMissiles]</c>.
/// Сущность — <c>[id, mask, …поля, чьи биты стоят в mask, по порядку битов]</c>; новая сущность и ключевой кадр — все поля.
/// Координаты и скорости чужих — float32, своего корабля — float64: иначе сверка предсказания видела бы шум округления.
/// Зеркало: client/src/net/snapshotCodec.ts, общий эталон — shared/test-vectors/snapshot.json.
/// </summary>
public static class SnapshotCodec
{
    public const int FrameType = 1;

    /// <summary>Ключевой кадр не реже раза в столько тиков — страховка, если клиент и сервер всё же разошлись.</summary>
    public const int KeyframeTicks = 5 * Sim.SimConfig.TickRate;

    // Поля корабля по битам маски.
    public const int ShipX = 0, ShipY = 1, ShipR = 2, ShipVx = 3, ShipVy = 4, ShipHull = 5, ShipTh = 6, ShipAck = 7,
        ShipHp = 8, ShipSh = 9, ShipW = 10, ShipRt = 11, ShipPu = 12, ShipTg = 13, ShipAi = 14, ShipJ = 15, ShipSl = 16;
    public const int ShipFields = 17;

    public const int LootX = 0, LootY = 1, LootI = 2, LootN = 3, LootE = 4, LootC = 5;
    public const int LootFields = 6;

    public const int MeteorX = 0, MeteorY = 1, MeteorVx = 2, MeteorVy = 3, MeteorS = 4, MeteorHp = 5;
    public const int MeteorFields = 6;

    public const int MissileX = 0, MissileY = 1, MissileR = 2, MissileO = 3, MissileT = 4, MissileW = 5;
    public const int MissileFields = 6;

    /// <summary>Элементов в кадре; клиент прошлой версии без ракет читал 12.</summary>
    public const int FrameLength = 14;

    /// <summary>Всё, что сущности этого тика и события, до отсечения радаром.</summary>
    public readonly record struct World(
        long Tick,
        IReadOnlyList<ShipDto> Ships,
        IReadOnlyList<LootDto>? Loot,
        IReadOnlyList<MeteorDto>? Meteors,
        IReadOnlyList<ShotDto> Shots,
        IReadOnlyList<KillDto> Kills,
        IReadOnlyList<PickDto> Picks,
        IReadOnlyList<MissileDto>? Missiles = null);

    /// <summary>
    /// Что уже знает один клиент — от этого считаются дельты. Живёт у игрока; сбрасывается при входе в систему,
    /// а если кадр не ушёл (очередь соединения полна) — следующий кадр будет ключевым.
    /// </summary>
    public sealed class Encoder
    {
        private readonly Dictionary<int, ShipDto> _ships = [];
        private readonly Dictionary<int, LootDto> _loot = [];
        private readonly Dictionary<int, MeteorDto> _meteors = [];
        private readonly Dictionary<int, MissileDto> _missiles = [];
        private readonly HashSet<int> _seen = [];
        private readonly HashSet<int> _gone = [];
        private readonly List<ShotDto> _shots = [];
        private readonly ArrayBufferWriter<byte> _buffer = new(4096);
        private long _nextKeyTick = long.MinValue;

        /// <summary>Следующий кадр — ключевой: клиент начинает с чистого листа.</summary>
        public void Reset() => _nextKeyTick = long.MinValue;

        /// <param name="self">Свой корабль: всегда целиком и в float64.</param>
        /// <param name="visible">Видит ли игрок точку (x, y) — радар.</param>
        public byte[] Encode(World world, int self, Func<double, double, bool> visible)
        {
            var key = world.Tick >= _nextKeyTick;
            if (key)
            {
                _ships.Clear();
                _loot.Clear();
                _meteors.Clear();
                _missiles.Clear();
                // Разнос по тикам: ключевые кадры разных игроков не совпадают и не дают общий всплеск трафика.
                _nextKeyTick = world.Tick + KeyframeTicks;
            }

            _buffer.Clear();
            var w = new MessagePackWriter(_buffer);
            w.WriteArrayHeader(FrameLength);
            w.Write(FrameType);
            w.Write(world.Tick);
            w.Write(key);

            _seen.Clear();
            // Кто виден сейчас или был виден в прошлом кадре — события о них игроку нужны (выстрел по уходящему, его гибель).
            foreach (var id in _ships.Keys) _seen.Add(id);
            foreach (var id in _loot.Keys) _seen.Add(id);
            foreach (var id in _meteors.Keys) _seen.Add(id);

            WriteShips(ref w, world.Ships, self, visible);
            WriteLoot(ref w, world.Loot, visible);
            WriteMeteors(ref w, world.Meteors, visible);
            foreach (var id in _ships.Keys) _seen.Add(id);
            foreach (var id in _loot.Keys) _seen.Add(id);
            foreach (var id in _meteors.Keys) _seen.Add(id);
            _seen.Add(self);

            WriteShots(ref w, world.Shots);
            WriteKills(ref w, world.Kills);
            WritePicks(ref w, world.Picks);
            WriteMissiles(ref w, world.Missiles, visible);
            w.Flush();
            return _buffer.WrittenSpan.ToArray();
        }

        private void WriteShips(ref MessagePackWriter w, IReadOnlyList<ShipDto> ships, int self, Func<double, double, bool> visible)
        {
            var count = 0;
            foreach (var ship in ships) if (ship.Id == self || visible(ship.X, ship.Y)) count++;
            w.WriteArrayHeader(count);
            _gone.Clear();
            _gone.UnionWith(_ships.Keys);
            foreach (var ship in ships)
            {
                if (ship.Id != self && !visible(ship.X, ship.Y)) continue;
                _gone.Remove(ship.Id);
                var own = ship.Id == self;
                var mask = own || !_ships.TryGetValue(ship.Id, out var was) ? (1 << ShipFields) - 1 : ShipMask(was, ship);
                _ships[ship.Id] = ship;
                w.WriteArrayHeader(2 + BitOperations.PopCount((uint)mask));
                w.Write(ship.Id);
                w.Write(mask);
                if (Has(mask, ShipX)) WriteCoord(ref w, ship.X, own);
                if (Has(mask, ShipY)) WriteCoord(ref w, ship.Y, own);
                if (Has(mask, ShipR)) WriteCoord(ref w, ship.R, own);
                if (Has(mask, ShipVx)) WriteCoord(ref w, ship.Vx, own);
                if (Has(mask, ShipVy)) WriteCoord(ref w, ship.Vy, own);
                if (Has(mask, ShipHull)) w.Write(ship.Hull);
                if (Has(mask, ShipTh)) w.Write((float)ship.Th);
                if (Has(mask, ShipAck)) w.Write(ship.Ack);
                if (Has(mask, ShipHp)) w.Write(ship.Hp);
                if (Has(mask, ShipSh)) w.Write(ship.Sh);
                if (Has(mask, ShipW)) w.Write(ship.W);
                if (Has(mask, ShipRt)) w.Write(ship.Rt);
                if (Has(mask, ShipPu)) w.Write(ship.Pu);
                if (Has(mask, ShipTg)) w.Write(ship.Tg);
                if (Has(mask, ShipAi)) WriteString(ref w, ship.Ai);
                if (Has(mask, ShipJ)) w.Write(ship.J);
                if (Has(mask, ShipSl)) w.Write(ship.Sl);
            }
            WriteGone(ref w, _ships);
        }

        private void WriteLoot(ref MessagePackWriter w, IReadOnlyList<LootDto>? loot, Func<double, double, bool> visible)
        {
            var count = 0;
            if (loot is not null) foreach (var item in loot) if (visible(item.X, item.Y)) count++;
            w.WriteArrayHeader(count);
            _gone.Clear();
            _gone.UnionWith(_loot.Keys);
            if (loot is not null)
            {
                foreach (var item in loot)
                {
                    if (!visible(item.X, item.Y)) continue;
                    _gone.Remove(item.Id);
                    var mask = _loot.TryGetValue(item.Id, out var was) ? LootMask(was, item) : (1 << LootFields) - 1;
                    _loot[item.Id] = item;
                    w.WriteArrayHeader(2 + BitOperations.PopCount((uint)mask));
                    w.Write(item.Id);
                    w.Write(mask);
                    if (Has(mask, LootX)) w.Write((float)item.X);
                    if (Has(mask, LootY)) w.Write((float)item.Y);
                    if (Has(mask, LootI)) w.Write(item.I);
                    if (Has(mask, LootN)) w.Write(item.N);
                    if (Has(mask, LootE)) w.Write(item.E);
                    if (Has(mask, LootC)) w.Write(item.C);
                }
            }
            WriteGone(ref w, _loot);
        }

        private void WriteMeteors(ref MessagePackWriter w, IReadOnlyList<MeteorDto>? meteors, Func<double, double, bool> visible)
        {
            var count = 0;
            if (meteors is not null) foreach (var meteor in meteors) if (visible(meteor.X, meteor.Y)) count++;
            w.WriteArrayHeader(count);
            _gone.Clear();
            _gone.UnionWith(_meteors.Keys);
            if (meteors is not null)
            {
                foreach (var meteor in meteors)
                {
                    if (!visible(meteor.X, meteor.Y)) continue;
                    _gone.Remove(meteor.Id);
                    var mask = _meteors.TryGetValue(meteor.Id, out var was) ? MeteorMask(was, meteor) : (1 << MeteorFields) - 1;
                    _meteors[meteor.Id] = meteor;
                    w.WriteArrayHeader(2 + BitOperations.PopCount((uint)mask));
                    w.Write(meteor.Id);
                    w.Write(mask);
                    if (Has(mask, MeteorX)) w.Write((float)meteor.X);
                    if (Has(mask, MeteorY)) w.Write((float)meteor.Y);
                    if (Has(mask, MeteorVx)) w.Write((float)meteor.Vx);
                    if (Has(mask, MeteorVy)) w.Write((float)meteor.Vy);
                    if (Has(mask, MeteorS)) w.Write(meteor.S);
                    if (Has(mask, MeteorHp)) w.Write(meteor.Hp);
                }
            }
            WriteGone(ref w, _meteors);
        }

        private void WriteMissiles(ref MessagePackWriter w, IReadOnlyList<MissileDto>? missiles, Func<double, double, bool> visible)
        {
            var count = 0;
            if (missiles is not null) foreach (var missile in missiles) if (visible(missile.X, missile.Y)) count++;
            w.WriteArrayHeader(count);
            _gone.Clear();
            _gone.UnionWith(_missiles.Keys);
            if (missiles is not null)
            {
                foreach (var missile in missiles)
                {
                    if (!visible(missile.X, missile.Y)) continue;
                    _gone.Remove(missile.Id);
                    var mask = _missiles.TryGetValue(missile.Id, out var was) ? MissileMask(was, missile) : (1 << MissileFields) - 1;
                    _missiles[missile.Id] = missile;
                    w.WriteArrayHeader(2 + BitOperations.PopCount((uint)mask));
                    w.Write(missile.Id);
                    w.Write(mask);
                    if (Has(mask, MissileX)) w.Write((float)missile.X);
                    if (Has(mask, MissileY)) w.Write((float)missile.Y);
                    if (Has(mask, MissileR)) w.Write((float)missile.R);
                    if (Has(mask, MissileO)) w.Write(missile.O);
                    if (Has(mask, MissileT)) w.Write(missile.T);
                    if (Has(mask, MissileW)) w.Write(missile.W);
                }
            }
            WriteGone(ref w, _missiles);
        }

        /// <summary>Ушедшие из радара или из системы: список id, и клиент их забывает.</summary>
        private void WriteGone<T>(ref MessagePackWriter w, Dictionary<int, T> known)
        {
            w.WriteArrayHeader(_gone.Count);
            foreach (var id in _gone)
            {
                w.Write(id);
                known.Remove(id);
            }
        }

        private void WriteShots(ref MessagePackWriter w, IReadOnlyList<ShotDto> shots)
        {
            _shots.Clear();
            foreach (var shot in shots)
            {
                if (_seen.Contains(shot.From) || _seen.Contains(shot.To)) _shots.Add(shot);
            }
            if (_shots.Count == 0)
            {
                w.WriteNil();
                return;
            }
            w.WriteArrayHeader(_shots.Count);
            foreach (var shot in _shots)
            {
                w.WriteArrayHeader(8);
                w.Write(shot.From);
                w.Write(shot.To);
                w.Write(shot.W);
                w.Write(shot.Hit);
                w.Write(shot.Dmg);
                w.Write(shot.Sh);
                w.Write(shot.Ch);
                w.Write(shot.Blk);
            }
            // Второй участник видимого выстрела — тоже в деле: камень, разбившийся о свой корабль в тот же тик,
            // когда появился, в радаре не бывал, но его гибель игроку нужна.
            foreach (var shot in _shots)
            {
                _seen.Add(shot.From);
                _seen.Add(shot.To);
            }
        }

        private void WriteKills(ref MessagePackWriter w, IReadOnlyList<KillDto> kills)
        {
            var count = 0;
            foreach (var kill in kills) if (_seen.Contains(kill.Id) || _seen.Contains(kill.By)) count++;
            if (count == 0)
            {
                w.WriteNil();
                return;
            }
            w.WriteArrayHeader(count);
            foreach (var kill in kills)
            {
                if (!_seen.Contains(kill.Id) && !_seen.Contains(kill.By)) continue;
                w.WriteArrayHeader(2);
                w.Write(kill.Id);
                w.Write(kill.By);
            }
        }

        private void WritePicks(ref MessagePackWriter w, IReadOnlyList<PickDto> picks)
        {
            var count = 0;
            foreach (var pick in picks) if (_seen.Contains(pick.By) || _seen.Contains(pick.Id)) count++;
            if (count == 0)
            {
                w.WriteNil();
                return;
            }
            w.WriteArrayHeader(count);
            foreach (var pick in picks)
            {
                if (!_seen.Contains(pick.By) && !_seen.Contains(pick.Id)) continue;
                w.WriteArrayHeader(4);
                w.Write(pick.By);
                w.Write(pick.Id);
                w.Write(pick.I);
                w.Write(pick.N);
            }
        }
    }

    private static bool Has(int mask, int bit) => (mask & (1 << bit)) != 0;

    private static int Bit(bool changed, int bit) => changed ? 1 << bit : 0;

    /// <summary>float32 на проводе: изменение, которое в нём не видно, — не изменение.</summary>
    private static bool Differs(double a, double b) => (float)a != (float)b;

    private static int ShipMask(ShipDto a, ShipDto b) =>
        Bit(Differs(a.X, b.X), ShipX) | Bit(Differs(a.Y, b.Y), ShipY) | Bit(Differs(a.R, b.R), ShipR) |
        Bit(Differs(a.Vx, b.Vx), ShipVx) | Bit(Differs(a.Vy, b.Vy), ShipVy) | Bit(a.Hull != b.Hull, ShipHull) |
        Bit(Differs(a.Th, b.Th), ShipTh) | Bit(a.Ack != b.Ack, ShipAck) | Bit(a.Hp != b.Hp, ShipHp) | Bit(a.Sh != b.Sh, ShipSh) |
        Bit(a.W != b.W, ShipW) | Bit(a.Rt != b.Rt, ShipRt) | Bit(a.Pu != b.Pu, ShipPu) | Bit(a.Tg != b.Tg, ShipTg) |
        Bit(a.Ai != b.Ai, ShipAi) | Bit(a.J != b.J, ShipJ) | Bit(a.Sl != b.Sl, ShipSl);

    private static int LootMask(LootDto a, LootDto b) =>
        Bit(Differs(a.X, b.X), LootX) | Bit(Differs(a.Y, b.Y), LootY) | Bit(a.I != b.I, LootI) |
        Bit(a.N != b.N, LootN) | Bit(a.E != b.E, LootE) | Bit(a.C != b.C, LootC);

    private static int MeteorMask(MeteorDto a, MeteorDto b) =>
        Bit(Differs(a.X, b.X), MeteorX) | Bit(Differs(a.Y, b.Y), MeteorY) | Bit(Differs(a.Vx, b.Vx), MeteorVx) |
        Bit(Differs(a.Vy, b.Vy), MeteorVy) | Bit(a.S != b.S, MeteorS) | Bit(a.Hp != b.Hp, MeteorHp);

    private static int MissileMask(MissileDto a, MissileDto b) =>
        Bit(Differs(a.X, b.X), MissileX) | Bit(Differs(a.Y, b.Y), MissileY) | Bit(Differs(a.R, b.R), MissileR) |
        Bit(a.O != b.O, MissileO) | Bit(a.T != b.T, MissileT) | Bit(a.W != b.W, MissileW);

    private static void WriteCoord(ref MessagePackWriter w, double value, bool precise)
    {
        if (precise) w.Write(value);
        else w.Write((float)value);
    }

    private static void WriteString(ref MessagePackWriter w, string? value)
    {
        if (value is null) w.WriteNil();
        else w.Write(value);
    }

    /// <summary>
    /// Собирает из кадров полный <see cref="SnapshotMsg"/> — как клиент. Нужен тестам, нагрузочным ботам и эталону.
    /// </summary>
    public sealed class Decoder
    {
        private readonly Dictionary<int, ShipDto> _ships = [];
        private readonly Dictionary<int, LootDto> _loot = [];
        private readonly Dictionary<int, MeteorDto> _meteors = [];
        private readonly Dictionary<int, MissileDto> _missiles = [];

        /// <summary>Пришёл кадр-дельта раньше первого ключевого: клиент и сервер разошлись.</summary>
        public int Desyncs { get; private set; }

        public void Reset()
        {
            _ships.Clear();
            _loot.Clear();
            _meteors.Clear();
            _missiles.Clear();
        }

        public SnapshotMsg Decode(ReadOnlyMemory<byte> frame)
        {
            var r = new MessagePackReader(frame);
            var length = r.ReadArrayHeader();
            if (length < 12 || r.ReadInt32() != FrameType) throw new FormatException("not a snapshot frame");
            var tick = r.ReadInt64();
            if (r.ReadBoolean()) Reset();

            var n = r.ReadArrayHeader();
            for (var i = 0; i < n; i++) ReadShip(ref r);
            foreach (var id in ReadIds(ref r)) _ships.Remove(id);
            n = r.ReadArrayHeader();
            for (var i = 0; i < n; i++) ReadLoot(ref r);
            foreach (var id in ReadIds(ref r)) _loot.Remove(id);
            n = r.ReadArrayHeader();
            for (var i = 0; i < n; i++) ReadMeteor(ref r);
            foreach (var id in ReadIds(ref r)) _meteors.Remove(id);

            List<ShotDto>? shots = null;
            if (!r.TryReadNil())
            {
                n = r.ReadArrayHeader();
                shots = new List<ShotDto>(n);
                for (var i = 0; i < n; i++)
                {
                    r.ReadArrayHeader();
                    shots.Add(new ShotDto(
                        r.ReadInt32(), r.ReadInt32(), r.ReadString()!, r.ReadBoolean(),
                        r.ReadInt32(), r.ReadInt32(), r.ReadDouble(), r.ReadBoolean()));
                }
            }
            List<KillDto>? kills = null;
            if (!r.TryReadNil())
            {
                n = r.ReadArrayHeader();
                kills = new List<KillDto>(n);
                for (var i = 0; i < n; i++)
                {
                    r.ReadArrayHeader();
                    kills.Add(new KillDto(r.ReadInt32(), r.ReadInt32()));
                }
            }
            List<PickDto>? picks = null;
            if (!r.TryReadNil())
            {
                n = r.ReadArrayHeader();
                picks = new List<PickDto>(n);
                for (var i = 0; i < n; i++)
                {
                    r.ReadArrayHeader();
                    picks.Add(new PickDto(r.ReadInt32(), r.ReadInt32(), r.ReadString()!, r.ReadInt32()));
                }
            }
            if (length >= FrameLength)
            {
                n = r.ReadArrayHeader();
                for (var i = 0; i < n; i++) ReadMissile(ref r);
                foreach (var id in ReadIds(ref r)) _missiles.Remove(id);
            }

            return new SnapshotMsg(
                tick,
                [.. _ships.Values],
                shots,
                kills,
                _loot.Count > 0 ? [.. _loot.Values] : null,
                picks,
                _meteors.Count > 0 ? [.. _meteors.Values] : null,
                _missiles.Count > 0 ? [.. _missiles.Values] : null);
        }

        private void ReadMissile(ref MessagePackReader r)
        {
            r.ReadArrayHeader();
            var id = r.ReadInt32();
            var mask = r.ReadInt32();
            if (!_missiles.TryGetValue(id, out var m))
            {
                if (mask != (1 << MissileFields) - 1) Desyncs++;
                m = new MissileDto(id, 0, 0, 0, 0, 0, "");
            }
            if (Has(mask, MissileX)) m = m with { X = r.ReadDouble() };
            if (Has(mask, MissileY)) m = m with { Y = r.ReadDouble() };
            if (Has(mask, MissileR)) m = m with { R = r.ReadDouble() };
            if (Has(mask, MissileO)) m = m with { O = r.ReadInt32() };
            if (Has(mask, MissileT)) m = m with { T = r.ReadInt32() };
            if (Has(mask, MissileW)) m = m with { W = r.ReadString()! };
            _missiles[id] = m;
        }

        private static List<int> ReadIds(ref MessagePackReader r)
        {
            var n = r.ReadArrayHeader();
            var ids = new List<int>(n);
            for (var i = 0; i < n; i++) ids.Add(r.ReadInt32());
            return ids;
        }

        private void ReadShip(ref MessagePackReader r)
        {
            r.ReadArrayHeader();
            var id = r.ReadInt32();
            var mask = r.ReadInt32();
            var full = mask == (1 << ShipFields) - 1;
            if (!_ships.TryGetValue(id, out var s))
            {
                if (!full) Desyncs++;
                s = new ShipDto(id, 0, 0, 0, 0, 0, "", 0, 0, 0, 0, "");
            }
            if (Has(mask, ShipX)) s = s with { X = r.ReadDouble() };
            if (Has(mask, ShipY)) s = s with { Y = r.ReadDouble() };
            if (Has(mask, ShipR)) s = s with { R = r.ReadDouble() };
            if (Has(mask, ShipVx)) s = s with { Vx = r.ReadDouble() };
            if (Has(mask, ShipVy)) s = s with { Vy = r.ReadDouble() };
            if (Has(mask, ShipHull)) s = s with { Hull = r.ReadString()! };
            if (Has(mask, ShipTh)) s = s with { Th = r.ReadDouble() };
            if (Has(mask, ShipAck)) s = s with { Ack = r.ReadInt32() };
            if (Has(mask, ShipHp)) s = s with { Hp = r.ReadInt32() };
            if (Has(mask, ShipSh)) s = s with { Sh = r.ReadInt32() };
            if (Has(mask, ShipW)) s = s with { W = r.ReadString()! };
            if (Has(mask, ShipRt)) s = s with { Rt = r.ReadInt64() };
            if (Has(mask, ShipPu)) s = s with { Pu = r.ReadInt64() };
            if (Has(mask, ShipTg)) s = s with { Tg = r.ReadInt32() };
            if (Has(mask, ShipAi)) s = s with { Ai = r.TryReadNil() ? null : r.ReadString() };
            if (Has(mask, ShipJ)) s = s with { J = r.ReadInt64() };
            if (Has(mask, ShipSl)) s = s with { Sl = r.ReadInt64() };
            _ships[id] = s;
        }

        private void ReadLoot(ref MessagePackReader r)
        {
            r.ReadArrayHeader();
            var id = r.ReadInt32();
            var mask = r.ReadInt32();
            if (!_loot.TryGetValue(id, out var l))
            {
                if (mask != (1 << LootFields) - 1) Desyncs++;
                l = new LootDto(id, 0, 0, "", 0, 0);
            }
            if (Has(mask, LootX)) l = l with { X = r.ReadDouble() };
            if (Has(mask, LootY)) l = l with { Y = r.ReadDouble() };
            if (Has(mask, LootI)) l = l with { I = r.ReadString()! };
            if (Has(mask, LootN)) l = l with { N = r.ReadInt32() };
            if (Has(mask, LootE)) l = l with { E = r.ReadInt64() };
            if (Has(mask, LootC)) l = l with { C = r.ReadBoolean() };
            _loot[id] = l;
        }

        private void ReadMeteor(ref MessagePackReader r)
        {
            r.ReadArrayHeader();
            var id = r.ReadInt32();
            var mask = r.ReadInt32();
            if (!_meteors.TryGetValue(id, out var m))
            {
                if (mask != (1 << MeteorFields) - 1) Desyncs++;
                m = new MeteorDto(id, 0, 0, 0, 0, "", 0);
            }
            if (Has(mask, MeteorX)) m = m with { X = r.ReadDouble() };
            if (Has(mask, MeteorY)) m = m with { Y = r.ReadDouble() };
            if (Has(mask, MeteorVx)) m = m with { Vx = r.ReadDouble() };
            if (Has(mask, MeteorVy)) m = m with { Vy = r.ReadDouble() };
            if (Has(mask, MeteorS)) m = m with { S = r.ReadString()! };
            if (Has(mask, MeteorHp)) m = m with { Hp = r.ReadInt32() };
            _meteors[id] = m;
        }
    }
}
