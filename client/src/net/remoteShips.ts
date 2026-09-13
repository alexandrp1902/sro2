import { Container } from 'pixi.js';
import { ShipView, engineGlow } from '../render/ship';
import type { Hulls } from '../sim/hulls';
import { RenderClock, SnapshotBuffer } from './interpolation';
import type { SnapshotMsg } from './protocol';
import type { Roster } from './roster';

const REMOTE_COLOR = 0xffb45a;
const FADE_IN_MS = 300;
/** Корабль без связи висит в космосе полупрозрачным. */
const LOST_ALPHA = 0.4;

/** Чужой корабль, каким он нарисован в этом кадре: для ников и стрелок за краем экрана. */
export interface RemoteShipInfo {
  id: number;
  x: number;
  y: number;
  size: number;
  alpha: number;
  name: string;
  online: boolean;
}

interface Remote {
  ship: ShipView;
  bornAt: number;
  visible: boolean;
  info: RemoteShipInfo;
}

/** Чужие корабли: интерполяция между снапшотами по часам RenderClock (§52). */
export class RemoteShips {
  readonly view = new Container();
  readonly clock = new RenderClock();
  /** Сколько раз снапшоты опоздали и чужие корабли летели по экстраполяции. */
  extrapolations = 0;

  private readonly buffer = new SnapshotBuffer();
  private readonly ships = new Map<number, Remote>();
  private extrapolating = false;

  constructor(
    private readonly hulls: Hulls,
    private readonly roster: Roster,
  ) {}

  push(snapshot: SnapshotMsg, now: number): void {
    if (this.buffer.push(snapshot)) this.clock.onSnapshot(snapshot.tick, now);
  }

  clear(): void {
    this.buffer.clear();
    for (const remote of this.ships.values()) remote.ship.view.destroy({ children: true });
    this.ships.clear();
  }

  /** Корабли, нарисованные в последнем update. */
  *visible(): Iterable<RemoteShipInfo> {
    for (const remote of this.ships.values()) if (remote.visible) yield remote.info;
  }

  update(now: number, ownId: number): void {
    const latest = this.buffer.latest;
    if (!latest) return;
    const renderTick = this.clock.update(now);

    let extrapolating = false;
    for (const id of latest.ships.keys()) {
      if (id === ownId) continue;
      let remote = this.ships.get(id);
      if (!remote) {
        remote = { ship: new ShipView(REMOTE_COLOR), bornAt: now, visible: false, info: { id, x: 0, y: 0, size: 0, alpha: 0, name: '', online: true } };
        this.ships.set(id, remote);
        this.view.addChild(remote.ship.view);
      }

      const s = this.buffer.sample(id, renderTick);
      remote.visible = remote.ship.view.visible = s !== null;
      if (!s) continue;
      extrapolating ||= s.extrapolated;

      const hull = this.hulls.get(s.hull);
      const player = this.roster.get(id);
      const online = player?.online ?? true;
      const alpha = Math.min(1, (now - remote.bornAt) / FADE_IN_MS) * (online ? 1 : LOST_ALPHA);
      remote.ship.update(s.x, s.y, s.rot, hull, engineGlow(s, s.th, hull), null);
      remote.ship.view.alpha = alpha;
      const info = remote.info;
      info.x = s.x;
      info.y = s.y;
      info.size = hull.size;
      info.alpha = alpha;
      info.name = player?.name ?? '';
      info.online = online;
    }

    for (const [id, remote] of this.ships) {
      if (id !== ownId && latest.ships.has(id)) continue;
      remote.ship.view.destroy({ children: true });
      this.ships.delete(id);
    }

    if (extrapolating && !this.extrapolating) this.extrapolations++;
    this.extrapolating = extrapolating;
  }
}
