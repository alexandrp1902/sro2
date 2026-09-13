import { FakeLag } from './fakeLag';
import type { ClientMessage, ServerMessage, SnapshotMsg, WelcomeMsg } from './protocol';

export type ConnectionState = 'connecting' | 'online' | 'offline';

const PING_INTERVAL_MS = 1000;
const FIRST_RETRY_MS = 500;
const MAX_RETRY_MS = 5000;

/** WebSocket-соединение с игровым сервером: автопереподключение, замер пинга и частоты снапшотов. */
export class Connection {
  state: ConnectionState = 'offline';
  playerId = 0;
  online = 0;
  rttMs = 0;
  /** Снапшотов в секунду — фактическая частота тиков сервера. */
  snapshotRate = 0;
  lastTick = 0;
  readonly lag = new FakeLag();

  onWelcome: ((message: WelcomeMsg) => void) | null = null;
  onConfig: ((message: Extract<ServerMessage, { t: 'config' }>) => void) | null = null;
  onSnapshot: ((message: SnapshotMsg) => void) | null = null;

  private ws: WebSocket | null = null;
  private pingTimer = 0;
  private retryTimer = 0;
  private retryMs = FIRST_RETRY_MS;
  private rateWindowStart = 0;
  private rateCount = 0;

  constructor(
    readonly url: string,
    private readonly name: string,
    private readonly hullId: () => string,
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
      this.send({ t: 'hello', name: this.name, hull: this.hullId() });
      this.ping();
      this.pingTimer = window.setInterval(() => this.ping(), PING_INTERVAL_MS);
    };
    ws.onmessage = (e) => {
      const message = JSON.parse(e.data as string) as ServerMessage;
      this.lag.receive(() => {
        if (this.ws === ws) this.handle(message); // задержанное сообщение старой сессии не нужно
      });
    };
    ws.onclose = () => {
      if (this.ws !== ws) return;
      window.clearInterval(this.pingTimer);
      this.ws = null;
      this.state = 'offline';
      this.snapshotRate = 0;
      this.retryTimer = window.setTimeout(() => this.connect(), this.retryMs);
      this.retryMs = Math.min(this.retryMs * 2, MAX_RETRY_MS);
    };
  }

  send(message: ClientMessage): void {
    const ws = this.ws;
    if (!ws) return;
    const text = JSON.stringify(message);
    this.lag.send(() => {
      if (ws.readyState === WebSocket.OPEN) ws.send(text);
    });
  }

  private ping(): void {
    this.send({ t: 'ping', c: performance.now() });
  }

  private handle(message: ServerMessage): void {
    switch (message.t) {
      case 'welcome':
        this.playerId = message.id;
        this.state = 'online';
        this.onWelcome?.(message);
        break;
      case 'pong': {
        const rtt = performance.now() - message.c;
        this.rttMs = this.rttMs === 0 ? rtt : this.rttMs * 0.8 + rtt * 0.2;
        break;
      }
      case 'online':
        this.online = message.count;
        break;
      case 'config':
        this.onConfig?.(message);
        break;
      case 'snapshot':
        this.lastTick = message.tick;
        this.countSnapshot();
        this.onSnapshot?.(message);
        break;
    }
  }

  private countSnapshot(): void {
    const now = performance.now();
    if (this.rateCount === 0) this.rateWindowStart = now;
    this.rateCount++;
    const elapsed = now - this.rateWindowStart;
    if (elapsed >= 1000) {
      this.snapshotRate = ((this.rateCount - 1) * 1000) / elapsed;
      this.rateCount = 0;
    }
  }
}
