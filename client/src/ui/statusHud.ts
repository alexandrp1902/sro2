import type { Connection } from '../net/connection';
import { PROTOCOL_VERSION } from '../net/protocol';

const LABELS = { connecting: 'подключение…', online: 'онлайн', offline: 'нет связи' } as const;
const RENDER_INTERVAL_MS = 250;

/** Строка статуса в левом верхнем углу. Тап по ней открывает окно «Пилот»: ник и сервер. */
export class StatusHud {
  private lastRender = 0;

  constructor(
    private readonly el: HTMLElement,
    onTap: () => void,
  ) {
    el.addEventListener('click', onTap);
  }

  /** @param braking связи нет, и корабль тормозит — так же, как его копия на сервере */
  update(connection: Connection | null, fps: number, braking: boolean): void {
    const now = performance.now();
    if (now - this.lastRender < RENDER_INTERVAL_MS) return;
    this.lastRender = now;

    const state = connection?.state ?? 'offline';
    const parts: string[] = [];
    if (!connection) parts.push('сервер не задан', 'локальный полёт');
    else if (connection.serverVersion !== null) {
      parts.push(
        connection.serverVersion < PROTOCOL_VERSION ? 'сервер устарел — перезапустите его' : 'клиент устарел — обновите страницу',
      );
    } else if (state === 'online') {
      parts.push(`${LABELS.online} ${connection.roster.onlineCount}`, `пинг ${Math.round(connection.rttMs)} мс`);
    } else parts.push(LABELS[state]);
    if (braking) parts.push('корабль тормозит');
    parts.push(`${Math.round(fps)} FPS`);

    this.el.dataset.state = state;
    this.el.textContent = parts.join(' · ');
  }
}
