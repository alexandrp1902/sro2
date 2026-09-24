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
  /** «Стоп» защёлкнут кнопкой (M20c): тяга держится нулём, а направление стик по-прежнему задаёт. */
  private stopped = false;

  /** Держит ли кнопка «Стоп» корабль на месте. */
  get stopLock(): boolean {
    return this.stopped;
  }

  setDirection(dx: number, dy: number): void {
    const length = Math.hypot(dx, dy);
    if (length < 1e-6) return;
    this.dx = dx / length;
    this.dy = dy / length;
  }

  /** Тяга от игрока. При защёлкнутом «Стопе» держится нулём: снять его может только сама кнопка. */
  setThrottle(throttle: number): void {
    this.throttle = this.stopped ? 0 : Math.min(1, Math.max(0, throttle));
  }

  /**
   * Явная команда «ход» с клавиатуры: снимает «Стоп». У клавиатуры горящей кнопки рядом нет, и мёртвая W
   * читалась бы как поломка. Стик замок не снимает нарочно — им крутятся на месте, а кнопка рядом видна.
   */
  thrust(throttle: number): void {
    this.setStop(false);
    this.setThrottle(throttle);
  }

  /** @returns новое состояние замка — по нему горит кнопка */
  toggleStop(): boolean {
    this.setStop(!this.stopped);
    return this.stopped;
  }

  setStop(on: boolean): void {
    this.stopped = on;
    if (on) this.throttle = 0;
  }

  /** Полный сброс: тяга 0 и «Стоп» снят. Прыжок, стыковка, гибель — дальше игрок полетит, а не встанет. */
  release(): void {
    this.setStop(false);
    this.throttle = 0;
  }

  input(): MoveInput {
    return { dx: this.dx, dy: this.dy, throttle: this.throttle };
  }
}
