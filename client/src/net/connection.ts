import type { ClientMessage, ServerMessage } from './protocol';

export type ConnectionState = 'connecting' | 'online' | 'offline';

const PING_INTERVAL_MS = 1000;
const FIRST_RETRY_MS = 500;
const MAX_RETRY_MS = 5000;

/** WebSocket-соединение с игровым сервером: автопереподключение и замер пинга. */
export class Connection {
  state: ConnectionState = 'offline';
  playerId = 0;
  online = 0;
  rttMs = 0;

  private ws: WebSocket | null = null;
  private pingTimer = 0;
  private retryTimer = 0;
  private retryMs = FIRST_RETRY_MS;

  constructor(
    readonly url: string,
    private readonly name: string,
  ) {
    // iOS рвёт сокеты фоновых вкладок — при возвращении в игру переподключаемся сразу.
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible' && this.state === 'offline') {
        window.clearTimeout(this.retryTimer);
        this.retryMs = FIRST_RETRY_MS;
        this.connect();
      }
    });
  }

  connect(): void {
    this.state = 'connecting';
    const ws = new WebSocket(this.url);
    this.ws = ws;

    ws.onopen = () => {
      this.retryMs = FIRST_RETRY_MS;
      this.send({ t: 'hello', name: this.name });
      this.ping();
      this.pingTimer = window.setInterval(() => this.ping(), PING_INTERVAL_MS);
    };
    ws.onmessage = (e) => this.handle(JSON.parse(e.data as string) as ServerMessage);
    ws.onclose = () => {
      if (this.ws !== ws) return;
      window.clearInterval(this.pingTimer);
      this.ws = null;
      this.state = 'offline';
      this.retryTimer = window.setTimeout(() => this.connect(), this.retryMs);
      this.retryMs = Math.min(this.retryMs * 2, MAX_RETRY_MS);
    };
  }

  send(message: ClientMessage): void {
    if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(message));
  }

  private ping(): void {
    this.send({ t: 'ping', c: performance.now() });
  }

  private handle(message: ServerMessage): void {
    switch (message.t) {
      case 'welcome':
        this.playerId = message.id;
        this.state = 'online';
        break;
      case 'pong': {
        const rtt = performance.now() - message.c;
        this.rttMs = this.rttMs === 0 ? rtt : this.rttMs * 0.8 + rtt * 0.2;
        break;
      }
      case 'online':
        this.online = message.count;
        break;
    }
  }
}
