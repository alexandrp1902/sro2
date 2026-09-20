import { Container, Graphics, Sprite, Text } from 'pixi.js';
import type { ShotDto } from '../net/protocol';
import type { Weapons } from '../sim/weapons';
import { spriteSize, texture, type SpriteName } from './sprites';

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
const FLIGHT_MS: Record<string, number> = { bolt: 150, orb: 320, beam: 0, rail: 90, ion: 260, flak: 120 };
const BEAM_MS = 140;
/** Длина снаряда в мире; у плазмы — сгусток с хвостом. */
const PROJECTILE_LENGTH: Record<string, number> = { bolt: 42, orb: 34, rail: 90, ion: 40, flak: 26 };
/**
 * Где на картинке снаряда его голова (доля ширины): она и летит в цель.
 * Кадры рельсы, иона и зенитки пришли от генератора носом вверх и поворачиваются при нарезке
 * (NOSE_UP в tools/sprites.py); прежние доли подбирались под ту, боком летевшую, картинку.
 */
const PROJECTILE_HEAD: Record<string, number> = { bolt: 0.9, orb: 0.78, rail: 0.95, ion: 0.9, flak: 0.9 };
/** Толщина луча лазера. */
const BEAM_WIDTH = 12;
/** Вспышка у ствола и вспышка попадания — в размерах корабля. */
const MUZZLE_SIZE = 1.9;
const HIT_SIZE = 1.7;
const MUZZLE_MS = 130;
const HIT_MS = 260;
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
const EXPLOSION_MS = 950;
/** Кадры взрыва с листа explosions.png. */
const EXPLOSION_FRAMES = Array.from({ length: 9 }, (_, i) => `explosions-f${i}` as SpriteName);
/** Сторона кадра в размерах корабля: огонь занимает около двух третей кадра. */
const EXPLOSION_SIZE = 6.5;
/** Взрыв ракеты — такая доля взрыва корабля. */
const MISSILE_BLAST = 0.4;
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
      if (to) this.impact(shot, null, to, now);
      return;
    }
    // Ракета долетела сама (render/missiles.ts): трассера нет — только попадание и малый взрыв на цели.
    const weapon = this.weapons.get(shot.w);
    if (weapon.missile) {
      if (to) {
        this.impact(shot, 'bolt', to, now);
        this.add(new Explosion(this.view, to.x, to.y, to.size * MISSILE_BLAST, now));
      }
      return;
    }
    if (!from || !to) return;
    const kind = shotKind(weapon.kind);
    this.add(new SpriteFlash(this.view, `weapon-shots-${kind}-flash`, shot.from, from, to, MUZZLE_SIZE, MUZZLE_MS, now, 0.12));
    this.add(new Tracer(this.view, shot, kind, now, from, to, (at, time) => this.impact(shot, kind, at, time)));
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

  /** @param kind вид выстрела для вспышки попадания; null — таран, вспышки нет */
  private impact(shot: ShotDto, kind: ShotKind | null, at: FxAnchor, now: number): void {
    const hullDamage = shot.dmg - shot.sh;
    if (!shot.hit) {
      this.add(new FloatingText(this.view, 'промах', MISS_COLOR, at, now));
      return;
    }
    if (kind) this.add(new SpriteFlash(this.view, `weapon-shots-${kind}-hit`, shot.to, at, null, HIT_SIZE, HIT_MS, now, 0.5));
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
  private readonly sprite: Sprite;
  private readonly width: number;
  private readonly side = Math.random() < 0.5 ? -1 : 1;
  private impacted = false;

  constructor(
    parent: Container,
    private readonly shot: ShotDto,
    private readonly kind: ShotKind,
    private readonly start: number,
    private from: FxAnchor,
    private to: FxAnchor,
    /** Снаряд долетел: где цель и в какой момент — от него живут вспышка попадания и число урона. */
    private readonly onImpact: (at: FxAnchor, time: number) => void,
  ) {
    const name: SpriteName = `weapon-shots-${kind}`;
    const { w, h } = spriteSize(name);
    this.width = w;
    this.sprite = new Sprite(texture(name));
    if (kind === 'beam') {
      // Луч — картинка, растянутая от ствола до точки попадания.
      this.sprite.anchor.set(0, 0.5);
      this.sprite.scale.set(1, BEAM_WIDTH / h);
    } else {
      this.sprite.anchor.set(PROJECTILE_HEAD[kind], 0.5);
      this.sprite.scale.set(PROJECTILE_LENGTH[kind] / w);
    }
    this.sprite.visible = false;
    parent.addChild(this.sprite);
  }

  update(now: number, _zoom: number, locate: Locate): boolean {
    this.from = copy(locate(this.shot.from)) ?? this.from;
    this.to = copy(locate(this.shot.to)) ?? this.to;
    const { from, to, sprite } = this;
    const end = this.shot.hit ? to : missPoint(from, to, this.side);
    const elapsed = now - this.start;
    sprite.visible = true;
    sprite.rotation = Math.atan2(end.y - from.y, end.x - from.x);

    if (this.kind === 'beam') {
      if (!this.impacted) this.hit(this.start);
      const fade = 1 - elapsed / BEAM_MS;
      if (fade <= 0) return false;
      sprite.position.set(from.x, from.y);
      sprite.scale.x = (Math.hypot(end.x - from.x, end.y - from.y) || 1) / this.width;
      sprite.alpha = fade;
      return true;
    }

    const flight = FLIGHT_MS[this.kind] ?? FLIGHT_MS.bolt;
    // Промах пролетает мимо цели на пути к точке дальше неё — «промах» всплывает, когда снаряд поравнялся с целью.
    const total = this.shot.hit ? flight : flight * MISS_OVERSHOOT;
    const p = Math.min(1, elapsed / total);
    if (!this.impacted && elapsed >= flight) this.hit(this.start + flight);
    sprite.position.set(from.x + (end.x - from.x) * p, from.y + (end.y - from.y) * p);
    return p < 1;
  }

  destroy(): void {
    this.sprite.destroy();
  }

  private hit(time: number): void {
    this.impacted = true;
    this.onImpact({ ...this.to }, time);
  }
}

