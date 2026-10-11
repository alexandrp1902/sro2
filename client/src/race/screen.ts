import rules from '../../../shared/race/rules.json';
import track from '../../../shared/race/tracks/red-canyon.json';
import { RaceInput } from './input';
import { body, step, type Body } from './physics';
import { RaceScene } from './scene';
import type { Board, Config, Input, RaceCommand, RaceEvent, Result, Snapshot } from './types';

export function raceTime(ms: number, precise = true): string {
  const value = Math.max(0, Math.round(ms));
  return `${Math.floor(value / 60000).toString().padStart(2, '0')}:${Math.floor(value / 1000 % 60).toString().padStart(2, '0')}.${
    (precise ? value % 1000 : Math.floor(value % 1000 / 10)).toString().padStart(precise ? 3 : 2, '0')}`;
}
const TEXT: Record<string, string> = {
  WALL: 'Удар о стену', COLUMN: 'Удар о колонну', ROCK: 'Удар о скалу', USER_ABORT: 'Отмена пилотом',
  CONTROL_LOST: 'Управление потеряно', SERVER_OVERLOAD: 'Сервер не успел обработать заезд',
  LOAD_TIMEOUT: 'Истекло время загрузки', TIME_LIMIT: 'Истекло время заезда',
  NOT_DOCKED_AT_RACE_PORT: 'Гонки доступны только в доке Тау-Прима', RACE_ALREADY_ACTIVE: 'Уже идёт другая попытка',
  CONFIG_MISMATCH: 'Конфигурация трассы изменилась. Вернитесь в терминал', SESSION_NOT_FOUND: 'Попытка не найдена',
  TRACK_UNAVAILABLE: 'Трасса недоступна', INVALID_STATE: 'Действие сейчас недоступно',
  INVALID_INPUT: 'Команда управления отклонена', RATE_LIMITED: 'Слишком много запросов',
  RESULT_PENDING: 'Итог ещё не готов', STORAGE_UNAVAILABLE: 'Не удалось подтвердить результат',
};
function node<K extends keyof HTMLElementTagNameMap>(tag: K, className: string, text?: string): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag); el.className = className; if (text) el.textContent = text; return el;
}
function button(label: string, action: () => void): HTMLButtonElement {
  const b = node('button', 'sro-btn', label); b.type = 'button'; b.onclick = action; return b;
}
interface Options { send(command: RaceCommand): void; online(): boolean; registered?(): boolean; resetSpace(): void; sound?(cue: string): void }

