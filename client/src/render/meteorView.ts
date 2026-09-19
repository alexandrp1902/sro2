import { Container, Sprite } from 'pixi.js';
import type { MeteorDto, SnapshotMsg } from '../net/protocol';
import { NO_METEORS, advance, meteorSize, spinFor, type MeteorRules, type Point3 } from '../sim/meteors';
import { spriteSize, texture, type SpriteName } from './sprites';

/** Девять камней с листа: вид выбирается по id, так что у всех игроков камень один и тот же. */
const ROCKS = Array.from({ length: 9 }, (_, i) => `meteors-m${i}` as SpriteName);
/** Картинка чуть больше радиуса попадания: у камня неровный край, круг попадания — по «телу». */
const ROCK_SCALE = 1.15;
/** Кувырок: картинка сплющивается поперёк оси вращения — плоский спрайт читается как тело. */
const TUMBLE_SQUASH = 0.12;
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
  body: Sprite;
  /** Последний снапшот и когда он пришёл: полёт считается от него тем же шагом, что и на сервере. */
  dto: MeteorDto;
  at: number;
  bornAt: number;
  /** Угловая скорость по трём осям: z — вращение картинки, x и y — кувырок. */
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
 * схемой, что и на сервере, — иначе дуга на экране разошлась бы с настоящей. Камень — картинка с листа:
 * крутится и слегка сплющивается в такт кувырку по двум другим осям.
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
      const { body, spin } = rock;
      body.rotation = t * spin.z;
      const base = body.scale.y;
      body.scale.x = base * (1 - TUMBLE_SQUASH * Math.abs(Math.sin(t * spin.x)));
    }
    for (const [id, gone] of this.gone) {
      if (now - gone.at > LAST_SEEN_MS) this.gone.delete(id);
    }
  }

  private create(dto: MeteorDto, now: number): Rock {
    const size = meteorSize(this.rules, dto.s);
    const root = new Container();
    const sprite = ROCKS[dto.id % ROCKS.length];
    const body = new Sprite(texture(sprite));
    const { w, h } = spriteSize(sprite);
    body.anchor.set(0.5);
    body.scale.set((size.radius * 2 * ROCK_SCALE) / Math.max(w, h));
    root.addChild(body);
    this.view.addChild(root);

    const rock: Rock = {
      root,
      body,
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
