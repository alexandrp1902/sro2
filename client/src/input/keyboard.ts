import type { Controls } from './controls';

// ПК-управление (§21–25): WASD/стрелки задают направление на экране, тяга — круизная.

const DIRECTIONS: Record<string, readonly [number, number]> = {
  KeyW: [0, -1],
  ArrowUp: [0, -1],
  KeyS: [0, 1],
  ArrowDown: [0, 1],
  KeyA: [-1, 0],
  ArrowLeft: [-1, 0],
  KeyD: [1, 0],
  ArrowRight: [1, 0],
};

const THROTTLE_KEYS: Record<string, number> = {
  Digit1: 0.25,
  Digit2: 0.5,
  Digit3: 0.75,
  Digit4: 1,
  Numpad1: 0.25,
  Numpad2: 0.5,
  Numpad3: 0.75,
  Numpad4: 1,
};

const STOP_KEY = 'KeyX';

/** Диагональ отпускают не одновременно: новый набор клавиш применяется, только если продержался столько. */
export const RELEASE_GRACE_MS = 80;
const WHEEL_STEP = 0.1;
/** Колесо мыши даёт за щелчок ~100 px — это один шаг тяги; мелкие дельты тачпада копятся. */
const WHEEL_NOTCH_PX = 50;
const WHEEL_ACCUMULATE_PX = 60;

/** Логика клавиатуры без DOM — чтобы её можно было проверить тестами. */
export class KeyboardControls {
  private readonly held = new Set<string>();
  /** Последняя ненулевая тяга: к ней возвращается нажатие направления после стопа. */
  private cruise = 1;
  private releaseAt: number | null = null;
  private wheelAccum = 0;

  constructor(private readonly controls: Controls) {}

  isGameKey(code: string): boolean {
    return code in DIRECTIONS || code in THROTTLE_KEYS || code === STOP_KEY;
  }

  keyDown(code: string): void {
    if (code in DIRECTIONS) {
      this.held.add(code);
      this.releaseAt = null;
      this.applyHeld();
      // Круиз (§25): направление включает двигатель на последней установленной тяге.
      if (this.controls.throttle === 0) this.controls.setThrottle(this.cruise);
    } else if (code in THROTTLE_KEYS) {
      this.setThrottle(THROTTLE_KEYS[code]);
    } else if (code === STOP_KEY) {
      this.controls.setThrottle(0); // §24: стоп, направление остаётся
    }
  }

  /** Отпускание ничего не сбрасывает: корабль продолжает круиз (§25). */
  keyUp(code: string, now: number): void {
    if (!this.held.delete(code)) return;
    this.releaseAt = this.heldVector() ? now + RELEASE_GRACE_MS : null;
  }

  update(now: number): void {
    if (this.releaseAt !== null && now >= this.releaseAt) {
      this.releaseAt = null;
      this.applyHeld();
    }
  }

  blur(): void {
    this.held.clear();
    this.releaseAt = null;
  }

  /** @param deltaY в пикселях; вверх (deltaY < 0) — больше тяги. */
  wheel(deltaY: number): void {
    let steps = 0;
    if (Math.abs(deltaY) >= WHEEL_NOTCH_PX) {
      steps = -Math.sign(deltaY);
    } else {
      this.wheelAccum += deltaY;
      if (Math.abs(this.wheelAccum) >= WHEEL_ACCUMULATE_PX) {
        steps = -Math.sign(this.wheelAccum);
        this.wheelAccum = 0;
      }
    }
    if (steps !== 0) this.setThrottle(Math.round((this.controls.throttle + steps * WHEEL_STEP) * 20) / 20);
  }

  private setThrottle(throttle: number): void {
    this.controls.setThrottle(throttle);
    if (this.controls.throttle > 0) this.cruise = this.controls.throttle;
  }

  private applyHeld(): void {
    const vector = this.heldVector();
    if (vector) this.controls.setDirection(vector[0], vector[1]);
  }

  /** Сумма зажатых направлений; противоположные клавиши гасят друг друга. */
  private heldVector(): [number, number] | null {
    let x = 0;
    let y = 0;
    for (const code of this.held) {
      x += DIRECTIONS[code][0];
      y += DIRECTIONS[code][1];
    }
    x = Math.sign(x);
    y = Math.sign(y);
    return x === 0 && y === 0 ? null : [x, y];
  }
}

export function bindKeyboard(keyboard: KeyboardControls): void {
  const isTyping = (e: Event) => e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;

  window.addEventListener('keydown', (e) => {
    if (isTyping(e) || e.ctrlKey || e.metaKey || e.altKey || !keyboard.isGameKey(e.code)) return;
    e.preventDefault(); // стрелки не скроллят страницу
    if (!e.repeat) keyboard.keyDown(e.code);
  });
  window.addEventListener('keyup', (e) => keyboard.keyUp(e.code, performance.now()));
  window.addEventListener('blur', () => keyboard.blur());
  window.addEventListener(
    'wheel',
    (e) => {
      if (e.ctrlKey) return; // Ctrl+колесо и щипок тачпада — зум (zoom.ts)
      e.preventDefault();
      const scale = e.deltaMode === WheelEvent.DOM_DELTA_LINE ? 33 : e.deltaMode === WheelEvent.DOM_DELTA_PAGE ? 400 : 1;
      keyboard.wheel(e.deltaY * scale);
    },
    { passive: false },
  );
}
