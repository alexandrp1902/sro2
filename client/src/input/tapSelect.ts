const TAP_MAX_MS = 300;
const TAP_SLOP_PX = 10;

interface Candidate {
  id: number;
  x: number;
  y: number;
  time: number;
  touch: boolean;
}

/**
 * Тап по кораблю (GDD §8–9): короткое касание canvas одним пальцем без сдвига или клик левой кнопкой.
 * Щипок (zoom.ts) тапом не считается. Стик и кнопка огня — отдельные элементы, их касания сюда не доходят,
 * поэтому одним пальцем можно рулить, а другим выбирать цель.
 */
export class TapSelect {
  private readonly down = new Set<number>();
  private candidate: Candidate | null = null;

  /** @param onTap координаты экрана и было ли это касание пальцем (радиус выбора шире) */
  constructor(canvas: HTMLElement, onTap: (x: number, y: number, touch: boolean) => void) {
    canvas.addEventListener('pointerdown', (e) => {
      this.down.add(e.pointerId);
      if (e.pointerType === 'mouse' && e.button !== 0) return;
      // Второй палец превращает касание в щипок.
      this.candidate =
        this.down.size === 1
          ? { id: e.pointerId, x: e.clientX, y: e.clientY, time: e.timeStamp, touch: e.pointerType !== 'mouse' }
          : null;
    });
    canvas.addEventListener('pointermove', (e) => {
      const c = this.candidate;
      if (c && c.id === e.pointerId && Math.hypot(e.clientX - c.x, e.clientY - c.y) > TAP_SLOP_PX) this.candidate = null;
    });
    canvas.addEventListener('pointerup', (e) => {
      this.down.delete(e.pointerId);
      const c = this.candidate;
      if (!c || c.id !== e.pointerId) return;
      this.candidate = null;
      if (e.timeStamp - c.time <= TAP_MAX_MS) onTap(c.x, c.y, c.touch);
    });
    canvas.addEventListener('pointercancel', (e) => {
      this.down.delete(e.pointerId);
      if (this.candidate?.id === e.pointerId) this.candidate = null;
    });
  }
}
