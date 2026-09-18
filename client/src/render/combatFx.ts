import { Container, Graphics, Text } from 'pixi.js';
import type { ShotDto } from '../net/protocol';
import type { Weapons } from '../sim/weapons';

/** Где сейчас нарисован корабль. */
export interface FxAnchor {
  x: number;
  y: number;
  size: number;
}

/** Нарисованное положение корабля по id; null — корабля уже нет. */
export type Locate = (id: number) => FxAnchor | null;

/** Псевдо-пушка тарана метеорита в ShotDto (MeteorRules.RamWeapon на сервере). */
export const RAM_WEAPON = 'ram';
/** Время полёта снаряда до цели, мс; луч — мгновенный. */
const FLIGHT_MS: Record<string, number> = { bolt: 150, orb: 320, beam: 0 };
const BEAM_MS = 120;
const BOLT_LENGTH = 26;
const ORB_RADIUS = 6;
/** Промах уходит мимо цели: на столько её размеров вбок и настолько дальше неё. */
const MISS_SIDE = 2.4;
const MISS_OVERSHOOT = 1.35;

const NUMBER_MS = 900;
/** Число урона всплывает на столько экранных px. */
const NUMBER_RISE_PX = 34;
const SHIELD_COLOR = 0x7fc8ff;
const HULL_COLOR = 0xffb45a;
const MISS_COLOR = 0x9aa4b4;
const FLASH_MS = 260;
const SPARK_MS = 220;
const EXPLOSION_MS = 800;
/** Тракторный луч (GDD §21): предмет втягивается в корабль за это время. */
const TRACTOR_MS = 350;

interface Effect {
  /** @returns false — эффект закончился */
  update(now: number, zoom: number, locate: Locate): boolean;
  destroy(): void;
}

/** Трассеры, числа урона, вспышки щита и взрывы. Слой в мировых координатах, внутри контейнера мира. */
export class CombatFx {
  readonly view = new Container();
  private effects: Effect[] = [];

  constructor(private readonly weapons: Weapons) {}

  shot(shot: ShotDto, now: number, locate: Locate): void {
    const from = locate(shot.from);
    const to = locate(shot.to);
    // Таран метеорита: снаряда нет, камень уже разбился — сразу цифры, вспышка щита и искры на корабле.
    if (shot.w === RAM_WEAPON) {
      if (to) this.impact(shot, to, now);
      return;
    }
    if (!from || !to) return;
    const weapon = this.weapons.get(shot.w);
    this.add(
      new Tracer(this.view, shot, weapon.kind, parseColor(weapon.color), now, from, to, (at) => this.impact(shot, at, now)),
    );
  }

  explosion(x: number, y: number, size: number, now: number): void {
    this.add(new Explosion(this.view, x, y, size, now));
  }

  /**
   * Предмет ушёл в трюм: луч от корабля и втягивание. Видно и у чужих кораблей — иначе непонятно,
   * куда делся предмет.
   * @param label подпись вроде «+Металл ×5»; пустая — не показывать (чужой подбор).
   */
  tractor(shipId: number, at: FxAnchor, x: number, y: number, color: number, label: string, now: number): void {
    this.add(new TractorBeam(this.view, shipId, at, x, y, color, now));
    if (label) this.add(new FloatingText(this.view, label, color, at, now));
  }

  update(now: number, zoom: number, locate: Locate): void {
    const alive: Effect[] = [];
    // Новые эффекты (вспышки от попаданий) добавляются прямо во время обхода — они обновятся со следующего кадра.
    const current = this.effects;
    this.effects = alive;
    for (const effect of current) {
      if (effect.update(now, zoom, locate)) alive.push(effect);
      else effect.destroy();
    }
  }

  private impact(shot: ShotDto, at: FxAnchor, now: number): void {
    const hullDamage = shot.dmg - shot.sh;
    if (!shot.hit) {
      this.add(new FloatingText(this.view, 'промах', MISS_COLOR, at, now));
      return;
    }
    this.add(new FloatingText(this.view, `−${shot.dmg}`, hullDamage > 0 ? HULL_COLOR : SHIELD_COLOR, at, now));
    if (shot.sh > 0) this.add(new ShieldFlash(this.view, shot.to, at, now));
    if (hullDamage > 0) this.add(new Sparks(this.view, shot.to, at, now));
  }

  private add(effect: Effect): void {
    this.effects.push(effect);
  }
}

/** Снаряд или луч от стрелка к цели; промах — мимо цели и дальше. Концы следят за нарисованными кораблями. */
class Tracer implements Effect {
  private readonly g = new Graphics();
  private readonly side = Math.random() < 0.5 ? -1 : 1;
  private impacted = false;

