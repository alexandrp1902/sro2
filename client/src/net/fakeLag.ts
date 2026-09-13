/** Варианты задержки туда-обратно для dev-панели, мс. */
export const LAG_PRESETS = [0, 100, 200, 300] as const;

/** Разброс задержки в одну сторону: ±25%. */
const JITTER = 0.25;

/**
 * Искусственная задержка сети, чтобы на localhost проверить предсказание в условиях tunnel.
 * Порядок сообщений сохраняется, как в TCP: следующее не обгоняет предыдущее.
 */
export class FakeLag {
  rttMs = 0;
  private lastSendAt = 0;
  private lastReceiveAt = 0;

  send(deliver: () => void): void {
    this.lastSendAt = this.schedule(deliver, this.lastSendAt);
  }

  receive(deliver: () => void): void {
    this.lastReceiveAt = this.schedule(deliver, this.lastReceiveAt);
  }

  private schedule(deliver: () => void, lastAt: number): number {
    const now = performance.now();
    if (this.rttMs === 0 && lastAt <= now) {
      deliver();
      return lastAt;
    }
    const oneWay = (this.rttMs / 2) * (1 - JITTER + Math.random() * 2 * JITTER);
    const at = Math.max(lastAt, now + oneWay);
    window.setTimeout(deliver, at - now);
    return at;
  }
}
