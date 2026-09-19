import { decode } from '@msgpack/msgpack';
import type { AiState, KillDto, LootDto, MeteorDto, PickDto, ShipDto, ShotDto, SnapshotMsg } from './protocol';

// Зеркало server/Sro.Server/Net/SnapshotCodec.cs; общий эталон — shared/test-vectors/snapshot.json.
// Кадр: [1, tick, key, ships, goneShips, loot, goneLoot, meteors, goneMeteors, shots, kills, picks].
// Сущность — [id, mask, …поля, чьи биты стоят в mask, по порядку битов]; новая сущность и ключевой кадр — все поля.

const FRAME_TYPE = 1;

const SHIP_FIELDS = 16;
const LOOT_FIELDS = 6;
const METEOR_FIELDS = 6;

type Row = unknown[];

/**
 * Собирает из бинарных дельта-кадров полный снапшот — такой же, какой раньше приходил в JSON, поэтому
 * остальному клиенту всё равно, как его прислали. Сервер шлёт только то, что видит радар (GDD §10),
 * и только изменившиеся поля; ушедшие из радара — списком id.
 */
export class SnapshotDecoder {
  private readonly ships = new Map<number, ShipDto>();
  private readonly loot = new Map<number, LootDto>();
  private readonly meteors = new Map<number, MeteorDto>();
  /** Дельта пришла к сущности, которой мы не знаем: клиент и сервер разошлись. Ключевой кадр это починит. */
  desyncs = 0;

  reset(): void {
    this.ships.clear();
    this.loot.clear();
    this.meteors.clear();
  }

  decode(bytes: ArrayBuffer | Uint8Array): SnapshotMsg {
    const frame = decode(bytes) as Row;
    if (!Array.isArray(frame) || frame.length < 12 || frame[0] !== FRAME_TYPE) throw new Error('not a snapshot frame');
    const [, tick, key, ships, goneShips, loot, goneLoot, meteors, goneMeteors, shots, kills, picks] = frame as [
      number,
      number,
      boolean,
      Row[],
      number[],
      Row[],
      number[],
      Row[],
      number[],
      Row[] | null,
      Row[] | null,
      Row[] | null,
    ];
    if (key) this.reset();

    for (const row of ships) this.readShip(row);
    for (const id of goneShips) this.ships.delete(id);
    for (const row of loot) this.readLoot(row);
    for (const id of goneLoot) this.loot.delete(id);
    for (const row of meteors) this.readMeteor(row);
    for (const id of goneMeteors) this.meteors.delete(id);

    const message: SnapshotMsg = { t: 'snapshot', tick, ships: [...this.ships.values()].map((s) => ({ ...s })) };
    if (this.loot.size > 0) message.loot = [...this.loot.values()].map((l) => ({ ...l }));
    if (this.meteors.size > 0) message.meteors = [...this.meteors.values()].map((m) => ({ ...m }));
    if (shots) message.shots = shots.map(readShot);
    if (kills) message.kills = kills.map(([id, by]) => ({ id, by }) as KillDto);
    if (picks) message.picks = picks.map(([by, id, i, n]) => ({ by, id, i, n }) as PickDto);
    return message;
  }

  private readShip(row: Row): void {
    const id = row[0] as number;
    const mask = row[1] as number;
    let s = this.ships.get(id);
    if (!s) {
      if (mask !== (1 << SHIP_FIELDS) - 1) this.desyncs++;
      s = { id, x: 0, y: 0, r: 0, vx: 0, vy: 0, hull: '', th: 0, ack: 0, hp: 0, sh: 0, w: '', rt: 0, pu: 0, tg: 0, ai: null, j: 0 };
      this.ships.set(id, s);
    }
    let i = 2;
    const next = () => row[i++];
    if (mask & (1 << 0)) s.x = next() as number;
    if (mask & (1 << 1)) s.y = next() as number;
    if (mask & (1 << 2)) s.r = next() as number;
    if (mask & (1 << 3)) s.vx = next() as number;
    if (mask & (1 << 4)) s.vy = next() as number;
    if (mask & (1 << 5)) s.hull = next() as string;
    if (mask & (1 << 6)) s.th = next() as number;
    if (mask & (1 << 7)) s.ack = next() as number;
    if (mask & (1 << 8)) s.hp = next() as number;
    if (mask & (1 << 9)) s.sh = next() as number;
    if (mask & (1 << 10)) s.w = next() as string;
    if (mask & (1 << 11)) s.rt = next() as number;
    if (mask & (1 << 12)) s.pu = next() as number;
    if (mask & (1 << 13)) s.tg = next() as number;
    if (mask & (1 << 14)) s.ai = (next() as AiState | null) ?? null;
    if (mask & (1 << 15)) s.j = next() as number;
  }

  private readLoot(row: Row): void {
    const id = row[0] as number;
    const mask = row[1] as number;
    let l = this.loot.get(id);
    if (!l) {
      if (mask !== (1 << LOOT_FIELDS) - 1) this.desyncs++;
      l = { id, x: 0, y: 0, i: '', n: 0, e: 0, c: false };
      this.loot.set(id, l);
    }
    let i = 2;
    const next = () => row[i++];
    if (mask & (1 << 0)) l.x = next() as number;
    if (mask & (1 << 1)) l.y = next() as number;
    if (mask & (1 << 2)) l.i = next() as string;
    if (mask & (1 << 3)) l.n = next() as number;
    if (mask & (1 << 4)) l.e = next() as number;
    if (mask & (1 << 5)) l.c = next() as boolean;
  }

  private readMeteor(row: Row): void {
    const id = row[0] as number;
    const mask = row[1] as number;
    let m = this.meteors.get(id);
    if (!m) {
      if (mask !== (1 << METEOR_FIELDS) - 1) this.desyncs++;
      m = { id, x: 0, y: 0, vx: 0, vy: 0, s: '', hp: 0 };
      this.meteors.set(id, m);
    }
    let i = 2;
    const next = () => row[i++];
    if (mask & (1 << 0)) m.x = next() as number;
    if (mask & (1 << 1)) m.y = next() as number;
    if (mask & (1 << 2)) m.vx = next() as number;
    if (mask & (1 << 3)) m.vy = next() as number;
    if (mask & (1 << 4)) m.s = next() as string;
    if (mask & (1 << 5)) m.hp = next() as number;
  }
}

function readShot(row: Row): ShotDto {
  const [from, to, w, hit, dmg, sh, ch] = row as [number, number, string, boolean, number, number, number];
  return { from, to, w, hit, dmg, sh, ch };
}