  constructor(
    parent: Container,
    private readonly shot: ShotDto,
    private readonly kind: string,
    private readonly color: number,
    private readonly start: number,
    private from: FxAnchor,
    private to: FxAnchor,
    private readonly onImpact: (at: FxAnchor) => void,
  ) {
    parent.addChild(this.g);
  }

  update(now: number, _zoom: number, locate: Locate): boolean {
    this.from = copy(locate(this.shot.from)) ?? this.from;
    this.to = copy(locate(this.shot.to)) ?? this.to;
    const { from, to } = this;
    const end = this.shot.hit ? to : missPoint(from, to, this.side);
    const elapsed = now - this.start;
    const g = this.g.clear();

    if (this.kind === 'beam') {
      if (!this.impacted) this.hit();
      const fade = 1 - elapsed / BEAM_MS;
      if (fade <= 0) return false;
      g.moveTo(from.x, from.y).lineTo(end.x, end.y).stroke({ width: 3, color: this.color, alpha: 0.35 * fade });
      g.moveTo(from.x, from.y).lineTo(end.x, end.y).stroke({ width: 1.2, color: 0xffffff, alpha: 0.9 * fade });
      return true;
    }

    const flight = FLIGHT_MS[this.kind] ?? FLIGHT_MS.bolt;
    // Промах пролетает мимо цели на пути к точке дальше неё — «промах» всплывает, когда снаряд поравнялся с целью.
    const total = this.shot.hit ? flight : flight * MISS_OVERSHOOT;
    const p = Math.min(1, elapsed / total);
    if (!this.impacted && elapsed >= flight) this.hit();
    const hx = from.x + (end.x - from.x) * p;
    const hy = from.y + (end.y - from.y) * p;
    if (this.kind === 'orb') {
      g.circle(hx, hy, ORB_RADIUS * 1.8).fill({ color: this.color, alpha: 0.25 });
      g.circle(hx, hy, ORB_RADIUS).fill({ color: this.color, alpha: 0.95 });
    } else {
      const length = Math.hypot(end.x - from.x, end.y - from.y) || 1;
      const tail = Math.max(0, p - BOLT_LENGTH / length);
      g.moveTo(from.x + (end.x - from.x) * tail, from.y + (end.y - from.y) * tail)
        .lineTo(hx, hy)
        .stroke({ width: 3, color: this.color, alpha: 0.95, cap: 'round' });
    }
    return p < 1;
  }

  destroy(): void {
    this.g.destroy();
  }

  private hit(): void {
    this.impacted = true;
    this.onImpact({ ...this.to });
  }
}

/** «−100» или «промах» над целью: всплывает и тает, размер на экране не зависит от зума. */
class FloatingText implements Effect {
  private readonly text: Text;
  private readonly x: number;
  private readonly y: number;

  constructor(
    parent: Container,
    label: string,
    color: number,
    at: FxAnchor,
    private readonly start: number,
  ) {
    this.text = new Text({
      text: label,
      style: {
        fill: color,
        fontSize: 15,
        fontWeight: '700',
        fontFamily: 'system-ui, -apple-system, "Segoe UI", sans-serif',
        stroke: { color: 0x05060a, width: 3 },
      },
    });
    this.text.resolution = 2;
    this.text.anchor.set(0.5, 1);
    // Небольшой разброс, чтобы числа частой стрельбы не ложились друг на друга.
    this.x = at.x + (Math.random() - 0.5) * at.size;
    this.y = at.y - at.size;
    parent.addChild(this.text);
  }

  update(now: number, zoom: number): boolean {
    const t = (now - this.start) / NUMBER_MS;
    if (t >= 1) return false;
    this.text.scale.set(1 / zoom);
    this.text.position.set(this.x, this.y - (NUMBER_RISE_PX * t) / zoom);
    this.text.alpha = t < 0.6 ? 1 : 1 - (t - 0.6) / 0.4;
    return true;
  }

  destroy(): void {
    this.text.destroy();
  }
}

/** Тракторный луч: линия от корабля к предмету и кольцо, которое стягивается вместе с ним. */
class TractorBeam implements Effect {
  private readonly g = new Graphics();

  constructor(
    parent: Container,
    private readonly id: number,
    private ship: FxAnchor,
    private readonly x: number,
    private readonly y: number,
    private readonly color: number,
    private readonly start: number,
  ) {
    parent.addChild(this.g);
  }

