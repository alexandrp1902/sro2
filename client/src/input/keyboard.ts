import { localVelocity, type HullParams, type ShipState } from '../sim/movement';
import type { Controls } from './controls';

// ПК-управление корпусом (GDD §7): W/↑ — газ, S/↓ — тормоз, A/D/←/→ — поворот.
// Ни газ, ни тормоз не нажаты — корабль держит набранную скорость (круиз). Модель полёта та же, что у стика:
// поворот — это желаемое направление на 90° от носа, поэтому корабль крутится с полным TurnRate.

type Action = 'thrust' | 'brake' | 'left' | 'right';

const ACTIONS: Record<string, Action> = {
  KeyW: 'thrust',
  ArrowUp: 'thrust',
  KeyS: 'brake',
  ArrowDown: 'brake',
  KeyA: 'left',
  ArrowLeft: 'left',
  KeyD: 'right',
  ArrowRight: 'right',
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
const WHEEL_STEP = 0.1;
/** Колесо мыши даёт за щелчок ~100 px — это один шаг тяги; мелкие дельты тачпада копятся. */
const WHEEL_NOTCH_PX = 50;
const WHEEL_ACCUMULATE_PX = 60;

/** Логика клавиатуры без DOM — чтобы её можно было проверить тестами. */
export class KeyboardControls {
  private readonly held = new Set<string>();
  /** На прошлом шаге держали газ или тормоз — после отпускания надо зафиксировать скорость. */
  private throttling = false;
  /** На прошлом шаге поворачивали — после отпускания надо зафиксировать курс. */
  private turning = false;
  private wheelAccum = 0;

  constructor(private readonly controls: Controls) {}

  isGameKey(code: string): boolean {
    return code in ACTIONS || code in THROTTLE_KEYS || code === STOP_KEY;
  }

  keyDown(code: string): void {
    if (code in ACTIONS) {
      this.held.add(code);
    } else if (code in THROTTLE_KEYS) {
      this.setThrottle(THROTTLE_KEYS[code]);
    } else if (code === STOP_KEY) {
      this.setThrottle(0); // §24: стоп, корабль тормозит сам
    }
  }

  keyUp(code: string): void {
    this.held.delete(code);
  }

  blur(): void {
    this.held.clear();
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

  /**
   * Вызывается перед каждым шагом симуляции: поворот и фиксация скорости зависят от текущего курса и скорости.
   * Пока клавиши движения не трогают, управление не перезаписывается — стик продолжает работать.
   */
  apply(ship: ShipState, hull: HullParams): void {
    const thrust = this.isHeld('thrust');
    const brake = this.isHeld('brake');
    const turn = (this.isHeld('right') ? 1 : 0) - (this.isHeld('left') ? 1 : 0);

    if (brake) this.controls.setThrottle(0);
    else if (thrust) this.controls.setThrottle(1);
    else if (this.throttling) {
      // Отпустили газ или тормоз — держим набранную скорость.
      this.controls.setThrottle(Math.max(0, localVelocity(ship).forward) / hull.maxSpeed);
    }

    if (turn !== 0 || this.turning) {
      // Желаемое направление на 90° от носа — полный TurnRate; отпустили — курс замирает на текущем.
      const angle = ship.rot + (turn * Math.PI) / 2;
      this.controls.setDirection(Math.sin(angle), -Math.cos(angle));
    }

    if (thrust || brake || turn !== 0) this.controls.source = 'keyboard';
    this.throttling = thrust || brake;
    this.turning = turn !== 0;
  }

  private setThrottle(throttle: number): void {
    this.controls.setThrottle(throttle);
    this.controls.source = 'keyboard';
  }

  private isHeld(action: Action): boolean {
    for (const code of this.held) if (ACTIONS[code] === action) return true;
    return false;
  }
}

export function bindKeyboard(keyboard: KeyboardControls): void {
  const isTyping = (e: Event) => e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;

  window.addEventListener('keydown', (e) => {
    if (isTyping(e) || e.ctrlKey || e.metaKey || e.altKey || !keyboard.isGameKey(e.code)) return;
    e.preventDefault(); // стрелки не скроллят страницу
    if (!e.repeat) keyboard.keyDown(e.code);
  });
  window.addEventListener('keyup', (e) => keyboard.keyUp(e.code));
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
