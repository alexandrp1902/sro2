import type { MoveInput } from '../sim/movement';

/**
 * Текущее управление кораблём (§1): желаемое направление на экране и тяга.
 * Пишут клавиатура и стик — кто последний, тот и прав.
 */
export class Controls {
  /** Последнее ненулевое направление: при нулевой тяге корабль не разворачивается «в никуда». */
  dx = 0;
  dy = -1;
  throttle = 0;
  /** Кто управлял последним: указатель желаемого направления (§26) нужен только стику. */
  source: 'keyboard' | 'stick' = 'stick';

  setDirection(dx: number, dy: number): void {
    const length = Math.hypot(dx, dy);
    if (length < 1e-6) return;
    this.dx = dx / length;
    this.dy = dy / length;
  }

  setThrottle(throttle: number): void {
    this.throttle = Math.min(1, Math.max(0, throttle));
  }

  input(): MoveInput {
    return { dx: this.dx, dy: this.dy, throttle: this.throttle };
  }
}
