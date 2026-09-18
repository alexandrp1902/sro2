import { Container, Graphics } from 'pixi.js';
import type { MeteorDto, SnapshotMsg } from '../net/protocol';
import {
  NO_METEORS,
  advance,
  clipHalfPlane,
  meteorSize,
  rockPoints,
  rotatePoints,
  silhouette,
  spinFor,
  type MeteorRules,
  type Point3,
} from '../sim/meteors';

/** Дневная сторона камня. */
const BASE_COLOR = 0x6b5f52;
/**
 * Терминатор — полосы ночной стороны: силуэт режется прямой поперёк направления на свет, каждая следующая
 * полоса дальше от света и темнее. Ими камень и читается как тело, а не как плоский многоугольник.
 * [насколько прямая сдвинута от центра в долях радиуса, цвет, прозрачность]
 */
const TERMINATOR: readonly (readonly [number, number, number])[] = [
  [-0.35, 0x574c43, 0.55],
  [0, 0x453c35, 0.55],
  [0.3, 0x342d28, 0.6],
  [0.6, 0x241f1b, 0.6],
];
/** Светлая макушка у освещённого края. */
const HIGHLIGHT_COLOR = 0xbdac96;
const EDGE_COLOR = 0x241f1b;
/** Рыжие прожилки: камень читается на тёмном небе и не путается с кораблём. */
const VEIN_COLOR = 0xc9722f;
const CRATER_COLOR = 0x37302a;
const CRATER_LIT = 0xb2a290;
const TAIL_COLOR = 0xffb27a;
/** Хвост — путь за столько секунд: по нему видно, куда камень несёт. */
const TAIL_SECONDS = 0.3;
const TAIL_SEGMENTS = 7;
/** Свет падает отсюда: та сторона камня светлее, противоположная уходит в тень. */
const LIGHT = { x: -0.55, y: -0.83 };
const FADE_IN_MS = 300;
const LAST_SEEN_MS = 2000;

/** Метеорит, каким он нарисован в этом кадре: для выбора целью, карточки и эффектов. */
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

interface Rock {
  root: Container;
  body: Graphics;
  tail: Graphics;
  /** Последний снапшот и когда он пришёл: полёт считается от него тем же шагом, что и на сервере. */
  dto: MeteorDto;
  at: number;
  bornAt: number;
  /** Точки поверхности в своих осях и угловая скорость по трём осям. */
  shape: Point3[];
  spin: Point3;
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
 * Метеориты (M5b). Мировой слой над лутом, под кораблями. Положение считается от последнего снапшота той же
 * схемой, что и на сервере, — иначе дуга на экране разошлась бы с настоящей. Камень кувыркается по трём осям:
 * силуэт пересобирается каждый кадр из повёрнутого облака точек, поэтому виден объём, а не вращение картинки.
 */
export class MeteorField {
  readonly view = new Container();
  private rules: MeteorRules = NO_METEORS;
  private readonly rocks = new Map<number, Rock>();
  private readonly gone = new Map<number, Gone>();

  setRules(rules: MeteorRules | undefined): void {
    this.rules = rules ?? NO_METEORS;
  }

  clear(): void {
    for (const rock of this.rocks.values()) rock.root.destroy({ children: true });
    this.rocks.clear();
    this.gone.clear();
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
      const state = advance(this.rules, rock.dto, (now - rock.at) / 1000);
      rock.info.x = state.x;
      rock.info.y = state.y;
      rock.info.vx = state.vx;
      rock.info.vy = state.vy;
      rock.root.position.set(state.x, state.y);
      rock.root.alpha = Math.min(1, (now - rock.bornAt) / FADE_IN_MS);
      rock.seen = true;

      const t = now / 1000;
      this.paint(rock, rotatePoints(rock.shape, t * rock.spin.x, t * rock.spin.y, t * rock.spin.z));
      // Хвост смотрит назад по курсу, а курс тяготение всё время меняет — значит, и хвост каждый кадр.
      this.paintTail(rock, state.vx, state.vy);
    }
    for (const [id, gone] of this.gone) {
      if (now - gone.at > LAST_SEEN_MS) this.gone.delete(id);
    }
  }

