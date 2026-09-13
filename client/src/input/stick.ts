import type { Controls } from './controls';
import {
  DoubleTapDetector,
  TAP_MAX_MS,
  TAP_SLOP_PX,
  clampToCircle,
  stickRadius,
  stickThrottle,
} from './stickMath';

/** §30: анимация возврата ручки после двойного тапа. */
const RETURN_MS = 120;

/**
 * «Залипающий» стик (§5, §32): ручка остаётся там, где её отпустили, и корабль летит без пальца на экране.
 * Направление — от центра к ручке, тяга — от расстояния. Двойной тап по стику — стоп.
 */
export class Stick {
  private radius = 64;
  private knob = { x: 0, y: 0 };
  private pointerId: number | null = null;
  private press = { x: 0, y: 0, time: 0 };
  private dragging = false;
  private returning: { fromX: number; fromY: number; start: number } | null = null;
  private readonly taps = new DoubleTapDetector();
  private readonly base: HTMLElement;
  private readonly handle: HTMLElement;

  constructor(
    private readonly root: HTMLElement,
    private readonly controls: Controls,
  ) {
    this.base = root.querySelector('.stick-base')!;
    this.handle = root.querySelector('.stick-handle')!;

    this.resize();
    window.addEventListener('resize', () => this.resize());

    // На ПК стик не нужен: показываем на сенсорных экранах или после первого касания.
    if (matchMedia('(pointer: coarse)').matches) root.hidden = false;
    window.addEventListener('pointerdown', (e) => {
      if (e.pointerType === 'touch') root.hidden = false;
    });

    root.addEventListener('pointerdown', (e) => this.onDown(e));
    root.addEventListener('pointermove', (e) => this.onMove(e));
    // Пункт управления, звонок, свайп с края дают pointercancel — это обычное отпускание (§32).
    for (const type of ['pointerup', 'pointercancel', 'lostpointercapture'] as const) {
      root.addEventListener(type, (e) => this.onUp(e));
    }
  }

  private resize(): void {
    this.radius = stickRadius(window.innerWidth, window.innerHeight);
    this.root.style.setProperty('--stick-r', `${this.radius}px`);
    this.render();
  }

  private onDown(e: PointerEvent): void {
    if (this.pointerId !== null) return; // стик управляется одним пальцем
    e.preventDefault();
    this.pointerId = e.pointerId;
    try {
      // Палец, ушедший за пределы стика, продолжает им управлять (§32).
      this.root.setPointerCapture(e.pointerId);
    } catch {
      // указатель уже исчез
    }
    this.press = { x: e.clientX, y: e.clientY, time: e.timeStamp };
    this.dragging = false;
  }

  private onMove(e: PointerEvent): void {
    if (e.pointerId !== this.pointerId) return;
    if (!this.dragging && Math.hypot(e.clientX - this.press.x, e.clientY - this.press.y) <= TAP_SLOP_PX) return;
    this.dragging = true;
    this.returning = null;
    const rect = this.base.getBoundingClientRect();
    this.setKnob(e.clientX - (rect.left + rect.width / 2), e.clientY - (rect.top + rect.height / 2));
  }

  private onUp(e: PointerEvent): void {
    if (e.pointerId !== this.pointerId) return;
    this.pointerId = null;
    // Тап ручку не двигает, поэтому первый тап двойного тапа не дёргает корабль.
    const isTap = !this.dragging && e.type === 'pointerup' && e.timeStamp - this.press.time <= TAP_MAX_MS;
    if (isTap && this.taps.tap(e.timeStamp, e.clientX, e.clientY)) this.snapToCenter();
  }

  private setKnob(x: number, y: number): void {
    [x, y] = clampToCircle(x, y, this.radius);
    this.knob = { x, y };
    const throttle = stickThrottle(Math.hypot(x, y) / this.radius);
    if (throttle > 0) this.controls.setDirection(x, y); // в мёртвой зоне направление не меняется
    this.controls.setThrottle(throttle);
    this.render();
  }

  /** §30: тяга обнуляется сразу, ручка возвращается анимацией. */
  private snapToCenter(): void {
    this.controls.setThrottle(0);
    const returning = { fromX: this.knob.x, fromY: this.knob.y, start: performance.now() };
    this.returning = returning;
    const animate = (now: number) => {
      if (this.returning !== returning) return; // игрок снова взялся за стик
      const t = Math.min(1, (now - returning.start) / RETURN_MS);
      const k = 1 - (1 - t) * (1 - t);
      this.knob = { x: returning.fromX * (1 - k), y: returning.fromY * (1 - k) };
      this.render();
      if (t < 1) requestAnimationFrame(animate);
      else this.returning = null;
    };
    requestAnimationFrame(animate);
  }

  private render(): void {
    this.handle.style.transform = `translate(${this.knob.x}px, ${this.knob.y}px)`;
    this.root.style.setProperty('--stick-power', String(Math.hypot(this.knob.x, this.knob.y) / this.radius));
  }
}
