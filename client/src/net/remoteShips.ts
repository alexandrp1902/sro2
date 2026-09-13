import { Container } from 'pixi.js';
import { ShipView, engineGlow } from '../render/ship';
import type { Hulls } from '../sim/hulls';
import { DT, wrapAngle } from '../sim/movement';
import type { ShipDto, SnapshotMsg } from './protocol';

/** Чужие корабли рисуются на 2 тика (100 мс) в прошлом — между двумя полученными снапшотами. */
const INTERPOLATION_DELAY_TICKS = 2;
const BUFFER_SIZE = 20;
const REMOTE_COLOR = 0xffb45a;

export class RemoteShips {
  readonly view = new Container();
  private snapshots: SnapshotMsg[] = [];
  private latestArrival = 0;
  private readonly ships = new Map<number, ShipView>();

  constructor(private readonly hulls: Hulls) {}

  push(snapshot: SnapshotMsg, now: number): void {
    const last = this.snapshots[this.snapshots.length - 1];
    if (last && snapshot.tick <= last.tick) return;
    this.snapshots.push(snapshot);
    if (this.snapshots.length > BUFFER_SIZE) this.snapshots.shift();
    this.latestArrival = now;
  }

  clear(): void {
    this.snapshots = [];
    for (const ship of this.ships.values()) ship.view.destroy({ children: true });
    this.ships.clear();
  }

  update(now: number, ownId: number): void {
    const latest = this.snapshots[this.snapshots.length - 1];
    if (!latest) return;

    const renderTick = latest.tick + (now - this.latestArrival) / (DT * 1000) - INTERPOLATION_DELAY_TICKS;
    let a = this.snapshots[0];
    let b = a;
    for (const snapshot of this.snapshots) {
      if (snapshot.tick <= renderTick) a = snapshot;
      else {
        b = snapshot;
        break;
      }
    }
    if (b.tick <= a.tick) b = a;
    const alpha = b === a ? 0 : Math.min(1, (renderTick - a.tick) / (b.tick - a.tick));

    const present = new Set<number>();
    for (const to of latest.ships) {
      if (to.id === ownId) continue;
      present.add(to.id);
      const from = find(a, to.id) ?? to;
      const target = find(b, to.id) ?? from;
      let ship = this.ships.get(to.id);
      if (!ship) {
        ship = new ShipView(REMOTE_COLOR);
        this.ships.set(to.id, ship);
        this.view.addChild(ship.view);
      }
      const hull = this.hulls.get(target.hull);
      const rot = from.r + wrapAngle(target.r - from.r) * alpha;
      const state = { x: 0, y: 0, rot, vx: target.vx, vy: target.vy };
      ship.update(
        from.x + (target.x - from.x) * alpha,
        from.y + (target.y - from.y) * alpha,
        rot,
        hull,
        engineGlow(state, target.th, hull),
        null,
      );
    }
    for (const [id, ship] of this.ships) {
      if (present.has(id)) continue;
      ship.view.destroy({ children: true });
      this.ships.delete(id);
    }
  }
}

function find(snapshot: SnapshotMsg, id: number): ShipDto | undefined {
  return snapshot.ships.find((ship) => ship.id === id);
}
