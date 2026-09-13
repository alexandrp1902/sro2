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

  update(hullName: string, speed: number, throttle: number): void {
    const now = performance.now();
    if (now - this.lastRender < RENDER_INTERVAL_MS) return;
    this.lastRender = now;
    this.el.textContent = `${hullName} · ${Math.round(speed)} · тяга ${Math.round(throttle * 100)}%`;
  }
}
