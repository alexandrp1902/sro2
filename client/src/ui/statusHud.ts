import type { Connection } from '../net/connection';

const LABELS = { connecting: 'подключение…', online: 'онлайн', offline: 'нет связи' } as const;
const RENDER_INTERVAL_MS = 250;

/** Строка статуса в левом верхнем углу. Тап по ней открывает смену сервера. */
export class StatusHud {
  private lastRender = 0;

  constructor(
    private readonly el: HTMLElement,
    onTap: () => void,
  ) {
    el.addEventListener('click', onTap);
  }

  update(connection: Connection | null, fps: number): void {
    const now = performance.now();
    if (now - this.lastRender < RENDER_INTERVAL_MS) return;
    this.lastRender = now;

    const state = connection?.state ?? 'offline';
    const parts: string[] = [];
    if (!connection) parts.push('сервер не задан');
    else if (state === 'online') parts.push(`${LABELS.online} ${connection.online}`, `пинг ${Math.round(connection.rttMs)} мс`);
    else parts.push(LABELS[state]);
    parts.push(`${Math.round(fps)} FPS`);

    this.el.dataset.state = state;
    this.el.textContent = parts.join(' · ');
  }
}
