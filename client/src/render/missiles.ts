import { Container, Graphics } from 'pixi.js';
import type { MissileDto, SnapshotMsg } from '../net/protocol';
import { DT } from '../sim/movement';
import type { Weapons } from '../sim/weapons';

/** Длина ракеты в мире. */
const LENGTH = 16;
/** Хвост — столько последних точек пути. */
const TRAIL_POINTS = 14;
/** Ракета, которая летит в меня, — красная; остальные — цвета своей ракетницы. */
const INCOMING_COLOR = 0xff4a3a;
/** Ракета ушла из снапшота, а мир ещё рисуется в прошлом: дорисовываем её столько тиков. */
const KEEP_TICKS = 6;

interface Sample {
  tick: number;
  x: number;
  y: number;
  r: number;
}

interface Flying {
  dto: MissileDto;
  samples: Sample[];
  trail: { x: number; y: number }[];
  body: Graphics;
  tail: Graphics;
  /** Последний тик, в котором ракета была в снапшоте. */
  lastTick: number;
}

/** Ракета, как она нарисована в этом кадре: для миникарты и предупреждения. */
export interface MissileInfo {
  id: number;
  x: number;
  y: number;
  /** Кто запустил и в кого летит. */
  owner: number;
  target: number;
}

/**
 * Ракеты (боевой документ §37). Рисуются в том же прошлом, что и чужие корабли (renderTick): иначе ракета
 * прилетала бы к цели раньше, чем её видно. Между кадрами — по прямой с её скоростью; за кадром — дальше по курсу.
 */
export class MissileField {
  readonly view = new Container();
  private readonly flying = new Map<number, Flying>();
  private readonly drawn: MissileInfo[] = [];
  private latestTick = 0;

  constructor(private readonly weapons: Weapons) {}

  push(message: SnapshotMsg): void {
    this.latestTick = message.tick;
    for (const dto of message.missiles ?? []) {
      let m = this.flying.get(dto.id);
      if (!m) {
        m = { dto, samples: [], trail: [], body: new Graphics(), tail: new Graphics(), lastTick: message.tick };
        this.view.addChild(m.tail, m.body);
        this.flying.set(dto.id, m);
      }
      m.dto = dto;
      m.lastTick = message.tick;
      m.samples.push({ tick: message.tick, x: dto.x, y: dto.y, r: dto.r });
      if (m.samples.length > 8) m.samples.shift();
    }
  }

  clear(): void {
    for (const m of this.flying.values()) this.destroy(m);
    this.flying.clear();
    this.drawn.length = 0;
  }

  /** Ракеты в меня — по последнему снапшоту: предупреждение не должно запаздывать. */
  incoming(ownId: number): number {
    let count = 0;
    for (const m of this.flying.values()) if (m.dto.t === ownId && m.lastTick === this.latestTick) count++;
    return count;
  }

  visible(): readonly MissileInfo[] {
    return this.drawn;
  }

  /** @param renderTick тик, в котором сейчас нарисован мир (дробный); NaN — снапшотов ещё нет */
  update(renderTick: number, ownId: number): void {
    this.drawn.length = 0;
    const tick = Number.isNaN(renderTick) ? this.latestTick : renderTick;
    for (const [id, m] of this.flying) {
      if (tick > m.lastTick + KEEP_TICKS) {
        this.destroy(m);
        this.flying.delete(id);
        continue;
      }
      const at = this.at(m, tick);
      if (!at) {
        m.body.visible = m.tail.visible = false;
        continue;
      }
      m.body.visible = m.tail.visible = true;
      const incoming = m.dto.t === ownId;
      const color = incoming ? INCOMING_COLOR : colorOf(this.weapons.get(m.dto.w).color);
      m.body.clear();
      // Корпус ракеты носом вверх и огонёк двигателя.
      m.body
        .poly([0, -LENGTH / 2, 4, LENGTH / 2, -4, LENGTH / 2])
        .fill({ color })
        .circle(0, LENGTH / 2 + 2, 3)
        .fill({ color: 0xffe0a0, alpha: 0.9 });
      m.body.position.set(at.x, at.y);
      m.body.rotation = at.r;

      const last = m.trail[m.trail.length - 1];
      if (!last || Math.hypot(last.x - at.x, last.y - at.y) > 4) m.trail.push({ x: at.x, y: at.y });
      if (m.trail.length > TRAIL_POINTS) m.trail.shift();
      m.tail.clear();
      for (let i = 1; i < m.trail.length; i++) {
        const a = m.trail[i - 1];
        const b = m.trail[i];
        m.tail.moveTo(a.x, a.y).lineTo(b.x, b.y).stroke({ width: 3, color, alpha: (i / m.trail.length) * 0.5, cap: 'round' });
      }
      this.drawn.push({ id, x: at.x, y: at.y, owner: m.dto.o, target: m.dto.t });
    }
  }

  /** Положение в тик tick: между двумя снапшотами — по прямой, после последнего — дальше по курсу. */
  private at(m: Flying, tick: number): Sample | null {
    const s = m.samples;
    if (s.length === 0 || tick < s[0].tick - 1) return null;
    for (let i = s.length - 1; i > 0; i--) {
      const a = s[i - 1];
      const b = s[i];
      if (tick >= a.tick && tick <= b.tick) {
        const k = b.tick > a.tick ? (tick - a.tick) / (b.tick - a.tick) : 1;
        return { tick, x: a.x + (b.x - a.x) * k, y: a.y + (b.y - a.y) * k, r: b.r };
      }
    }
    const last = s[s.length - 1];
    const speed = this.weapons.get(m.dto.w).missile?.speed ?? 0;
    const seconds = Math.max(-DT, (tick - last.tick) * DT);
    return { tick, x: last.x + Math.sin(last.r) * speed * seconds, y: last.y - Math.cos(last.r) * speed * seconds, r: last.r };
  }

  private destroy(m: Flying): void {
    m.body.destroy();
    m.tail.destroy();
  }
}

function colorOf(hex: string): number {
  const n = Number.parseInt(hex.replace('#', ''), 16);
  return Number.isNaN(n) ? 0xff6b3d : n;
}