  update(now: number, _zoom: number, locate: Locate): boolean {
    const t = (now - this.start) / TRACTOR_MS;
    if (t >= 1) return false;
    this.ship = copy(locate(this.id)) ?? this.ship;

    // Предмет едет к кораблю, а не наоборот: корабль за это время мог сдвинуться.
    const px = this.x + (this.ship.x - this.x) * t;
    const py = this.y + (this.ship.y - this.y) * t;
    const fade = 1 - t;
    this.g
      .clear()
      .moveTo(this.ship.x, this.ship.y)
      .lineTo(px, py)
      .stroke({ width: 2, color: this.color, alpha: 0.75 * fade })
      .circle(px, py, 10 * fade)
      .stroke({ width: 2, color: this.color, alpha: 0.9 * fade });
    return true;
  }

  destroy(): void {
    this.g.destroy();
  }
}

/** Щит принял удар: голубое кольцо вокруг корабля. */
class ShieldFlash implements Effect {
  private readonly g = new Graphics();

  constructor(
    parent: Container,
    private readonly id: number,
    private at: FxAnchor,
    private readonly start: number,
  ) {
    parent.addChild(this.g);
  }

  update(now: number, _zoom: number, locate: Locate): boolean {
    const t = (now - this.start) / FLASH_MS;
    if (t >= 1) return false;
    this.at = copy(locate(this.id)) ?? this.at;
    const r = this.at.size * (1.45 + 0.35 * t);
    this.g
      .clear()
      .circle(this.at.x, this.at.y, r)
      .fill({ color: SHIELD_COLOR, alpha: 0.12 * (1 - t) })
      .stroke({ width: 2.5, color: SHIELD_COLOR, alpha: 0.85 * (1 - t) });
    return true;
  }

  destroy(): void {
    this.g.destroy();
  }
}

/** Попадание в корпус: искры от корабля. */
class Sparks implements Effect {
  private readonly g = new Graphics();
  private readonly angles = Array.from({ length: 6 }, () => Math.random() * Math.PI * 2);

  constructor(
    parent: Container,
    private readonly id: number,
    private at: FxAnchor,
    private readonly start: number,
  ) {
    parent.addChild(this.g);
  }

  update(now: number, _zoom: number, locate: Locate): boolean {
    const t = (now - this.start) / SPARK_MS;
    if (t >= 1) return false;
    this.at = copy(locate(this.id)) ?? this.at;
    const { x, y, size } = this.at;
    const g = this.g.clear();
    for (const a of this.angles) {
      const r0 = size * (0.3 + 0.9 * t);
      const r1 = r0 + size * 0.5;
      g.moveTo(x + Math.cos(a) * r0, y + Math.sin(a) * r0).lineTo(x + Math.cos(a) * r1, y + Math.sin(a) * r1);
    }
    g.stroke({ width: 2, color: HULL_COLOR, alpha: 1 - t });
    return true;
  }

  destroy(): void {
    this.g.destroy();
  }
}

/** Уничтожение: вспышка, расходящееся кольцо и обломки. */
class Explosion implements Effect {
  private readonly g = new Graphics();
  private readonly debris = Array.from({ length: 12 }, () => ({
    angle: Math.random() * Math.PI * 2,
    speed: 0.6 + Math.random() * 1.4,
  }));

  constructor(
    parent: Container,
    private readonly x: number,
    private readonly y: number,
    private readonly size: number,
    private readonly start: number,
  ) {
    parent.addChild(this.g);
  }

  update(now: number): boolean {
    const t = (now - this.start) / EXPLOSION_MS;
    if (t >= 1) return false;
    const { x, y, size } = this;
    const g = this.g.clear();
    if (t < 0.3) g.circle(x, y, size * (1.2 + 2 * t)).fill({ color: 0xfff1c0, alpha: 0.8 * (1 - t / 0.3) });
    g.circle(x, y, size * (1 + 4 * t)).stroke({ width: 3, color: HULL_COLOR, alpha: 0.9 * (1 - t) });
    for (const d of this.debris) {
      const r = size * (0.5 + 4 * d.speed * t);
      g.circle(x + Math.cos(d.angle) * r, y + Math.sin(d.angle) * r, 2.2).fill({ color: 0xffd08a, alpha: 1 - t });
    }
    return true;
  }

  destroy(): void {
    this.g.destroy();
  }
}

function missPoint(from: FxAnchor, to: FxAnchor, side: number): { x: number; y: number } {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const length = Math.hypot(dx, dy) || 1;
  const px = (-dy / length) * side * to.size * MISS_SIDE;
  const py = (dx / length) * side * to.size * MISS_SIDE;
  return { x: from.x + (dx + px) * MISS_OVERSHOOT, y: from.y + (dy + py) * MISS_OVERSHOOT };
}

function copy(anchor: FxAnchor | null): FxAnchor | null {
  return anchor ? { x: anchor.x, y: anchor.y, size: anchor.size } : null;
}

function parseColor(color: string): number {
  const value = Number.parseInt(color.replace('#', ''), 16);
  return Number.isNaN(value) ? 0xffd166 : value;
}
