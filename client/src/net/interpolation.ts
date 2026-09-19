import { DT, wrapAngle } from '../sim/movement';
import type { AiState, ShipDto, SnapshotMsg } from './protocol';

const TICK_MS = DT * 1000;

/** Задержка интерполяции: тик между снапшотами плюс запас на джиттер, в этих пределах. */
const MIN_DELAY_MS = 75;
const MAX_DELAY_MS = 250;
/** Средний джиттер ×3 покрывает худшие опоздания, в том числе затор TCP за опоздавшим пакетом. */
const JITTER_FACTOR = 3;

/** Сглаживание оценок по снапшотам (доля нового замера). */
const OFFSET_SMOOTHING = 0.05;
const JITTER_SMOOTHING = 0.1;
/** Одиночный выброс (затор TCP) влияет на оценки не сильнее этого. */
const MAX_DEVIATION_MS = 100;
/** Расхождение больше — это новый отсчёт (старт, перезапуск сервера), а не джиттер. */
const RESET_MS = 1000;

/** Часы рендера подтягиваются к цели, меняя свой ход не больше чем на ±10%: без рывков и шагов назад. */
const MAX_RATE_DEVIATION = 0.1;
const CATCH_UP_MS = 500;

/** Буфер кончился — чужой корабль летит по скорости не дольше этого, потом ждёт. */
export const MAX_EXTRAPOLATION_MS = 150;

const BUFFER_SIZE = 40;

/**
 * Часы, по которым рисуются чужие корабли: дробный тик сервера, отстающий от свежих снапшотов на задержку
 * интерполяции. Задержка подстраивается под джиттер сети: по Wi-Fi меньше, через tunnel больше.
 */
export class RenderClock {
  jitterMs = 0;
  delayMs = MIN_DELAY_MS;

  /** Оценка «время прихода − тик·50 мс», мс. */
  private offset = Number.NaN;
  private renderTick = Number.NaN;
  private lastNow = 0;

  onSnapshot(tick: number, now: number): void {
    const sample = now - tick * TICK_MS;
    const deviation = sample - this.offset;
    if (!(Math.abs(deviation) < RESET_MS)) {
      this.offset = sample;
      this.jitterMs = 0;
    } else {
      const clamped = Math.max(-MAX_DEVIATION_MS, Math.min(MAX_DEVIATION_MS, deviation));
      this.jitterMs += (Math.abs(clamped) - this.jitterMs) * JITTER_SMOOTHING;
      this.offset += clamped * OFFSET_SMOOTHING;
    }
    this.delayMs = Math.max(MIN_DELAY_MS, Math.min(MAX_DELAY_MS, TICK_MS + JITTER_FACTOR * this.jitterMs));
  }

  /** @returns тик, который рисовать в момент now; NaN, пока не было снапшотов */
  update(now: number): number {
    if (Number.isNaN(this.offset)) return Number.NaN;
    const target = (now - this.offset - this.delayMs) / TICK_MS;
    if (!(Math.abs(target - this.renderTick) * TICK_MS < RESET_MS)) {
      this.renderTick = target;
    } else {
      const errorMs = (target - this.renderTick) * TICK_MS;
      const rate = Math.max(1 - MAX_RATE_DEVIATION, Math.min(1 + MAX_RATE_DEVIATION, 1 + errorMs / CATCH_UP_MS));
      this.renderTick += ((now - this.lastNow) / TICK_MS) * rate;
    }
    this.lastNow = now;
    return this.renderTick;
  }
}

export interface ShipSample {
  x: number;
  y: number;
  rot: number;
  vx: number;
  vy: number;
  hull: string;
  th: number;
  /** Корпус, щит, пушка, тик респауна (0 — цел), защита до тика (0 — нет): из кадра, до которого дошли часы. */
  hp: number;
  sh: number;
  w: string;
  rt: number;
  pu: number;
  /** Цель пирата (0 — нет) и состояние его ИИ (null — не пират): тоже из кадра, до которого дошли часы. */
  tg: number;
  ai: AiState | null;
  /** Готовится гиперпрыжок: уйдёт в этот тик; 0 — нет. */
  j: number;
  /** Рисуем дальше последнего снапшота — снапшот опоздал. */
  extrapolated: boolean;
}

