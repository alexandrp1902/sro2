const RENDER_INTERVAL_MS = 150;

/** Строка полёта в правом верхнем углу: класс, скорость, тяга. Тап по ней открывает dev-панель. */
export class FlightHud {
  private lastRender = 0;

  constructor(
    private readonly el: HTMLElement,
    onTap: () => void,
  ) {
    el.addEventListener('click', onTap);
  }

  /** @param fuel топливо и бак (GDD §6); null — сервер о них не сообщал (локальный полёт) */
  update(hullName: string, speed: number, throttle: number, fuel: { fuel: number; max: number } | null = null): void {
    const now = performance.now();
    if (now - this.lastRender < RENDER_INTERVAL_MS) return;
    this.lastRender = now;
    const parts = [hullName, String(Math.round(speed)), `тяга ${Math.round(throttle * 100)}%`];
    if (fuel && fuel.max > 0) parts.push(`топливо ${fuel.fuel}/${fuel.max}`);
    this.el.textContent = parts.join(' · ');
  }
}
