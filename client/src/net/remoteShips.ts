import { Container } from 'pixi.js';
import { ShipView, engineGlow } from '../render/ship';
import type { Hulls } from '../sim/hulls';
import { RenderClock, SnapshotBuffer } from './interpolation';
import type { AiState, NpcKind, PlayerDto, SnapshotMsg } from './protocol';
import type { Roster } from './roster';

/** Игрок или вид NPC — от этого картинка корабля, цвет подписи и стрелки. */
export type ShipKind = 'player' | NpcKind;

const FADE_IN_MS = 300;
/** Корабль без связи висит в космосе полупрозрачным. */
const LOST_ALPHA = 0.4;

/** Чужой корабль, каким он нарисован в этом кадре: для ников, полосок, выбора цели и эффектов. */
export interface RemoteShipInfo {
  id: number;
  x: number;
  y: number;
  rot: number;
  vx: number;
  vy: number;
  size: number;
  alpha: number;
  name: string;
  online: boolean;
  npc: boolean;
  kind: ShipKind;
  /** Цель пирата в бою; 0 — нет. */
  targetId: number;
  /** Состояние ИИ пирата; null — не пират. */
  ai: AiState | null;
  hull: string;
  hp: number;
  sh: number;
  maxHp: number;
  maxSh: number;
  /** Уничтожен и ждёт респауна. */
  dead: boolean;
  /** Под защитой после появления. */
  protected: boolean;
  /** Готовит гиперпрыжок: уйдёт из системы в этот тик; 0 — нет. */
  jumpAt: number;
}

interface Remote {
  ship: ShipView;
  bornAt: number;
  visible: boolean;
  info: RemoteShipInfo;
}

/** Чужие корабли и дроны: интерполяция между снапшотами по часам RenderClock (§52). */
export class RemoteShips {
  readonly view = new Container();
  readonly clock = new RenderClock();
  /** Сколько раз снапшоты опоздали и чужие корабли летели по экстраполяции. */
  extrapolations = 0;
  /** Тик, на который нарисованы чужие корабли в этом кадре; NaN — снапшотов ещё не было. */
  renderTick = Number.NaN;

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
    this.renderTick = Number.NaN;
    for (const remote of this.ships.values()) remote.ship.view.destroy({ children: true });
    this.ships.clear();
  }

  /** Целые корабли, нарисованные в последнем update. */
  *visible(): Iterable<RemoteShipInfo> {
    for (const remote of this.ships.values()) if (remote.visible) yield remote.info;
  }

  /** Корабль из последнего update — в том числе уничтоженный (для карточки цели). */
  get(id: number): RemoteShipInfo | undefined {
    return this.ships.get(id)?.info;
  }

  /** Есть ли корабль в последнем снапшоте; null — снапшотов ещё нет. */
  inLatest(id: number): boolean | null {
    const latest = this.buffer.latest;
    return latest ? latest.ships.has(id) : null;
  }

  update(now: number, ownId: number): void {
    const latest = this.buffer.latest;
    if (!latest) return;
    const renderTick = this.clock.update(now);
    this.renderTick = renderTick;

    let extrapolating = false;
    for (const id of latest.ships.keys()) {
      if (id === ownId) continue;
      const player = this.roster.get(id);
      let remote = this.ships.get(id);
      if (!remote) {
        remote = this.create(id, kindOf(player), now);
        this.ships.set(id, remote);
      }

      const s = this.buffer.sample(id, renderTick);
      if (!s) {
        remote.visible = remote.ship.view.visible = false;
        continue;
      }
      extrapolating ||= s.extrapolated;

      const info = remote.info;
      const dead = s.rt > 0;
      if (info.dead && !dead) remote.bornAt = now; // респаун: корабль проявляется у станции
      const hull = this.hulls.get(s.hull);
      const online = player?.online ?? true;
      const alpha = Math.min(1, (now - remote.bornAt) / FADE_IN_MS) * (online ? 1 : LOST_ALPHA);
      remote.visible = remote.ship.view.visible = !dead;
      if (!dead) {
        remote.ship.update(s.x, s.y, s.rot, s.hull, hull, engineGlow(s, s.th, hull), null);
        remote.ship.view.alpha = alpha;
      }

      info.x = s.x;
      info.y = s.y;
      info.rot = s.rot;
      info.vx = s.vx;
      info.vy = s.vy;
      info.size = hull.size;
      info.alpha = alpha;
      info.name = player?.name ?? '';
      info.online = online;
      info.npc = player?.npc ?? false;
      info.targetId = s.tg;
      info.ai = s.ai;
      info.hull = s.hull;
      info.hp = s.hp;
      info.sh = s.sh;
      info.maxHp = player?.maxHp ?? hull.hp;
      info.maxSh = player?.maxSh ?? hull.shield;
      info.dead = dead;
      info.protected = s.pu > renderTick;
      info.jumpAt = s.j;
    }

    for (const [id, remote] of this.ships) {
      if (id !== ownId && latest.ships.has(id)) continue;
      remote.ship.view.destroy({ children: true });
      this.ships.delete(id);
    }

    if (extrapolating && !this.extrapolating) this.extrapolations++;
    this.extrapolating = extrapolating;
  }

  private create(id: number, kind: ShipKind, now: number): Remote {
    const ship = new ShipView(kind);
    this.view.addChild(ship.view);
    return {
      ship,
      bornAt: now,
      visible: false,
      info: {
        id,
        x: 0,
        y: 0,
        rot: 0,
        vx: 0,
        vy: 0,
        size: 0,
        alpha: 0,
        name: '',
        online: true,
        npc: kind !== 'player',
        kind,
        targetId: 0,
        jumpAt: 0,
        ai: null,
        hull: '',
        hp: 0,
        sh: 0,
        maxHp: 0,
        maxSh: 0,
        dead: false,
        protected: false,
      },
    };
  }
}

/** NPC без вида (сервер до M4) — дрон. */
export function kindOf(player: PlayerDto | undefined): ShipKind {
  if (!player?.npc) return 'player';
  return player.kind ?? 'drone';
}
