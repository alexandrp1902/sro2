import { FakeLag } from './fakeLag';
import {
  PROTOCOL_VERSION,
  type AccountMsg,
  type BountyMsg,
  type CargoMsg,
  type MarketMsg,
  type ClientMessage,
  type DeniedCode,
  type HangarMsg,
  type InvasionMsg,
  type MissionsMsg,
  type NoticeMsg,
  type PartyEventMsg,
  type PartyInviteMsg,
  type PartyStateMsg,
  type SosMsg,
  type ServerMessage,
  type SnapshotMsg,
  type WelcomeMsg,
} from './protocol';
import { Roster, type RosterEvent } from './roster';
import { SnapshotDecoder } from './snapshotCodec';

export type ConnectionState = 'connecting' | 'online' | 'offline';

/** Вход по нику и паролю (свободный ник заводит аккаунт) или по ключу устройства. */
export type Credentials = { name: string; password: string } | { key: string };

const PING_INTERVAL_MS = 1000;
const FIRST_RETRY_MS = 500;
const MAX_RETRY_MS = 5000;

/** Вкладка ушла в фон: закрываемся сами, чтобы корабль на сервере сразу начал тормозить. */
const CLOSE_HIDDEN = 4000;
/** От сервера: корабль забрало другое соединение того же аккаунта — другое устройство или вкладка. */
const CLOSE_REPLACED = 4001;
/** Закрываемся сами: сервер другой версии протокола. */
const CLOSE_VERSION = 4002;
/** От сервера: вход отклонён, причина пришла в denied. */
const CLOSE_DENIED = 4003;
/** Закрываемся сами: пилот вышел или входит под другим ником. */
const CLOSE_LOGOUT = 1000;