  /**
   * Тело камня. Силуэт — выпуклая оболочка повёрнутого облака, объём — несколько вложенных слоёв, сдвинутых
   * к свету: получается выпуклость, а не плоский многоугольник. Кратеры и прожилки живут на видимой половине
   * (z > 0) и сплющиваются к краю, поэтому при кувыркании читается настоящий поворот тела.
   */
  private paint(rock: Rock, points: Point3[]): void {
    const radius = rock.info.size;
    const g = rock.body.clear();
    const outline = silhouette(points);
    if (outline.length < 6) return;

    g.poly(outline).fill(BASE_COLOR).stroke({ width: 1.2, color: EDGE_COLOR, alpha: 0.85 });
    // Ночная сторона: тот же силуэт, обрезанный прямой поперёк направления на свет.
    for (const [offset, color, alpha] of TERMINATOR) {
      const part = clipHalfPlane(outline, -LIGHT.x, -LIGHT.y, radius * offset);
      if (part.length >= 6) g.poly(part).fill({ color, alpha });
    }
    // Светлая макушка у освещённого края — пара вложенных эллипсов вместо размытия.
    for (const scale of [0.52, 0.32]) {
      g.ellipse(LIGHT.x * radius * 0.4, LIGHT.y * radius * 0.4, radius * scale, radius * scale * 0.85).fill({
        color: HIGHLIGHT_COLOR,
        alpha: 0.14,
      });
    }

    for (let i = 0; i < points.length; i += 4) {
      const p = points[i];
      if (p.z <= radius * 0.15) continue;
      // Кратер сплющивается к краю диска: ось поперёк направления на край — это и есть наклон поверхности.
      const depth = p.z / radius;
      const away = Math.hypot(p.x, p.y) / radius;
      const tilt = Math.max(0.18, Math.sqrt(Math.max(0, 1 - away * away)));
      const size = radius * 0.17 * (0.6 + depth * 0.7);
      g.ellipse(p.x * 0.78, p.y * 0.78, size, size * tilt)
        .fill({ color: CRATER_COLOR, alpha: 0.4 })
        .ellipse(p.x * 0.78 + LIGHT.x * size * 0.35, p.y * 0.78 + LIGHT.y * size * 0.35, size * 0.6, size * 0.6 * tilt)
        .fill({ color: CRATER_LIT, alpha: 0.22 });
    }

    for (let i = 2; i < points.length - 2; i += 9) {
      const a = points[i];
      const b = points[i + 2];
      if (a.z <= radius * 0.2 || b.z <= radius * 0.2) continue;
      g.moveTo(a.x * 0.74, a.y * 0.74)
        .lineTo(b.x * 0.74, b.y * 0.74)
        .stroke({ width: Math.max(0.8, radius * 0.045), color: VEIN_COLOR, alpha: 0.4 });
    }
  }

  /** Хвост: сужающийся шлейф назад по курсу — по нему видно, куда камень несёт, ещё до всякого расчёта. */
  private paintTail(rock: Rock, vx: number, vy: number): void {
    const g = rock.tail.clear();
    const speed = Math.hypot(vx, vy);
    if (speed < 1) return;
    const radius = rock.info.size;
    const ux = -vx / speed;
    const uy = -vy / speed;
    const px = -uy;
    const py = ux;
    const length = Math.min(speed * TAIL_SECONDS, radius * 9);
    const start = radius * 0.75;

    for (let i = 0; i < TAIL_SEGMENTS; i++) {
      const t0 = i / TAIL_SEGMENTS;
      const t1 = (i + 1) / TAIL_SEGMENTS;
      const d0 = start + t0 * length;
      const d1 = start + t1 * length;
      const w0 = radius * 0.46 * (1 - t0) ** 1.5;
      const w1 = radius * 0.46 * (1 - t1) ** 1.5;
      g.poly([
        ux * d0 + px * w0,
        uy * d0 + py * w0,
        ux * d1 + px * w1,
        uy * d1 + py * w1,
        ux * d1 - px * w1,
        uy * d1 - py * w1,
        ux * d0 - px * w0,
        uy * d0 - py * w0,
      ]).fill({ color: TAIL_COLOR, alpha: 0.3 * (1 - t0) ** 1.3 });
    }
  }

  private create(dto: MeteorDto, now: number): Rock {
    const size = meteorSize(this.rules, dto.s);
    const root = new Container();
    const tail = new Graphics();
    const body = new Graphics();
    root.addChild(tail, body);
    this.view.addChild(root);

    const rock: Rock = {
      root,
      body,
      tail,
      dto,
      at: now,
      bornAt: now,
      shape: rockPoints(dto.id, size.radius),
      spin: spinFor(dto.id),
      info: {
        id: dto.id,
        x: dto.x,
        y: dto.y,
        vx: dto.vx,
        vy: dto.vy,
        size: size.radius,
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
