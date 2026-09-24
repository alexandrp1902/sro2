// Геометрия и жесты мобильного стика (§27–31). Без DOM — проверяется тестами.

/** §28: мёртвая зона в долях радиуса. */
export const DEAD_ZONE = 0.1;
/** §29: кривая чувствительности — точное медленное движение около центра. */
export const THROTTLE_CURVE = 1.5;
/** Касание, сдвинувшееся меньше, — тап, а не перетаскивание ручки. */
export const TAP_SLOP_PX = 6;
export const TAP_MAX_MS = 250;
/** §31: двойной тап. */
export const DOUBLE_TAP_MS = 300;
export const DOUBLE_TAP_PX = 25;

/** @param distance расстояние ручки от центра в долях радиуса */
export function stickThrottle(distance: number): number {
  const t = Math.min(1, Math.max(0, (distance - DEAD_ZONE) / (1 - DEAD_ZONE)));
  return t ** THROTTLE_CURVE;
}

/** §27: радиус 60–75 px; CSS-пиксели уже не зависят от плотности экрана. */
export function stickRadius(viewportWidth: number, viewportHeight: number): number {
  return Math.min(72, Math.max(56, 0.16 * Math.min(viewportWidth, viewportHeight)));
}

export function clampToCircle(x: number, y: number, radius: number): [number, number] {
  const length = Math.hypot(x, y);
  if (length <= radius) return [x, y];
  return [(x / length) * radius, (y / length) * radius];
}

export class DoubleTapDetector {
  private last: { time: number; x: number; y: number } | null = null;

  /** @returns true, если этот тап завершил двойной */
  tap(time: number, x: number, y: number): boolean {
    const last = this.last;
    if (last && time - last.time <= DOUBLE_TAP_MS && Math.hypot(x - last.x, y - last.y) <= DOUBLE_TAP_PX) {
      this.last = null;
      return true;
    }
    this.last = { time, x, y };
    return false;
  }
}

/** Что делать после события указателя: ничего, взяли ручку, тянут, отпустили, стоп по двойному тапу. */
export type GrabResult = 'ignored' | 'grabbed' | 'dragging' | 'released' | 'stop';

/**
 * Кто сейчас держит стик (§32). Без DOM: залипший pointerId — это и есть «задипавший стик», из-за которого
 * ручка замирала, тяга продолжала уходить на сервер, а новое касание ничего не давало до перезагрузки.
 * Поэтому вся бухгалтерия пальца живёт здесь, где её проверяют тесты, а не в обработчиках событий.
 */
export class StickGrab {
  private pointerId: number | null = null;
  private press = { x: 0, y: 0, time: 0 };
  private dragging = false;
  private captured = false;
  private readonly taps = new DoubleTapDetector();

  get held(): boolean {
    return this.pointerId !== null;
  }

  /** Палец, за которым идёт слежка; null — стик свободен. */
  get id(): number | null {
    return this.pointerId;
  }

  /** Удалось ли забрать указатель себе: без захвата проверить, жив ли он, уже нечем. */
  get capturedPointer(): boolean {
    return this.captured;
  }

  /** @param captured вернул ли setPointerCapture успех */
  down(pointerId: number, x: number, y: number, time: number, captured: boolean): GrabResult {
    if (this.pointerId !== null) return 'ignored'; // стик управляется одним пальцем
    this.pointerId = pointerId;
    this.captured = captured;
    this.press = { x, y, time };
    this.dragging = false;
    return 'grabbed';
  }

  move(pointerId: number, x: number, y: number): GrabResult {
    if (pointerId !== this.pointerId) return 'ignored';
    // Пока касание не сдвинулось на TAP_SLOP_PX, это тап, а не перетаскивание: ручку он не двигает.
    if (!this.dragging && Math.hypot(x - this.press.x, y - this.press.y) <= TAP_SLOP_PX) return 'ignored';
    this.dragging = true;
    return 'dragging';
  }

  /** @param type тип события: только pointerup может оказаться тапом, cancel — нет */
  up(pointerId: number, type: string, time: number, x: number, y: number): GrabResult {
    if (pointerId !== this.pointerId) return 'ignored';
    const dragged = this.dragging;
    this.release();
    const isTap = !dragged && type === 'pointerup' && time - this.press.time <= TAP_MAX_MS;
    return isTap && this.taps.tap(time, x, y) ? 'stop' : 'released';
  }

  /** Палец потерян без события: вкладка ушла в фон, окно потеряло фокус, корабль уничтожен. */
  release(): void {
    this.pointerId = null;
    this.dragging = false;
    this.captured = false;
  }
}

/**
 * Ручка после смены размера экрана (свернулась адресная строка, поворот): сохраняется доля радиуса.
 * Иначе показанный наклон и уже отправленная тяга разъезжаются — ручка рисуется за пределами основания,
 * а корабль летит с прежней тягой.
 */
export function rescaleKnob(x: number, y: number, from: number, to: number): [number, number] {
  if (from <= 0 || to <= 0) return [x, y]; // радиуса ещё не было — пересчитывать нечего
  const k = to / from;
  return [x * k, y * k];
}