/** WebSocket-соединение с игровым сервером: вход, автопереподключение, список игроков, пинг. */
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
  /** Корабль забрало другое устройство: сами не переподключаемся, иначе устройства отбирали бы его друг у друга. */
  replaced = false;
  /** Ник аккаунта, под которым вошли. */
  accountName = '';
  /** Пилотов на связи во всей галактике; ростер — только своей системы. */
  totalOnline = 0;
  /** Сколько байт снапшотов пришло — для dev-панели. */
  snapshotBytes = 0;

  /**
   * transfer — welcome пришёл по тому же сокету, что и прошлый: корабль перелетел в другую систему (прыжок или
   * возврат домой после гибели). Буфер входов на сервере продолжает счёт, поэтому и клиент нумерует входы дальше.
   */
  onWelcome: ((message: WelcomeMsg, transfer: boolean) => void) | null = null;
  onConfig: ((message: Extract<ServerMessage, { t: 'config' }>) => void) | null = null;
  onSnapshot: ((message: SnapshotMsg) => void) | null = null;
  onRosterEvents: ((events: RosterEvent[]) => void) | null = null;
  onCargo: ((message: CargoMsg) => void) | null = null;
  /** Живые цены станции (M12) — приходят, только пока пилот в доке. */
  onMarket: ((message: MarketMsg) => void) | null = null;
  onNotice: ((message: NoticeMsg) => void) | null = null;
  onAccount: ((message: AccountMsg) => void) | null = null;
  onDenied: ((code: DeniedCode) => void) | null = null;
  onHangar: ((message: HangarMsg) => void) | null = null;
  onMissions: ((message: MissionsMsg) => void) | null = null;
  onSos: ((message: SosMsg) => void) | null = null;
  onParty: ((message: PartyInviteMsg | PartyStateMsg | PartyEventMsg) => void) | null = null;
  onBounty: ((message: BountyMsg) => void) | null = null;
  onInvasion: ((message: InvasionMsg) => void) | null = null;

  private ws: WebSocket | null = null;
  private credentials: Credentials | null = null;
  private pingTimer = 0;
  private retryTimer = 0;
  private retryMs = FIRST_RETRY_MS;
  private rateWindowStart = 0;
  private rateCount = 0;

  constructor(readonly url: string) {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') this.close(CLOSE_HIDDEN);
      else if (this.state === 'offline') this.resume(); // вернулись на вкладку — в том числе забираем корабль назад
    });
  }

  /** Есть ли с чем входить: без ника с паролем или ключа соединения нет. */
  get hasCredentials(): boolean {
    return this.credentials !== null;
  }

  /** Войти — в том числе заново, под другим ником. */
  login(credentials: Credentials): void {
    this.credentials = credentials;
    this.close(CLOSE_LOGOUT);
    this.resume();
  }

  /** Выйти: соединение закрывается, следующий вход — через форму. */
  logout(): void {
    this.credentials = null;
    this.accountName = '';
    this.close(CLOSE_LOGOUT);
  }

  /** Подключиться сейчас, не дожидаясь повтора: после возврата на вкладку или тапа «вернуть корабль». */
  resume(): void {
    if (!this.credentials || this.state !== 'offline') return;
    window.clearTimeout(this.retryTimer);
    this.retryMs = FIRST_RETRY_MS;
    this.replaced = false;
    this.connect();
  }

  send(message: ClientMessage): void {
    const ws = this.ws;
    if (!ws) return;
    const text = JSON.stringify(message);
    this.lag.send(() => {
      if (ws.readyState === WebSocket.OPEN) ws.send(text);
    });
  }

  private connect(): void {
    const credentials = this.credentials;
    if (!credentials) return;
    this.state = 'connecting';
    const ws = new WebSocket(this.url);
    ws.binaryType = 'arraybuffer';
    this.ws = ws;
    // Снапшоты — бинарные дельты: собирать их надо в порядке прихода, до задержки FakeLag, иначе кадры перепутаются.
    const snapshots = new SnapshotDecoder();

    ws.onopen = () => {
      this.retryMs = FIRST_RETRY_MS;
      this.send({ t: 'hello', ...credentials });
      this.ping();
      this.pingTimer = window.setInterval(() => this.ping(), PING_INTERVAL_MS);
    };
    ws.onmessage = (e) => {
      let message: ServerMessage;
      if (typeof e.data === 'string') {
        message = JSON.parse(e.data) as ServerMessage;
      } else {
        this.snapshotBytes += (e.data as ArrayBuffer).byteLength;
        message = snapshots.decode(e.data as ArrayBuffer);
      }
      this.lag.receive(() => {
        if (this.ws === ws) this.handle(message); // задержанное сообщение старой сессии не нужно
      });
    };
    ws.onclose = (e) => {
      if (this.ws !== ws) return;
      this.drop();
      if (e.code === CLOSE_DENIED) return; // ждём пилота с формой входа
      if (e.code === CLOSE_REPLACED) {
        this.replaced = true;
        return;
      }
      this.retryTimer = window.setTimeout(() => this.connect(), this.retryMs);
      this.retryMs = Math.min(this.retryMs * 2, MAX_RETRY_MS);
    };
  }

  /** Фон (CLOSE_HIDDEN): iOS всё равно порвёт сокет, но позже — а до тех пор сервер вёл бы корабль по последнему входу. */
  private close(code: number): void {
    window.clearTimeout(this.retryTimer);
    const ws = this.ws;
    if (!ws) return;
    this.drop();
    ws.close(code, code === CLOSE_HIDDEN ? 'hidden' : 'logout');
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
      case 'account':
        this.accountName = message.name;
        // Пароль больше не нужен: дальше входим по ключу устройства.
        if (message.key && this.credentials) this.credentials = { key: message.key };
        this.onAccount?.(message);
        break;
      case 'denied':
        this.credentials = null;
        this.onDenied?.(message.code);
        break;
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
        // 'online' бывает только после welcome этого же сокета: новый сокет начинается с 'offline' (drop).
        const transfer = this.state === 'online';
        this.state = 'online';
        this.roster.reset();
        this.onWelcome?.(message, transfer);
        break;
      case 'pong': {
        const rtt = performance.now() - message.c;
        this.rttMs = this.rttMs === 0 ? rtt : this.rttMs * 0.8 + rtt * 0.2;
        break;
      }
      case 'players': {
        this.totalOnline = message.total ?? this.roster.onlineCount;
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
      case 'cargo':
        this.onCargo?.(message);
        break;
      case 'market':
        this.onMarket?.(message);
        break;
      case 'notice':
        this.onNotice?.(message);
        break;
      case 'hangar':
        this.onHangar?.(message);
        break;
      case 'missions':
        this.onMissions?.(message);
        break;
      case 'sos':
        this.onSos?.(message);
        break;
      case 'partyInvite':
      case 'partyState':
      case 'partyEvent':
        this.onParty?.(message);
        break;
      case 'bounty':
        this.onBounty?.(message);
        break;
      case 'invasion':
        this.onInvasion?.(message);
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
