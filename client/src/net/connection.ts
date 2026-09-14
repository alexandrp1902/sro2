import { renewSession, sessionToken } from '../util/session';
import { FakeLag } from './fakeLag';
import { PROTOCOL_VERSION, type ClientMessage, type ServerMessage, type SnapshotMsg, type WelcomeMsg } from './protocol';
import { Roster, type RosterEvent } from './roster';

export type ConnectionState = 'connecting' | 'online' | 'offline';

const PING_INTERVAL_MS = 1000;
const FIRST_RETRY_MS = 500;
const MAX_RETRY_MS = 5000;

/** Вкладка ушла в фон: закрываемся сами, чтобы корабль на сервере сразу начал тормозить. */
const CLOSE_HIDDEN = 4000;
/** От сервера: к нашему кораблю подключилась вкладка с той же сессией (вкладку продублировали). */
const CLOSE_REPLACED = 4001;
/** Закрываемся сами: сервер другой версии протокола. */
const CLOSE_VERSION = 4002;

/** WebSocket-соединение с игровым сервером: автопереподключение, сессия, список игроков, пинг. */
export class Connection {
  state: ConnectionState = 'offline';
  playerId = 0;
  rttMs = 0;
  /** Снапшотов в секунду — фактическая частота тиков сервера. */
  snapshotRate = 0;
  lastTick = 0;
  readonly lag = new FakeLag();
  readonly roster = new Roster();
  /** Версия протокола сервера, если она не совпала с нашей: играть нельзя, сервер или страницу нужно обновить. */
  serverVersion: number | null = null;

  onWelcome: ((message: WelcomeMsg) => void) | null = null;
  onConfig: ((message: Extract<ServerMessage, { t: 'config' }>) => void) | null = null;
  onSnapshot: ((message: SnapshotMsg) => void) | null = null;
  onRosterEvents: ((events: RosterEvent[]) => void) | null = null;

  private ws: WebSocket | null = null;
  private pingTimer = 0;
  private retryTimer = 0;
  private retryMs = FIRST_RETRY_MS;
  private rateWindowStart = 0;
  private rateCount = 0;

  constructor(
    readonly url: string,
    private name: string,
    private readonly hullId: () => string,
    private readonly weaponId: () => string,
  ) {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') {
        this.suspend();
      } else if (this.state === 'offline') {
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
      this.send({ t: 'hello', name: this.name, hull: this.hullId(), weapon: this.weaponId(), token: sessionToken() });
      this.ping();
      this.pingTimer = window.setInterval(() => this.ping(), PING_INTERVAL_MS);
    };
    ws.onmessage = (e) => {
      const message = JSON.parse(e.data as string) as ServerMessage;
      this.lag.receive(() => {
        if (this.ws === ws) this.handle(message); // задержанное сообщение старой сессии не нужно
      });
    };
    ws.onclose = (e) => {
      if (this.ws !== ws) return;
      this.drop();
      if (e.code === CLOSE_REPLACED) {
        // Корабль остался у вкладки-дубля, а эта летит дальше новым пилотом.
        renewSession();
        this.retryTimer = window.setTimeout(() => this.connect(), 0);
        return;
      }
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

  /** Смена ника на лету; при следующем подключении он уйдёт в hello. */
  rename(name: string): void {
    this.name = name;
    if (this.state === 'online') this.send({ t: 'name', name });
  }

  /** Фон: iOS всё равно порвёт сокет, но позже — а до тех пор сервер вёл бы корабль по последнему входу. */
  private suspend(): void {
    window.clearTimeout(this.retryTimer);
    const ws = this.ws;
    if (!ws) return;
    this.drop();
    ws.close(CLOSE_HIDDEN, 'hidden');
  }

  private drop(): void {
    window.clearInterval(this.pingTimer);
    this.ws = null;
    this.state = 'offline';
    this.snapshotRate = 0;
  }

  private ping(): void {
    this.send({ t: 'ping', c: performance.now() });
  }

  private handle(message: ServerMessage): void {
    switch (message.t) {
      case 'welcome':
        if ((message.version ?? 0) !== PROTOCOL_VERSION) {
          // Старый сервер не пришлёт корпус и щит и не поймёт огонь — вместо NaN честно говорим, в чём дело.
          // Переподключаемся только при возврате на вкладку: вдруг сервер уже перезапустили.
          this.serverVersion = message.version ?? 0;
          const ws = this.ws;
          this.drop();
          ws?.close(CLOSE_VERSION, 'version');
          break;
        }
        this.serverVersion = null;
        this.playerId = message.id;
        this.state = 'online';
        this.roster.reset();
        this.onWelcome?.(message);
        break;
      case 'pong': {
        const rtt = performance.now() - message.c;
        this.rttMs = this.rttMs === 0 ? rtt : this.rttMs * 0.8 + rtt * 0.2;
        break;
      }
      case 'players': {
        const events = this.roster.update(message.players, this.playerId);
        if (events.length > 0) this.onRosterEvents?.(events);
        break;
      }
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
