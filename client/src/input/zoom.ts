// Масштаб камеры (GDD §42): щипок на телефоне, Ctrl+колесо (и щипок тачпада) и +/− на ПК.

const MIN_ZOOM = 0.6;
const MAX_ZOOM = 1.6;
const KEY_FACTOR = 1.15;
const WHEEL_SENSITIVITY = 0.004;
const WHEEL_MAX_DELTA = 50;

export class Zoom {
  /** На узком вертикальном экране телефона стартуем чуть дальше. */
  value = matchMedia('(pointer: coarse)').matches ? 0.8 : 1;

  private readonly touches = new Map<number, { x: number; y: number }>();
  private pinchDistance = 0;

  constructor(canvas: HTMLElement) {
    // Щипок — два пальца, начавшихся на canvas; касания стика сюда не попадают.
    canvas.addEventListener('pointerdown', (e) => {
      if (e.pointerType !== 'touch') return;
      this.touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
      this.pinchDistance = this.distance();
    });
    canvas.addEventListener('pointermove', (e) => {
      if (!this.touches.has(e.pointerId)) return;
      this.touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
      const distance = this.distance();
      if (this.pinchDistance > 0 && distance > 0) this.set(this.value * (distance / this.pinchDistance));
      this.pinchDistance = distance;
    });
    for (const type of ['pointerup', 'pointercancel'] as const) {
      canvas.addEventListener(type, (e) => {
        this.touches.delete(e.pointerId);
        this.pinchDistance = this.distance();
      });
    }

    window.addEventListener(
      'wheel',
      (e) => {
        if (!e.ctrlKey) return; // без Ctrl колесо меняет тягу (keyboard.ts)
        e.preventDefault(); // иначе зумится страница
        const delta = Math.max(-WHEEL_MAX_DELTA, Math.min(WHEEL_MAX_DELTA, e.deltaY));
        this.set(this.value * Math.exp(-delta * WHEEL_SENSITIVITY));
      },
      { passive: false },
    );
    window.addEventListener('keydown', (e) => {
      if (e.target instanceof HTMLInputElement || e.ctrlKey || e.metaKey) return;
      if (e.code === 'Equal' || e.code === 'NumpadAdd') this.set(this.value * KEY_FACTOR);
      else if (e.code === 'Minus' || e.code === 'NumpadSubtract') this.set(this.value / KEY_FACTOR);
    });
  }

  private set(value: number): void {
    this.value = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, value));
  }

  private distance(): number {
    if (this.touches.size < 2) return 0;
    const [a, b] = [...this.touches.values()];
    return Math.hypot(a.x - b.x, a.y - b.y);
  }
}
