import type { Controls } from './controls';
import { revealOnTouch } from './gestures';
import { StickGrab, clampToCircle, rescaleKnob, stickRadius, stickThrottle } from './stickMath';

/** §30: анимация возврата ручки после двойного тапа. */
const RETURN_MS = 120;

/**
 * «Залипающий» стик (§5, §32): ручка остаётся там, где её отпустили, и корабль летит без пальца на экране.
 * Направление — от центра к ручке, тяга — от расстояния. Двойной тап по стику — стоп.
 *
 * Движение и отпускание слушаются на окне, а не на самом стике: палец отпускают над панелями HUD, за краем
 * экрана и вообще вне вкладки. Раньше такое событие терялось, палец оставался зажатым навсегда, и стик
 * замирал с уже отправленной тягой — корабль улетал, а управление возвращала только перезагрузка.
 */
export class Stick {
  private radius = 0;
  private knob = { x: 0, y: 0 };
  private returning: { fromX: number; fromY: number; start: number } | null = null;
  private readonly grab = new StickGrab();
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
    revealOnTouch(root);

    root.addEventListener('pointerdown', (e) => this.onDown(e));
    window.addEventListener('pointermove', (e) => this.onMove(e));
    // Пункт управления, звонок, свайп с края дают pointercancel — это обычное отпускание (§32).
    for (const type of ['pointerup', 'pointercancel', 'lostpointercapture'] as const) {
      window.addEventListener(type, (e) => this.onUp(e));
    }
    // Палец пропал без события: вкладка ушла в фон, окно потеряло фокус. Ручку и тягу при этом не трогаем —
    // стик залипающий, и потеря фокуса не должна останавливать корабль в бою.
    window.addEventListener('blur', () => this.grab.release());
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') this.grab.release();
    });
  }

  /** Стоп как после двойного тапа — корабль уничтожен, и после респауна залипший стик не должен унести его. */
  reset(): void {
    this.grab.release();
    this.snapToCenter();
  }

  private resize(): void {
    const was = this.radius;
    this.radius = stickRadius(window.innerWidth, window.innerHeight);
    this.root.style.setProperty('--stick-r', `${this.radius}px`);
    // Кнопки боя справа выравниваются по центру стика — им нужен тот же радиус.
    document.documentElement.style.setProperty('--stick-r', `${this.radius}px`);
    // Доля радиуса сохраняется, поэтому отправленная тяга остаётся верной и посылать её заново не нужно.
    [this.knob.x, this.knob.y] = rescaleKnob(this.knob.x, this.knob.y, was, this.radius);
    this.returning = null; // анимация возврата считала пиксели прежнего радиуса
    this.render();
  }

  private onDown(e: PointerEvent): void {
    e.preventDefault(); // до отказа по второму пальцу: иначе iOS заберёт его себе под системный жест
    // Палец, за которым мы следим, мог исчезнуть вместе со своим событием отпускания. Завершившийся
    // указатель отпускает захват сам, поэтому «захвата больше нет» — это «того пальца нет»: берём стик себе.
    if (this.grab.held && this.stale()) this.grab.release();
    if (this.grab.held) return; // стик управляется одним пальцем
    this.grab.down(e.pointerId, e.clientX, e.clientY, e.timeStamp, this.capture(e.pointerId));
  }

  /** Стик держит палец, которого уже нет: захват не удался или его успели потерять. */
  private stale(): boolean {
    const id = this.grab.id;
    return id === null || !this.grab.capturedPointer || !this.root.hasPointerCapture(id);
  }

  /** @returns удалось ли забрать указатель себе: палец, ушедший за пределы стика, продолжает им управлять (§32) */
  private capture(pointerId: number): boolean {
    try {
      this.root.setPointerCapture(pointerId);
      return true;
    } catch {
      return false; // указатель уже исчез
    }
  }

  private onMove(e: PointerEvent): void {
    if (this.grab.move(e.pointerId, e.clientX, e.clientY) !== 'dragging') return;
    this.returning = null;
    const rect = this.base.getBoundingClientRect();
    this.setKnob(e.clientX - (rect.left + rect.width / 2), e.clientY - (rect.top + rect.height / 2));
  }

  private onUp(e: PointerEvent): void {
    // Тап ручку не двигает, поэтому первый тап двойного тапа не дёргает корабль.
    if (this.grab.up(e.pointerId, e.type, e.timeStamp, e.clientX, e.clientY) === 'stop') this.snapToCenter();
  }

  private setKnob(x: number, y: number): void {
    [x, y] = clampToCircle(x, y, this.radius);
    this.knob = { x, y };
    const throttle = stickThrottle(Math.hypot(x, y) / this.radius);
    if (throttle > 0) this.controls.setDirection(x, y); // в мёртвой зоне направление не меняется
    this.controls.setThrottle(throttle);
    this.controls.source = 'stick';
    this.render();
  }

  /**
   * §30: тяга обнуляется сразу, ручка возвращается анимацией.
   *
   * Разовый стоп, а не замок: замком владеет кнопка «Стоп», у которой видно состояние. Жест, молча
   * защёлкивающий тягу в нуль, был бы неотличим от залипшего стика.
   */
  private snapToCenter(): void {
    this.controls.setThrottle(0);
    this.controls.source = 'stick';
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
