import rules from '../../../shared/race/rules.json';
import track from '../../../shared/race/tracks/red-canyon.json';
import { RaceInput } from './input';
import { body, type Body } from './physics';
import { LocalRace } from './localRace';
import { RaceOpponents } from './opponents';
import { RaceScene } from './scene';
import { artUrl, type Livery } from './art';
import type { Board, Config, Finish, RaceCommand, RaceEvent, Result } from './types';

export function raceTime(ms: number, precise = true): string {
  const value = Math.max(0, Math.round(ms));
  return `${Math.floor(value / 60000).toString().padStart(2, '0')}:${Math.floor(value / 1000 % 60).toString().padStart(2, '0')}.${
    (precise ? value % 1000 : Math.floor(value % 1000 / 10)).toString().padStart(precise ? 3 : 2, '0')}`;
}
const TEXT: Record<string, string> = {
  WALL: 'Удар о стену', COLUMN: 'Удар о колонну', ROCK: 'Удар о скалу', BOLIDE: 'Столкновение с болидом', USER_ABORT: 'Отмена пилотом',
  CONTROL_LOST: 'Управление потеряно', SERVER_OVERLOAD: 'Сервер не успел обработать заезд',
  LOAD_TIMEOUT: 'Истекло время загрузки', TIME_LIMIT: 'Истекло время заезда',
  NOT_DOCKED_AT_RACE_PORT: 'Гонки доступны только в доке Тау-Прима', RACE_ALREADY_ACTIVE: 'Уже идёт другая попытка',
  CONFIG_MISMATCH: 'Конфигурация трассы изменилась. Вернитесь в терминал', SESSION_NOT_FOUND: 'Попытка не найдена',
  TRACK_UNAVAILABLE: 'Трасса недоступна', INVALID_STATE: 'Действие сейчас недоступно',
  INVALID_INPUT: 'Команда управления отклонена', RATE_LIMITED: 'Слишком много запросов',
  RESULT_PENDING: 'Итог ещё не готов', STORAGE_UNAVAILABLE: 'Не удалось подтвердить результат',
  INVALID_RESULT: 'Сервер отклонил результат заезда',
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
  private config: Config = { track, rules, hash: '', rankingKey: `${track.id}:${track.version}:${rules.id}:${rules.vehicleVersion}:${track.lapCount}:browser-v2}` };
  private run: LocalRace | null = null;
  private opponents: RaceOpponents | null = null;
  private finishSubmission: Finish | null = null;
  private finishSent = false;
  private session: string | null = null;
  private result: Result | null = null;
  private validation = 'PENDING';
  private boards: Partial<Record<'total' | 'lap', Board>> = {};
  private boardKind: 'total' | 'lap' = 'total';
  private livery: Livery = 'copper';
  private previousBest: number | null = null;
  private message = '';
  private body: Body = body(track, rules);
  private input: RaceInput;
  private lastFrame = 0;
  private menuOpen = false;
  private hud: HTMLElement | null = null;
  private countdown: HTMLElement | null = null;
  private brake: HTMLButtonElement | null = null;
  private lapNotice: HTMLElement | null = null;
  private lapUntil = 0;
  private lastCountdown = '';
  private joinRequest: string | null = null;
  private boardRequests = new Set<string>();
  private onlineBefore = true;
  private resultShowAt = 0;
  private resultRendered = false;
  get throttle(): number { return this.mode === 'race' && this.run?.state === 'RUNNING' && !this.input.value.brake ? this.input.value.throttle : 0; }
  constructor(private options: Options) {
    this.root.id = 'race'; this.root.hidden = true; document.body.append(this.root);
    for (const event of ['keydown', 'keyup'] as const) window.addEventListener(event, e => {
      if (this.open && this.mode !== 'race' && !(e.code === 'Enter' && e.target instanceof HTMLButtonElement)) e.stopImmediatePropagation();
    }, { capture: true });
    this.input = new RaceInput(() => {}, () => this.exitMenu(), () => this.mode === 'race' && !this.menuOpen &&
      (this.run?.state === 'RUNNING' || this.run?.state === 'COUNTDOWN'));
  }
  show(): void {
    this.open = true; this.root.hidden = false; document.body.classList.add('race-open');
    this.options.resetSpace(); this.terminal();
  }
  close(): void {
    if (this.run?.active && this.mode === 'race') return;
    this.input.unbind(); this.scene.close(); this.root.hidden = true; this.open = false;
    this.options.resetSpace(); document.body.classList.remove('race-open');
  }
  private terminal(): void {
    this.mode = 'terminal'; this.session = null; this.run = null; this.opponents = null; this.result = null;
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
    for (const [livery, label] of [['copper', 'Медный болид'], ['blue', 'Синий болид'], ['yellow', 'Жёлтый болид'], ['white', 'Белый болид']] as const) {
      const pick = button(label, () => { this.livery = livery; this.renderTerminal(); });
      pick.setAttribute('aria-pressed', String(this.livery === livery));
      const image = node('img', ''); image.src = artUrl(`pod-${livery}-v1`); image.alt = ''; image.width = 160; image.height = 96;
      pick.prepend(image); vehicles.append(pick);
    }
    card.append(vehicles);
    const best = this.boards.total?.self, lap = this.boards.lap?.self;
    card.append(node('p', 'race-best', `Личный заезд: ${best ? raceTime(best.timeMs) : 'Рекорд пока не установлен'}`),
      node('p', 'race-best', `Лучший круг: ${lap ? raceTime(lap.timeMs) : 'Рекорд пока не установлен'}`));
    if (!this.options.registered?.()) card.append(node('p', 'race-muted', 'Тестовый заезд — общий рекорд не сохраняется. Для рейтинга нужен зарегистрированный аккаунт.'));
    card.append(node('p', '', 'На старте — три компьютерных соперника: Шакал, Искра и Кочевник. Обгоняйте их и ставьте рекорды времени.'));
    const start = button('Гонка с соперниками', () => this.start()); start.classList.add('sro-btn--primary'); start.disabled = !this.options.online(); card.append(start);
    card.append(node('p', '', 'W / ↑ — повысить скорость · S / ↓ — снизить скорость · A / D / ← / → — поворот · Пробел — тормоз, пока зажат. На телефоне: стик задаёт направление и скорость, остаётся в выбранном положении; кнопка тормоза действует, пока зажата.'));
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
    this.mode = 'loading'; this.session = null; this.message = ''; this.result = null; this.run = null; this.finishSubmission = null; this.finishSent = false;
    this.resultShowAt = 0; this.resultRendered = false;
    this.opponents = null;
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
    if (m.event === 'joined' && m.config && m.requestId === this.joinRequest && this.mode === 'loading') {
      this.session = m.sessionId!; this.config = m.config; this.body = body(this.config.track, this.config.rules);
      this.scene.load(this.config);
      this.options.send({ t: 'raceAct', act: 'ready', sessionId: this.session, configHash: this.config.hash }); return;
    }
    if (m.sessionId !== this.session) return;
    if (m.event === 'started' && this.mode === 'loading') {
      this.run = new LocalRace(this.config, performance.now(), m.countdownMs ?? this.config.rules.countdownSeconds * 1000);
      this.opponents = new RaceOpponents(this.run, this.livery);
      this.body = this.run.body; this.mode = 'race'; this.menuOpen = false;
      this.input.value.heading = this.config.track.spawnHeading;
      this.renderRace(); this.lastFrame = 0; this.lastCountdown = ''; return;
    }
    if (m.event === 'result' && m.result) {
      const first = this.result === null;
      this.result = m.result; this.validation = m.validationStatus ?? 'PENDING';
      if (this.validation === 'CONFIRMED') { this.message = ''; this.finishSubmission = null; }
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
    stick.setAttribute('aria-label', 'Направление и скорость');
    this.brake = button('ТОРМОЗ', () => {}); this.brake.classList.add('race-brake', 'race-mobile');
    const menu = button('Выход', () => this.exitMenu()); menu.classList.add('race-menu');
    this.root.append(this.hud, this.countdown, this.lapNotice, stick, this.brake, menu);
    this.input.bind(this.root, stick, knob, this.brake);
  }
  private exitMenu(): void {
    if (this.mode !== 'race') return;
    this.menuOpen = !this.menuOpen; this.input.safe(true);
    this.root.querySelector('.race-exit')?.remove();
    if (!this.menuOpen) return;
    const card = node('section', 'race-exit race-card sro-pane');
    card.append(node('h2', '', 'Прервать заезд?'), node('p', '', 'Заезд продолжается. Таймер и физика не остановлены.'),
      button('Продолжить', () => this.exitMenu()), button('Прервать заезд', () => {
        this.run?.abort(); this.completeLocal();
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
    if (this.opponents) {
      card.append(node('h2', '', r.status === 'FINISHED' ? `Место на финише: ${this.opponents.place}/4` : 'Участники заезда'));
      const table = node('table', 'race-ranking');
      for (const [i, racer] of this.opponents.standings().entries()) {
        const row = node('tr', racer.player ? 'race-best' : '');
        const run = racer.run;
        row.append(node('td', '', String(i + 1)), node('td', '', racer.name), node('td', 'sro-num',
          run.state === 'FINISHED' ? raceTime(run.elapsedMs) : run.active ? `Круг ${run.lap}/${this.config.track.lapCount}` : 'DNF'));
        table.append(row);
      }
      card.append(table);
    }
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
    const repeat = button('Повторить', () => this.start()), terminal = button('В терминал', () => this.terminal());
    repeat.disabled = terminal.disabled = !confirmed;
    card.append(repeat, terminal);
    if (!confirmed) card.append(button('Проверить сохранение', () => {
      if (this.finishSubmission) this.sendFinish();
      else this.options.send({ t: 'raceAct', act: 'result', sessionId: this.session! });
    }));
    this.root.replaceChildren(card); this.root.classList.remove('race-screen--driving');
    this.resultRendered = true;
  }
  private showLap(count: number, time: number): void {
    if (this.lapNotice) this.lapNotice.textContent = `КРУГ ${count} — ${raceTime(time, false)}`;
    this.lapUntil = performance.now() + 2500; this.options.sound?.('lap');
  }
  private sendFinish(): void {
    if (!this.session || !this.finishSubmission || !this.options.online()) return;
    this.options.send({ t: 'raceAct', act: 'finish', sessionId: this.session, finish: this.finishSubmission });
    this.finishSent = true;
  }
  private completeLocal(): void {
    const run = this.run;
    if (!run || run.active || this.mode !== 'race') return;
    this.finishSubmission = { status: run.state as Finish['status'], reason: run.reason, lapTimesMs: [...run.lapTimesMs] };
    this.apply({ t: 'raceEvent', event: 'result', sessionId: this.session!, validationStatus: 'PENDING', result: {
      sessionId: this.session!, playerId: this.options.registered?.() ? 'pending' : null, name: '', rankingKey: this.config.rankingKey,
      status: run.state, reason: run.reason, elapsedMs: run.elapsedMs, totalMs: run.state === 'FINISHED' ? run.elapsedMs : null,
      lapTimesMs: [...run.lapTimesMs], finishedAt: new Date().toISOString(),
    } });
    this.sendFinish();
  }
  frame(now: number, width: number, height: number): void {
    if (!this.open) return;
    const online = this.options.online();
    if (online && !this.onlineBefore && this.finishSubmission) { this.finishSent = false; this.sendFinish(); }
    if (online && !this.onlineBefore && this.mode === 'loading') {
      if (this.session) this.options.send({ t: 'raceAct', act: 'ready', sessionId: this.session, configHash: this.config.hash });
      else if (this.joinRequest) this.options.send({ t: 'raceAct', act: 'join', requestId: this.joinRequest });
    }
    this.onlineBefore = online;
    const dt = this.lastFrame ? Math.min(.1, (now - this.lastFrame) / 1000) : 0; this.lastFrame = now;
    this.input.update(dt);
    if (this.mode === 'race' && this.run) {
      const laps = this.run.lapTimesMs.length;
      if (this.opponents) this.opponents.advance(now, this.input.value);
      else this.run.advance(now, this.input.value);
      if (this.run.lapTimesMs.length > laps) {
        this.showLap(this.run.lapTimesMs.length, this.run.lapTimesMs.at(-1)!);
      }
      this.completeLocal();
    }
    if (this.mode === 'results' && this.finishSubmission && !this.finishSent && online) this.sendFinish();
    if (this.scene.view.visible) this.scene.frame(now, width, height, this.body, this.input.value,
      this.result?.status === 'CRASHED', this.run?.crashVelocity, this.run?.impacts.splice(0), this.opponents?.racers);
    if (this.mode === 'results' && !this.resultRendered && now >= this.resultShowAt) this.renderResults();
    if (this.mode !== 'race' || !this.run) return;
    const s = this.run;
    if (this.hud) this.hud.textContent = `${raceTime(s.elapsedMs, false)}  ·  Круг ${s.lap}/${this.config.track.lapCount}  ·  Место ${this.opponents?.place ?? 1}/4\n${Math.ceil(this.body.hp)} HP  ·  ${Math.hypot(this.body.velocity.x, this.body.velocity.y).toFixed(0)} → ${(this.input.value.throttle * this.config.rules.engineLimit).toFixed(0)} ед./с${!online ? ' · Нет связи' : ''}`;
    const count = s.state === 'COUNTDOWN' ? String(Math.ceil(s.countdownMs(now) / 1000)) : s.state === 'RUNNING' && s.elapsedMs < 700 ? 'СТАРТ' : '';
    if (count && count !== this.lastCountdown) this.options.sound?.(count === 'СТАРТ' ? 'start' : 'countdown');
    this.lastCountdown = count; if (this.countdown) this.countdown.textContent = count;
    if (this.lapNotice) this.lapNotice.hidden = now > this.lapUntil;
    if (this.brake) this.brake.setAttribute('aria-pressed', String(this.input.value.brake));
  }
}
