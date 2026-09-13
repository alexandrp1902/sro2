import { DT } from '../sim/movement';

/** Долгая пауза (фоновая вкладка, подвисание) не догоняется шагами — иначе корабль «прыгнет». */
const MAX_STEPS_PER_FRAME = 5;

/**
 * Симуляция идёт фиксированными шагами, как на сервере (20 Гц), а рендер — с частотой экрана
 * (Safari сам выбирает 30/60/120 Гц) и интерполирует между двумя последними шагами.
 */
export class FixedLoop {
  private accumulator = 0;
  private last = performance.now();

  constructor(private readonly onStep: () => void) {
    document.addEventListener('visibilitychange', () => this.reset());
  }

  reset(): void {
    this.accumulator = 0;
    this.last = performance.now();
  }

  /**
   * Время берётся из performance.now(), а не из тикера Pixi: тот зажимает deltaMS.
   * @returns доля пути до следующего шага (0…1) для интерполяции
   */
  advance(now: number): number {
    this.accumulator += (now - this.last) / 1000;
    this.last = now;
    let steps = 0;
    while (this.accumulator >= DT && steps < MAX_STEPS_PER_FRAME) {
      this.onStep();
      this.accumulator -= DT;
      steps++;
    }
    if (this.accumulator >= DT) this.accumulator = 0;
    return this.accumulator / DT;
  }
}
