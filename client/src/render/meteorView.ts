import { Container, Graphics } from 'pixi.js';
import type { MeteorDto, SnapshotMsg } from '../net/protocol';
import { NO_METEORS, meteorSize, positionAt, shapeFor, spinFor, type MeteorRules } from '../sim/meteors';

const ROCK_COLOR = 0x5d544c;
const ROCK_EDGE = 0x8a7a68;
/** Рыжие прожилки: камень читается на тёмном небе и не путается с кораблём. */
const VEIN_COLOR = 0xd9803a;
const TAIL_COLOR = 0xffb27a;
/** Хвост — путь за столько секунд: по нему курс читается раньше любого расчёта. */
const TAIL_SECONDS = 0.45;
const TAIL_SEGMENTS = 6;
const FADE_IN_MS = 300;
const LAST_SEEN_MS = 2000;
const WARN_COLOR = 0xff4a4a;
const DASH = 14;
const GAP = 10;

/** Метеорит, каким он нарисован в этом кадре: для выбора целью, карточки, стрелок и эффектов. */
export interface MeteorInfo {
  id: number;
  x: number;
  y: number;
  vx: number;
  vy: number;
  /** Радиус — как size у кораблей: по нему попадание тапом и рамка цели. */
  size: number;
  sizeId: string;
  name: string;
  hp: number;
  maxHp: number;
  /** Для совместимости с проверкой выстрела: камень не бывает ни «уничтоженным на респауне», ни под защитой. */
  dead: false;
  protected: false;
}

/** Опасный метеорит этого кадра: секунды до касания — для кольца, пунктира и отсчёта. */
export interface MeteorThreat {
  id: number;
  seconds: number;
}

interface Rock {
  root: Container;
  body: Graphics;
  tail: Graphics;
  /** Последний снапшот и когда он пришёл: положение считается от него. */
  dto: MeteorDto;
  at: number;
  bornAt: number;
  spin: number;
  info: MeteorInfo;
  seen: boolean;
}

interface Gone {
  x: number;
  y: number;
  size: number;
  name: string;
  at: number;
}

/**
 * Метеориты (M5b). Мировой слой над лутом, под кораблями. Положение — от последнего снапшота по прямой,
 * в «настоящем» времени, как свой предсказанный корабль: таран на экране совпадает с касанием корпусов.
 */
export class MeteorField {
  readonly view = new Container();
  private readonly warn = new Graphics();
  private readonly layer = new Container();
  private rules: MeteorRules = NO_METEORS;
  private readonly rocks = new Map<number, Rock>();
  private readonly gone = new Map<number, Gone>();

  constructor() {
    this.view.addChild(this.warn, this.layer);
  }

  setRules(rules: MeteorRules | undefined): void {
    this.rules = rules ?? NO_METEORS;
  }

  clear(): void {
    for (const rock of this.rocks.values()) rock.root.destroy({ children: true });
    this.rocks.clear();
    this.gone.clear();
    this.warn.clear();
  }

  *visible(): Iterable<MeteorInfo> {
    for (const rock of this.rocks.values()) if (rock.seen) yield rock.info;
  }

  get(id: number): MeteorInfo | undefined {
    return this.rocks.get(id)?.info;
  }

  /** Последнее положение — в том числе уже разбитого: взрыв проигрывается позже, чем камень ушёл из снапшота. */
  lastSeen(id: number, now: number): { x: number; y: number; size: number } | null {
    const rock = this.rocks.get(id);
    if (rock) return rock.info;
    const gone = this.gone.get(id);
    return gone && now - gone.at <= LAST_SEEN_MS ? gone : null;
  }

  /** Имя для ленты: метеоритов нет в ростере. */
  nameOf(id: number): string | null {
    return this.rocks.get(id)?.info.name ?? this.gone.get(id)?.name ?? null;
  }

  push(snapshot: SnapshotMsg, now: number): void {
    const list = snapshot.meteors ?? [];
    for (const dto of list) {
      const rock = this.rocks.get(dto.id);
      if (!rock) {
        this.rocks.set(dto.id, this.create(dto, now));
        continue;
      }
      rock.dto = dto;
      rock.at = now;
      rock.info.hp = dto.hp;
    }

    if (this.rocks.size === list.length) return;
    const present = new Set(list.map((dto) => dto.id));
    for (const [id, rock] of this.rocks) {
      if (present.has(id)) continue;
      const { x, y, size, name } = rock.info;
      this.gone.set(id, { x, y, size, name, at: now });
      rock.root.destroy({ children: true });
      this.rocks.delete(id);
    }
  }

