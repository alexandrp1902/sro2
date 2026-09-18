import { Container, Graphics } from 'pixi.js';
import type { LootDto, SnapshotMsg } from '../net/protocol';
import { NO_LOOT, rarityColor, type LootRules } from '../sim/loot';
import { TICK_RATE } from '../sim/movement';

/** Радиус предмета в мировых единицах: заметно мельче лёгкого корпуса (16), но пальцем попадаешь. */
const SIZE = 11;
const FADE_IN_MS = 250;
/** Мигание перед исчезновением: альфа гуляет между этими значениями. */
const BLINK_MIN = 0.3;
const BLINK_PERIOD_MS = 550;
/** Исчезнувший предмет помним столько: подбор проигрывается позже, чем сервер убрал предмет. */
const LAST_SEEN_MS = 2000;

/** Предмет, каким он нарисован в этом кадре: для выбора тапом, карточки и тракторного луча. */
export interface LootInfo {
  id: number;
  x: number;
  y: number;
  /** Радиус для попадания тапом — как size у кораблей. */
  size: number;
  /** Идентификатор предмета и количество в стопке. */
  item: string;
  count: number;
}

interface Sample {
  tick: number;
  x: number;
  y: number;
}

interface Drop {
  g: Graphics;
  prev: Sample;
  curr: Sample;
  expiresTick: number;
  bornAt: number;
  info: LootInfo;
  /** Из контейнера: рисуется ящиком, чтобы не путать с только что выпавшими обломками. */
  container: boolean;
  seen: boolean;
}

interface Gone {
  x: number;
  y: number;
  item: string;
  at: number;
}

/**
 * Предметы в космосе (GDD §21). Мировой слой под кораблями.
 * Интерполяция — по тем же часам, что и чужие корабли: сервер шлёт позицию, клиент ничего не предсказывает.
 */
export class LootField {
  readonly view = new Container();

  private rules: LootRules = NO_LOOT;
  private readonly drops = new Map<number, Drop>();
  /** Куда делся предмет: подбор и взрыв рисуются, когда его в снапшоте уже нет. */
  private readonly gone = new Map<number, Gone>();

  setRules(rules: LootRules | undefined): void {
    this.rules = rules ?? NO_LOOT;
    // Редкость могла поменяться на лету — перекрасим при ближайшей перерисовке.
    for (const drop of this.drops.values()) this.paint(drop);
  }

  clear(): void {
    for (const drop of this.drops.values()) drop.g.destroy();
    this.drops.clear();
    this.gone.clear();
  }

  /** Целые предметы из последнего кадра — по ним работает выбор тапом. */
  *visible(): Iterable<LootInfo> {
    for (const drop of this.drops.values()) if (drop.seen) yield drop.info;
  }

  get(id: number): LootInfo | undefined {
    return this.drops.get(id)?.info;
  }

  /** Последняя известная позиция предмета — в том числе уже исчезнувшего. */
  lastSeen(id: number, now: number): { x: number; y: number; item: string } | null {
    const drop = this.drops.get(id);
    if (drop) return { x: drop.info.x, y: drop.info.y, item: drop.info.item };
    const gone = this.gone.get(id);
    if (gone && now - gone.at <= LAST_SEEN_MS) return gone;
    return null;
  }

  push(snapshot: SnapshotMsg, now: number): void {
    const loot = snapshot.loot ?? [];
    for (const dto of loot) {
      const drop = this.drops.get(dto.id);
      if (!drop) {
        this.drops.set(dto.id, this.create(dto, snapshot.tick, now));
        continue;
      }
      // Тот же тик мог прийти дважды (переподключение) — тогда двигать нечего.
      if (snapshot.tick === drop.curr.tick) continue;
      drop.prev = drop.curr;
      drop.curr = { tick: snapshot.tick, x: dto.x, y: dto.y };
    }

    if (this.drops.size === loot.length) return;
    const present = new Set(loot.map((dto) => dto.id));
    for (const [id, drop] of this.drops) {
      if (present.has(id)) continue;
      this.gone.set(id, { x: drop.info.x, y: drop.info.y, item: drop.info.item, at: now });
      drop.g.destroy();
      this.drops.delete(id);
    }
  }

  update(now: number, renderTick: number): void {
    if (Number.isNaN(renderTick)) return; // снапшотов ещё не было — интерполировать не от чего
    const fadeTicks = this.rules.fadeSeconds * TICK_RATE;
    for (const drop of this.drops.values()) {
      const { prev, curr } = drop;
      const span = curr.tick - prev.tick;
      const t = span > 0 ? clamp((renderTick - prev.tick) / span, 0, 1) : 1;
      const x = prev.x + (curr.x - prev.x) * t;
      const y = prev.y + (curr.y - prev.y) * t;
      drop.info.x = x;
      drop.info.y = y;

      drop.g.position.set(x, y);
      drop.g.rotation = drop.info.id * 0.7 + now * 0.0004; // медленное вращение оживляет поле
      drop.seen = true;

      let alpha = Math.min(1, (now - drop.bornAt) / FADE_IN_MS);
      // Последние секунды жизни предмет мигает: понятно, что пора забирать.
      if (fadeTicks > 0 && drop.expiresTick - renderTick <= fadeTicks) {
        const pulse = (Math.sin((now / BLINK_PERIOD_MS) * Math.PI * 2) + 1) / 2;
        alpha *= BLINK_MIN + (1 - BLINK_MIN) * pulse;
      }
      drop.g.alpha = alpha;
    }

    for (const [id, gone] of this.gone) {
      if (now - gone.at > LAST_SEEN_MS) this.gone.delete(id);
    }
  }

  private create(dto: LootDto, tick: number, now: number): Drop {
    const sample = { tick, x: dto.x, y: dto.y };
    const drop: Drop = {
      g: new Graphics(),
      prev: sample,
      curr: sample,
      expiresTick: dto.e,
      bornAt: now,
      info: { id: dto.id, x: dto.x, y: dto.y, size: SIZE, item: dto.i, count: dto.n },
      container: dto.c === true,
      seen: false,
    };
    this.view.addChild(drop.g);
    this.paint(drop);
    return drop;
  }

  /** Обломки — гранёный кристалл цвета редкости (GDD §23), контейнер — ящик: формы различимы на мелком зуме. */
  private paint(drop: Drop): void {
    const color = rarityColor(this.rules, drop.info.item);
    if (drop.container) {
      const s = SIZE * 0.82;
      drop.g
        .clear()
        .rect(-s, -s, s * 2, s * 2)
        .fill({ color, alpha: 0.75 })
        .stroke({ width: 1.5, color })
        .moveTo(-s, 0)
        .lineTo(s, 0)
        .stroke({ width: 1.5, color: 0xffffff, alpha: 0.4 });
      return;
    }
    drop.g
      .clear()
      .poly([0, -SIZE, SIZE * 0.62, 0, 0, SIZE, -SIZE * 0.62, 0])
      .fill({ color, alpha: 0.85 })
      .stroke({ width: 1.5, color, alpha: 1 })
      .poly([0, -SIZE * 0.45, SIZE * 0.28, 0, 0, SIZE * 0.45, -SIZE * 0.28, 0])
      .fill({ color: 0xffffff, alpha: 0.35 });
  }
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}