export class RaceScreen {
  readonly scene = new RaceScene();
  readonly root = node('div', 'race-screen');
  open = false;
  private mode: 'terminal' | 'loading' | 'race' | 'results' = 'terminal';
  private config: Config = { track, rules, hash: '', rankingKey: `${track.id}:${track.version}:${rules.id}:${rules.vehicleVersion}:${track.lapCount}` };
  private snapshot: Snapshot | null = null;
  private session: string | null = null;
  private result: Result | null = null;
  private validation = 'PENDING';
  private boards: Partial<Record<'total' | 'lap', Board>> = {};
  private boardKind: 'total' | 'lap' = 'total';
  private livery: 'copper' | 'blue' = 'copper';
  private previousBest: number | null = null;
  private message = '';
  private body: Body = body(track, rules);
  private input: RaceInput;
  private lastFrame = 0;
  private lastSend = 0;
  private accumulator = 0;
  private pending: { input: Input }[] = [];
  private menuOpen = false;
  private hud: HTMLElement | null = null;
  private countdown: HTMLElement | null = null;
  private speed: HTMLInputElement | null = null;
  private speedLabel: HTMLElement | null = null;
  private brake: HTMLButtonElement | null = null;
  private lapNotice: HTMLElement | null = null;
  private lapUntil = 0;
  private lastCountdown = '';
  private joinRequest: string | null = null;
  private boardRequests = new Set<string>();
  private onlineBefore = true;
  private resultShowAt = 0;
  private resultRendered = false;
  get throttle(): number { return this.mode === 'race' && this.snapshot?.state === 'RUNNING' && !this.input.value.brake ? this.input.value.throttle : 0; }
  constructor(private options: Options) {
    this.root.id = 'race'; this.root.hidden = true; document.body.append(this.root);
    for (const event of ['keydown', 'keyup'] as const) window.addEventListener(event, e => {
      if (this.open && this.mode !== 'race' && !(e.code === 'Enter' && e.target instanceof HTMLButtonElement)) e.stopImmediatePropagation();
    }, { capture: true });
    this.input = new RaceInput(() => this.sendInput(), () => this.exitMenu(), () => this.mode === 'race' && !this.menuOpen &&
      (this.snapshot?.state === 'RUNNING' || this.snapshot?.state === 'COUNTDOWN'));
  }
  show(): void {
    this.open = true; this.root.hidden = false; document.body.classList.add('race-open');
    this.options.resetSpace(); this.terminal();
  }
  close(): void {
    if (this.snapshot && ['RUNNING', 'COUNTDOWN', 'GRID'].includes(this.snapshot.state) && this.mode === 'race') return;
    this.input.unbind(); this.scene.close(); this.root.hidden = true; this.open = false;
    this.options.resetSpace(); document.body.classList.remove('race-open');
  }
  private terminal(): void {
    this.mode = 'terminal'; this.session = null; this.snapshot = null; this.result = null;
    this.input.unbind(); this.scene.close(); this.message = ''; this.renderTerminal(); this.fetchBoards();
    this.root.classList.remove('race-screen--driving');
  }
  private fetchBoards(): void {
    if (!this.options.online()) { this.message = 'Нет связи с сервером'; this.renderTerminal(); return; }
    for (const kind of ['total', 'lap'] as const) {
      const requestId = crypto.randomUUID(); this.boardRequests.add(requestId);
      this.options.send({ t: 'raceAct', act: 'leaderboard', kind, requestId });
    }
  }
  private renderTerminal(): void {
    const card = node('section', 'race-card sro-pane');
    card.append(node('p', 'race-eyebrow', 'ТАУ-ПРИМА · ПЫЛЬНАЯ ГАВАНЬ'), node('h1', '', this.config.track.name));
    card.append(node('p', '', `${this.config.track.length} ед. · ${this.config.track.lapCount} круга · Сложная трасса`));
    card.append(node('p', '', 'Инерция, плавный разгон и торможение. Сильный удар уничтожает болид. Участие бесплатно.'));
    const vehicles = node('div', 'race-vehicles');
    for (const [livery, label] of [['copper', 'Медный болид'], ['blue', 'Синий болид']] as const) {
      const pick = button(label, () => { this.livery = livery; this.renderTerminal(); });
      pick.setAttribute('aria-pressed', String(this.livery === livery));
      const image = node('img', ''); image.src = `${import.meta.env.BASE_URL}race/bolide-${livery}.png`; image.alt = ''; image.width = 160; image.height = 96;
      pick.prepend(image); vehicles.append(pick);
    }
    card.append(vehicles);
    const best = this.boards.total?.self, lap = this.boards.lap?.self;
    card.append(node('p', 'race-best', `Личный заезд: ${best ? raceTime(best.timeMs) : 'Рекорд пока не установлен'}`),
      node('p', 'race-best', `Лучший круг: ${lap ? raceTime(lap.timeMs) : 'Рекорд пока не установлен'}`));
    if (!this.options.registered?.()) card.append(node('p', 'race-muted', 'Тестовый заезд — общий рекорд не сохраняется. Для рейтинга нужен зарегистрированный аккаунт.'));
    const start = button('Заезд на время', () => this.start()); start.classList.add('sro-btn--primary'); start.disabled = !this.options.online(); card.append(start);
    card.append(node('p', '', 'W / ↑ — повысить скорость · S / ↓ — снизить скорость · A / D / ← / → — поворот · Пробел — тормоз, пока зажат. На телефоне: стик задаёт направление, шкала — скорость; кнопка тормоза действует, пока зажата.'));
    const tabs = node('div', 'race-actions');
    for (const [kind, label] of [['total', `${this.config.track.lapCount} круга`], ['lap', 'Лучший круг']] as const) {
      const b = button(label, () => { this.boardKind = kind; this.renderTerminal(); }); b.setAttribute('aria-pressed', String(this.boardKind === kind)); tabs.append(b);
    }
    card.append(node('h2', '', 'Рекорды'), tabs, node('small', 'race-muted', `Версия: ${this.config.rankingKey}`));
    const board = this.boards[this.boardKind];
    if (board) {
      const table = node('table', 'race-ranking');
      for (const row of board.rows) {
        const tr = node('tr', '');
        tr.append(node('td', '', `${row.rank}`), node('td', '', row.name), node('td', 'sro-num', raceTime(row.timeMs))); table.append(tr);
      }
      card.append(table);
      if (!board.rows.length) card.append(node('p', '', 'Рекорд пока не установлен'));
      card.append(node('p', '', board.self ? `Моё место: ${board.self.rank} · ${raceTime(board.self.timeMs)}` : 'Мой результат: рекорд пока не установлен'));
    } else card.append(node('p', '', this.message || 'Загружаем рейтинг…'));
    if (this.message) card.append(node('p', 'race-error', this.message));
    card.append(button('Обновить рекорды', () => this.fetchBoards()), button('Назад в док', () => this.close()));
    this.root.replaceChildren(card);
  }
  private async start(): Promise<void> {
    if (!this.options.online()) return;
    this.previousBest = this.boards.total?.self?.timeMs ?? null;
    this.mode = 'loading'; this.message = ''; this.result = null; this.snapshot = null; this.pending = [];
    this.resultShowAt = 0; this.resultRendered = false;
    this.input.unbind(); this.root.replaceChildren(node('section', 'race-card sro-pane', 'Загружаем трассу…'));
    this.joinRequest = crypto.randomUUID();
    const requestId = this.joinRequest;
    try {
      await this.scene.prepare(this.livery);
      if (!this.open || this.mode !== 'loading' || this.joinRequest !== requestId) return;
      this.options.send({ t: 'raceAct', act: 'join', requestId });
    } catch {
      if (!this.open || this.mode !== 'loading' || this.joinRequest !== requestId) return;
      this.mode = 'terminal'; this.message = 'Не удалось загрузить болид. Повторите попытку.'; this.renderTerminal();
    }
  }
  apply(m: RaceEvent): void {
    if (!this.open) return;
    if (m.event === 'error') {
      if (m.sessionId && m.sessionId !== this.session) return;
      this.message = TEXT[m.code ?? ''] ?? 'Не удалось выполнить запрос';
      if (this.mode === 'loading') { this.mode = 'terminal'; this.renderTerminal(); }
      else if (this.mode === 'terminal') this.renderTerminal();
      else if (this.mode === 'results') this.renderResults();
      return;
    }
    if (m.event === 'leaderboard' && m.board && m.requestId && this.boardRequests.delete(m.requestId)) {
      this.boards[m.board.kind as 'total' | 'lap'] = m.board; this.message = '';
      if (this.mode === 'terminal') this.renderTerminal(); return;
    }
    if (m.event === 'joined' && m.config && m.snapshot && m.requestId === this.joinRequest) {
      this.session = m.sessionId!; this.config = m.config; this.body = body(this.config.track, this.config.rules);
      this.scene.load(this.config); this.snapshot = m.snapshot; this.mode = 'race'; this.menuOpen = false;
      this.input.value.heading = this.config.track.spawnHeading; this.input.value.seq = 0;
      this.renderRace(); this.lastFrame = 0; this.accumulator = 0; this.lastCountdown = '';
      this.options.send({ t: 'raceAct', act: 'ready', sessionId: this.session, configHash: this.config.hash }); return;
    }
    if (m.sessionId !== this.session) return;
    if (m.snapshot) {
      const s = m.snapshot;
      if (this.snapshot && s.tick < this.snapshot.tick) return;
      if (s.lapTimesMs.length > (this.snapshot?.lapTimesMs.length ?? 0)) {
        if (this.lapNotice) this.lapNotice.textContent = `КРУГ ${s.lapTimesMs.length} — ${raceTime(s.lapTimesMs.at(-1)!, false)}`;
        this.lapUntil = performance.now() + 2500; this.options.sound?.('lap');
      }
      this.snapshot = s;
      this.body = { position: { ...s.position }, velocity: { ...s.velocity }, heading: s.heading, omega: s.omega,
        hp: s.hp, contacts: this.body.contacts };
      this.pending = this.pending.filter(p => p.input.seq > s.ackSeq);
      if (s.state === 'RUNNING') for (const p of this.pending) step(this.body, p.input, this.config.track, this.config.rules);
    }
    if (m.event === 'result' && m.result) {
      const first = this.result === null;
      this.result = m.result; this.validation = m.validationStatus ?? 'PENDING';
      if (this.validation === 'CONFIRMED') this.message = '';
      if (m.board) this.boards.total = m.board;
      if (first) {
        if (m.result.status === 'CRASHED') { this.body.hp = 0; this.options.sound?.('crash'); }
        else if (m.result.status === 'FINISHED') this.options.sound?.('finish');
        this.body.velocity = { x: 0, y: 0 };
      }
      this.mode = 'results'; this.input.unbind(); this.renderResults();
      if (first && m.result.status === 'CRASHED') {
        this.resultShowAt = performance.now() + 650; this.resultRendered = false;
        // Keep the field visible briefly so the confirmed explosion can be read.
        this.root.replaceChildren(node('div', 'race-countdown', 'DNF')); this.root.classList.add('race-screen--driving');
      } else if (performance.now() < this.resultShowAt) {
        this.resultRendered = false;
        this.root.replaceChildren(node('div', 'race-countdown', 'DNF')); this.root.classList.add('race-screen--driving');
      }
    }
  }
  private renderRace(): void {
    this.root.replaceChildren(); this.root.classList.add('race-screen--driving');
    this.hud = node('div', 'race-hud sro-pane sro-num');
    this.countdown = node('div', 'race-countdown'); this.countdown.setAttribute('aria-live', 'polite');
    this.lapNotice = node('div', 'race-lap-notice'); this.lapNotice.setAttribute('aria-live', 'polite');
    const stick = node('div', 'race-stick race-mobile'), knob = node('div', 'race-knob'); stick.append(knob);
    stick.setAttribute('aria-label', 'Целевое направление');
    this.brake = button('ТОРМОЗ', () => {}); this.brake.classList.add('race-brake', 'race-mobile');
    const speedControl = node('label', 'race-speed race-mobile');
    this.speedLabel = node('span', 'race-speed-label', '0 ед./с');
    this.speed = node('input', 'race-speed-range'); this.speed.type = 'range';
    this.speed.min = '0'; this.speed.max = '100'; this.speed.step = '0.1'; this.speed.value = '0';
    this.speed.setAttribute('aria-label', 'Заданная скорость');
    speedControl.append(this.speedLabel, this.speed, node('span', 'race-speed-title', 'СКОРОСТЬ'));
    const menu = button('Выход', () => this.exitMenu()); menu.classList.add('race-menu');
    this.root.append(this.hud, this.countdown, this.lapNotice, stick, speedControl, this.brake, menu);
    this.input.bind(this.root, stick, knob, this.speed, this.brake);
  }
  private exitMenu(): void {
    if (this.mode !== 'race') return;
    this.menuOpen = !this.menuOpen; this.input.safe(true); this.sendInput();
    this.root.querySelector('.race-exit')?.remove();
    if (!this.menuOpen) return;
    const card = node('section', 'race-exit race-card sro-pane');
    card.append(node('h2', '', 'Прервать заезд?'), node('p', '', 'Заезд продолжается. Таймер и физика не остановлены.'),
      button('Продолжить', () => this.exitMenu()), button('Прервать заезд', () => {
        this.options.send({ t: 'raceAct', act: 'abort', sessionId: this.session! });
      }));
    this.root.append(card);
  }
  private renderResults(): void {
    const r = this.result!;
    const card = node('section', 'race-card sro-pane');
    card.append(node('p', 'race-eyebrow', this.config.track.name), node('h1', '',
      r.status === 'FINISHED' ? 'Финиш' : r.status === 'CRASHED' ? 'БОЛИД УНИЧТОЖЕН · DNF' : 'Заезд прерван · ABORT'));
    if (r.reason) card.append(node('p', '', TEXT[r.reason] ?? 'Заезд завершён'));
    card.append(node('h2', 'sro-num', raceTime(r.elapsedMs)));
    for (const [i, lap] of r.lapTimesMs.entries()) card.append(node('p', 'sro-num', `Круг ${i + 1} — ${raceTime(lap)}`));
    if (r.lapTimesMs.length) card.append(node('p', '', `Лучший завершённый круг: ${raceTime(Math.min(...r.lapTimesMs))}`));
    const confirmed = this.validation === 'CONFIRMED';
    card.append(node('p', '', r.playerId === null ? 'Тестовый заезд — общий рекорд не сохраняется' :
      r.status === 'ABORTED' ? 'Отменённые заезды и их круги не входят в рейтинг' :
      confirmed ? 'Результат подтверждён сервером' : 'Проверяем результат… Рекорд пока не подтверждён'));
    if (confirmed && r.playerId !== null && r.status === 'FINISHED') {
      if (this.previousBest === null || r.totalMs! < this.previousBest) card.append(node('p', 'race-best',
        this.previousBest === null ? 'Первый личный рекорд!' : `Новый личный рекорд: −${((this.previousBest - r.totalMs!) / 1000).toFixed(3)} с`));
      const rank = this.boards.total?.self;
      if (rank) card.append(node('p', '', `Моё место: ${rank.rank} · Личный рекорд ${raceTime(rank.timeMs)}`));
    }
    if (this.message) card.append(node('p', 'race-error', this.message));
    card.append(button('Повторить', () => this.start()), button('В терминал', () => this.terminal()));
    if (!confirmed) card.append(button('Проверить сохранение', () => this.options.send({ t: 'raceAct', act: 'result', sessionId: this.session! })));
    this.root.replaceChildren(card); this.root.classList.remove('race-screen--driving');
    this.resultRendered = true;
  }
  private sendInput(): void {
    if (this.mode !== 'race' || !this.session || !this.options.online()) return;
    if (performance.now() - this.lastSend < 12) return;
    this.input.value.seq++; this.lastSend = performance.now();
    this.options.send({ t: 'raceAct', act: 'input', sessionId: this.session, input: { ...this.input.value } });
  }
  frame(now: number, width: number, height: number): void {
    if (!this.open) return;
    const online = this.options.online();
    if (online && !this.onlineBefore && this.session) this.options.send({ t: 'raceAct', act: 'result', sessionId: this.session });
    if (!online && this.onlineBefore) {
      this.input.safe(false); this.message = 'Нет связи. Заезд не ставится на паузу; ожидаем официальный итог.';
      if (this.mode === 'loading') { this.mode = 'terminal'; this.renderTerminal(); }
    }
    this.onlineBefore = online;
    const dt = this.lastFrame ? Math.min(.1, (now - this.lastFrame) / 1000) : 0; this.lastFrame = now;
    this.input.update(dt);
    if (this.mode === 'race' && now - this.lastSend >= 50) this.sendInput();
    if (this.mode === 'race' && this.snapshot?.state === 'RUNNING') {
      this.accumulator += dt;
      while (this.accumulator >= 1 / 60) {
        if (this.body.hp > 0) step(this.body, this.input.value, this.config.track, this.config.rules);
        this.pending.push({ input: { ...this.input.value } }); this.pending = this.pending.slice(-12);
        this.accumulator -= 1 / 60;
      }
    }
    if (this.scene.view.visible) this.scene.frame(now, width, height, this.body, this.input.value, this.result?.status === 'CRASHED');
    if (this.mode === 'results' && !this.resultRendered && now >= this.resultShowAt) this.renderResults();
    if (this.mode !== 'race' || !this.snapshot) return;
    const s = this.snapshot;
    if (this.hud) this.hud.textContent = `${raceTime(s.elapsedMs, false)}  ·  Круг ${s.lap}/${this.config.track.lapCount}  ·  ${raceTime(s.lapElapsedMs, false)}\n${Math.ceil(s.hp)} HP  ·  ${Math.hypot(this.body.velocity.x, this.body.velocity.y).toFixed(0)} → ${(this.input.value.throttle * this.config.rules.engineLimit).toFixed(0)} ед./с${!online ? ' · Нет связи' : ''}`;
    const count = s.state === 'COUNTDOWN' ? String(Math.ceil(s.countdownMs / 1000)) : s.state === 'RUNNING' && s.elapsedMs < 700 ? 'СТАРТ' : '';
    if (count && count !== this.lastCountdown) this.options.sound?.(count === 'СТАРТ' ? 'start' : 'countdown');
    this.lastCountdown = count; if (this.countdown) this.countdown.textContent = count;
    if (this.lapNotice) this.lapNotice.hidden = now > this.lapUntil;
    if (this.brake) this.brake.setAttribute('aria-pressed', String(this.input.value.brake));
    if (this.speedLabel) this.speedLabel.textContent = `${(this.input.value.throttle * this.config.rules.engineLimit).toFixed(0)} ед./с`;
    if (this.speed) this.speed.setAttribute('aria-valuetext', `${(this.input.value.throttle * this.config.rules.engineLimit).toFixed(0)} единиц в секунду`);
  }
}