interface Frame {
  tick: number;
  ships: Map<number, ShipDto>;
}

/** Последние снапшоты и состояние корабля на дробный тик между ними. */
export class SnapshotBuffer {
  private frames: Frame[] = [];

  get latest(): Frame | undefined {
    return this.frames[this.frames.length - 1];
  }

  /** @returns false — снапшот устарел (пришёл не по порядку) */
  push(snapshot: SnapshotMsg): boolean {
    const latest = this.latest;
    if (latest && snapshot.tick <= latest.tick) {
      // Тики пошли заново — сервер перезапустили.
      if (latest.tick - snapshot.tick < RESET_MS / TICK_MS) return false;
      this.frames = [];
    }
    this.frames.push({ tick: snapshot.tick, ships: new Map(snapshot.ships.map((ship) => [ship.id, ship])) });
    if (this.frames.length > BUFFER_SIZE) this.frames.shift();
    return true;
  }

  clear(): void {
    this.frames = [];
  }

  /** @returns null — корабля ещё нет на момент renderTick (только что вошёл) */
  sample(id: number, renderTick: number): ShipSample | null {
    const frames = this.frames;
    if (frames.length === 0) return null;

    let next = frames.findIndex((frame) => frame.tick > renderTick);
    if (next === -1) {
      const last = frames[frames.length - 1];
      const ship = last.ships.get(id);
      if (!ship) return null;
      const ahead = Math.min(renderTick - last.tick, MAX_EXTRAPOLATION_MS / TICK_MS);
      return extrapolate(ship, ahead * DT, ahead > 1e-6);
    }
    if (next === 0) {
      const ship = frames[0].ships.get(id);
      return ship ? extrapolate(ship, 0, false) : null;
    }

    const a = frames[next - 1];
    const b = frames[next];
    const from = a.ships.get(id);
    const to = b.ships.get(id);
    // Нет в следующем снапшоте — корабль ушёл, держим на месте; нет в предыдущем — только вошёл, рано.
    if (!from || !to) return from ? extrapolate(from, 0, false) : null;
    // Респаун — телепорт к станции: не тянем корабль через полкарты, а сразу рисуем на новом месте.
    if ((from.rt ?? 0) > 0 && !to.rt) return extrapolate(to, 0, false);
    const alpha = (renderTick - a.tick) / (b.tick - a.tick);
    return {
      x: from.x + (to.x - from.x) * alpha,
      y: from.y + (to.y - from.y) * alpha,
      rot: from.r + wrapAngle(to.r - from.r) * alpha,
      vx: to.vx,
      vy: to.vy,
      hull: to.hull,
      th: to.th,
      // Дискретное — из кадра a: полоска падает ровно тогда, когда проигрывается выстрел тика b.
      hp: from.hp,
      sh: from.sh,
      w: from.w,
      rt: from.rt ?? 0,
      pu: from.pu ?? 0,
      tg: from.tg ?? 0,
      ai: from.ai ?? null,
      j: from.j ?? 0,
      extrapolated: false,
    };
  }
}

function extrapolate(ship: ShipDto, seconds: number, extrapolated: boolean): ShipSample {
  return {
    x: ship.x + ship.vx * seconds,
    y: ship.y + ship.vy * seconds,
    rot: ship.r,
    vx: ship.vx,
    vy: ship.vy,
    hull: ship.hull,
    th: ship.th,
    hp: ship.hp,
    sh: ship.sh,
    w: ship.w,
    rt: ship.rt ?? 0,
    pu: ship.pu ?? 0,
    tg: ship.tg ?? 0,
    ai: ship.ai ?? null,
    j: ship.j ?? 0,
    extrapolated,
  };
}
