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
