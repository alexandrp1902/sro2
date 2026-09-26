import { Container, Graphics, type Sprite } from 'pixi.js';
import type { MissileDto, SnapshotMsg } from '../net/protocol';
import { neonExhaust } from './exhaust';
import { DT } from '../sim/movement';
import type { Weapons } from '../sim/weapons';

/** Длина ракеты в мире; торпеда (M11) заметно больше, ракета залпа (M19) — меньше. */
const LENGTH = 16;
const TORPEDO_LENGTH = 30;
const ROCKET_LENGTH = 11;
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
  body: Container;
  casing: Graphics;
  exhaust: Sprite;
  style: string;
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

  /**
   * Новая ракета в снапшоте — это пуск: по нему звучит стартовый заряд (M17). Ракеты, уже летящие
   * в момент подключения, тоже сочтутся новыми, но их пуски погасит бюджет звука.
   */
  onLaunch: ((dto: MissileDto) => void) | null = null;

  constructor(private readonly weapons: Weapons) {}

  push(message: SnapshotMsg): void {
    this.latestTick = message.tick;
    for (const dto of message.missiles ?? []) {
      let m = this.flying.get(dto.id);
      if (!m) {
        const casing = new Graphics();
        const exhaust = neonExhaust(14, 24);
        const body = new Container();
        body.addChild(exhaust, casing);
        m = { dto, samples: [], trail: [], body, casing, exhaust, style: '', tail: new Graphics(), lastTick: message.tick };
        m.tail.blendMode = 'add';
        this.view.addChild(m.tail, m.body);
        this.flying.set(dto.id, m);
        this.onLaunch?.(dto);
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

  /** Ракета по id среди нарисованных: по ней трассер зенитки (M11) знает, куда стрелять. */
  find(id: number): MissileInfo | null {
    return this.drawn.find((m) => m.id === id) ?? null;
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
      const weapon = this.weapons.get(m.dto.w);
      const color = incoming ? INCOMING_COLOR : colorOf(weapon.color);
      // Три размера вместо двух (M19): у залпа ракеты мелкие — четыре штуки в кадре не должны
      // выглядеть четырьмя торпедами.
      const kind = weapon.missile?.sprite ?? '';
      const torpedo = kind === 'torpedo';
      const rocket = kind === 'rocket';
      const length = torpedo ? TORPEDO_LENGTH : rocket ? ROCKET_LENGTH : LENGTH;
      const halfWidth = torpedo ? 7 : rocket ? 3 : 4;
      const style = `${kind}:${color}`;
      if (style !== m.style) {
        m.style = style;
        const g = m.casing.clear();
        const w = halfWidth * 0.6;
        // Separate fins, shaded casing, nose cap and faction stripe.
        g.poly([-w, 1, -halfWidth * 1.35, length * 0.48, halfWidth * 1.35, length * 0.48, w, 1])
          .fill(0x405d78).stroke({ color: 0x8bb8d2, width: 0.7 });
        g.poly([0, -length / 2, w, -length * 0.24, w, length * 0.42, -w, length * 0.42, -w, -length * 0.24])
          .fill(0x90b0c8).stroke({ color: 0x25384d, width: 0.8 });
        g.poly([0, -length / 2, w * 0.7, -length * 0.2, -w * 0.7, -length * 0.2]).fill(color);
        g.rect(-w * 0.65, -length * 0.12, w * 0.6, length * 0.42).fill(0xe1f5ff);
        g.rect(-w, length * 0.22, w * 2, torpedo ? 3 : 2).fill(color);
        g.roundRect(-w, length * 0.4, w * 2, 3, 1).fill(0x162a42);
        m.exhaust.position.set(0, length * 0.48);
        m.exhaust.width = torpedo ? 23 : rocket ? 10 : 14;
        m.exhaust.height = torpedo ? 38 : rocket ? 17 : 24;
      }
      m.exhaust.alpha = 0.86 + 0.12 * Math.sin(tick * 1.7 + id);
      m.body.position.set(at.x, at.y);
      m.body.rotation = at.r;

      m.body.scale.set(1);
      const last = m.trail[m.trail.length - 1];
      const nozzle = { x: at.x - Math.sin(at.r) * length / 2, y: at.y + Math.cos(at.r) * length / 2 };
      if (!last || Math.hypot(last.x - nozzle.x, last.y - nozzle.y) > 4) m.trail.push(nozzle);
      if (m.trail.length > TRAIL_POINTS) m.trail.shift();
      m.tail.clear();
      for (let i = 1; i < m.trail.length; i++) {
        const a = m.trail[i - 1];
        const b = m.trail[i];
        const alpha = (i / m.trail.length) ** 2;
        m.tail.moveTo(a.x, a.y).lineTo(b.x, b.y).stroke({ width: torpedo ? 8 : rocket ? 3 : 5, color: 0x188fff, alpha: alpha * 0.12, cap: 'round' });
        m.tail.moveTo(a.x, a.y).lineTo(b.x, b.y).stroke({ width: torpedo ? 2 : 1, color: 0x86eaff, alpha: alpha * 0.48, cap: 'round' });
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
    m.body.destroy({ children: true });
    m.tail.destroy();
  }
}

function colorOf(hex: string): number {
  const n = Number.parseInt(hex.replace('#', ''), 16);
  return Number.isNaN(n) ? 0xff6b3d : n;
}