  update(now: number): void {
    for (const rock of this.rocks.values()) {
      const at = positionAt(rock.dto, (now - rock.at) / 1000);
      rock.info.x = at.x;
      rock.info.y = at.y;
      rock.root.position.set(at.x, at.y);
      rock.body.rotation = (now / 1000) * rock.spin;
      rock.root.alpha = Math.min(1, (now - rock.bornAt) / FADE_IN_MS);
      rock.seen = true;
    }
    for (const [id, gone] of this.gone) {
      if (now - gone.at > LAST_SEEN_MS) this.gone.delete(id);
    }
  }

  /**
   * Предупреждение в мире для опасных камней: пульсирующее красное кольцо и пунктир до точки касания.
   * Кольцо частит, когда до касания меньше секунды с половиной.
   */
  drawThreats(threats: readonly MeteorThreat[], now: number): void {
    const g = this.warn.clear();
    for (const threat of threats) {
      const rock = this.rocks.get(threat.id);
      if (!rock) continue;
      const { x, y, vx, vy, size } = rock.info;
      const period = threat.seconds < 1.5 ? 220 : 480;
      const pulse = (Math.sin((now / period) * Math.PI * 2) + 1) / 2;
      g.circle(x, y, size + 10 + pulse * 6).stroke({ width: 3, color: WARN_COLOR, alpha: 0.45 + 0.5 * pulse });

      const tx = x + vx * threat.seconds;
      const ty = y + vy * threat.seconds;
      dashed(g, x, y, tx, ty);
      g.stroke({ width: 2, color: WARN_COLOR, alpha: 0.7 });
      g.circle(tx, ty, 6).stroke({ width: 2, color: WARN_COLOR, alpha: 0.8 });
    }
  }

  private create(dto: MeteorDto, now: number): Rock {
    const size = meteorSize(this.rules, dto.s);
    const root = new Container();
    const tail = new Graphics();
    const body = new Graphics();
    root.addChild(tail, body);
    this.layer.addChild(root);

    const radius = size.radius;
    const shape = shapeFor(dto.id, radius);
    body
      .poly(shape)
      .fill(ROCK_COLOR)
      .stroke({ width: 1.5, color: ROCK_EDGE })
      .moveTo(-radius * 0.45, -radius * 0.2)
      .lineTo(radius * 0.1, radius * 0.05)
      .lineTo(radius * 0.4, -radius * 0.3)
      .moveTo(-radius * 0.2, radius * 0.45)
      .lineTo(radius * 0.15, radius * 0.25)
      .stroke({ width: Math.max(1.2, radius * 0.09), color: VEIN_COLOR, alpha: 0.85 });

    // Хвост постоянный: камень летит по прямой, направление не меняется до самого конца.
    const speed = Math.hypot(dto.vx, dto.vy);
    if (speed > 0) {
      const ux = -dto.vx / speed;
      const uy = -dto.vy / speed;
      const length = speed * TAIL_SECONDS;
      for (let i = 0; i < TAIL_SEGMENTS; i++) {
        const from = (i / TAIL_SEGMENTS) * length + radius * 0.6;
        const to = ((i + 1) / TAIL_SEGMENTS) * length + radius * 0.6;
        const k = 1 - i / TAIL_SEGMENTS;
        tail
          .moveTo(ux * from, uy * from)
          .lineTo(ux * to, uy * to)
          .stroke({ width: radius * 1.1 * k, color: TAIL_COLOR, alpha: 0.28 * k, cap: 'round' });
      }
    }

    const rock: Rock = {
      root,
      body,
      tail,
      dto,
      at: now,
      bornAt: now,
      spin: spinFor(dto.id),
      info: {
        id: dto.id,
        x: dto.x,
        y: dto.y,
        vx: dto.vx,
        vy: dto.vy,
        size: radius,
        sizeId: dto.s,
        name: size.name,
        hp: dto.hp,
        maxHp: size.hp,
        dead: false,
        protected: false,
      },
      seen: false,
    };
    root.position.set(dto.x, dto.y);
    return rock;
  }
}

/** Пунктир от (x0, y0) до (x1, y1) — путь к точке касания. */
function dashed(g: Graphics, x0: number, y0: number, x1: number, y1: number): void {
  const length = Math.hypot(x1 - x0, y1 - y0);
  if (length < 1) return;
  const ux = (x1 - x0) / length;
  const uy = (y1 - y0) / length;
  for (let d = 0; d < length; d += DASH + GAP) {
    const e = Math.min(length, d + DASH);
    g.moveTo(x0 + ux * d, y0 + uy * d).lineTo(x0 + ux * e, y0 + uy * e);
  }
}