/**
 * Вспышка-картинка у корабля: у ствола (развёрнута к цели) или в месте попадания. Следит за кораблём,
 * растёт и гаснет.
 * @param toward куда развернуть вспышку; null — попадание: из центра, под случайным углом
 * @param grow насколько вспышка вырастает к концу
 */
class SpriteFlash implements Effect {
  private readonly sprite: Sprite;
  private readonly scale: number;

  constructor(
    parent: Container,
    name: SpriteName,
    private readonly id: number,
    private at: FxAnchor,
    toward: FxAnchor | null,
    size: number,
    private readonly duration: number,
    private readonly start: number,
    private readonly grow: number,
  ) {
    const { w, h } = spriteSize(name);
    this.sprite = new Sprite(texture(name));
    this.scale = (at.size * size) / Math.max(w, h);
    this.sprite.anchor.set(toward ? 0.2 : 0.5, 0.5);
    this.sprite.rotation = toward ? Math.atan2(toward.y - at.y, toward.x - at.x) : Math.random() * Math.PI * 2;
    this.sprite.visible = false;
    parent.addChild(this.sprite);
  }

  update(now: number, _zoom: number, locate: Locate): boolean {
    const t = (now - this.start) / this.duration;
    if (t >= 1) return false;
    this.at = copy(locate(this.id)) ?? this.at;
    this.sprite.visible = true;
    this.sprite.position.set(this.at.x, this.at.y);
    this.sprite.scale.set(this.scale * (1 + this.grow * t));
    this.sprite.alpha = t < 0.4 ? 1 : 1 - (t - 0.4) / 0.6;
    return true;
  }

  destroy(): void {
    this.sprite.destroy();
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

/** Уничтожение: девять кадров с листа — вспышка, огненный шар, дым и тлеющие обломки. */
class Explosion implements Effect {
  private readonly sprite = new Sprite();
  private readonly scale: number;

  constructor(parent: Container, x: number, y: number, size: number, private readonly start: number) {
    this.scale = (size * EXPLOSION_SIZE) / spriteSize(EXPLOSION_FRAMES[0]).w;
    this.sprite.anchor.set(0.5);
    this.sprite.position.set(x, y);
    this.sprite.rotation = Math.random() * Math.PI * 2; // взрывы не одинаковые
    this.sprite.visible = false;
    parent.addChild(this.sprite);
  }

  update(now: number): boolean {
    const t = (now - this.start) / EXPLOSION_MS;
    if (t >= 1) return false;
    const frame = Math.min(EXPLOSION_FRAMES.length - 1, Math.floor(t * EXPLOSION_FRAMES.length));
    this.sprite.texture = texture(EXPLOSION_FRAMES[frame]);
    this.sprite.visible = true;
    // Кадры одного масштаба; лёгкое расширение сглаживает скачки между ними.
    this.sprite.scale.set(this.scale * (0.9 + 0.2 * t));
    this.sprite.alpha = t < 0.75 ? 1 : 1 - (t - 0.75) / 0.25;
    return true;
  }

  destroy(): void {
    this.sprite.destroy();
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

type ShotKind = 'bolt' | 'beam' | 'orb' | 'rail' | 'ion' | 'flak';

const SHOT_KINDS: ShotKind[] = ['bolt', 'beam', 'orb', 'rail', 'ion', 'flak'];

/** Вид выстрела по пушке — от него картинки снаряда и вспышек; неизвестный — импульсный снаряд. */
function shotKind(kind: string): ShotKind {
  return (SHOT_KINDS as string[]).includes(kind) ? (kind as ShotKind) : 'bolt';
}
