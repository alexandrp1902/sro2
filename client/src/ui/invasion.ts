import type { InvasionMsg, InvasionScoreDto } from '../net/protocol';
import { formatCredits } from '../sim/shop';
import { clock } from '../util/clock';

/** Итог висит на табло столько, потом табло гаснет. */
const RESULT_MS = 12000;
/** Без новостей столько — вторжение считаем забытым (связь рвалась, сервер перезапустили). */
const STALE_MS = 5000;
/** Строк в таблице итогов. */
const TOP = 5;

export interface InvasionLines {
  title: string;
  hint: string;
  /** Итог: лучшие по урону и своя строка, если меня нет среди лучших. */
  results?: { place: number; name: string; damage: number; reward: number; self: boolean }[];
  alert: boolean;
}

/**
 * «Вторжение пиратов» (GDD §38) на клиенте: что сейчас с ним (табло справа), куда лететь (точка на миникарте)
 * и строки в ленту — об анонсе, начале, новой волне и итоге.
 */
export class InvasionBoard {
  private last: InvasionMsg | null = null;
  private seenAt = 0;
  /** Отсчёт идёт между рассылками сам — сервер присылает секунды раз в секунду. */
  private secondsAt = 0;

  /** @returns строка для ленты (и тревожная ли она) или null — просто очередной отсчёт. */
  apply(message: InvasionMsg, now: number, mySystem: string): { text: string; alert: boolean } | null {
    const prev = this.last;
    this.last = message;
    this.seenAt = now;
    this.secondsAt = now;
    const here = message.system === mySystem;
    const where = here ? 'в этой системе' : `в системе ${message.systemName}`;
    if (message.state === 'announce') {
      return prev?.state === 'announce' && prev.system === message.system
        ? null
        : { text: `ВТОРЖЕНИЕ ПИРАТОВ ${where} через ${clock(message.secondsLeft)}!`, alert: true };
    }
    if (message.state === 'wave') {
      if (prev?.state !== 'wave') return { text: `Вторжение началось ${where}: волна 1 из ${message.waves}`, alert: here };
      if (prev.wave !== message.wave) return { text: `Волна ${message.wave} из ${message.waves}!`, alert: here };
      if (prev.nextIn === 0 && message.nextIn > 0) return { text: `Волна ${prev.wave} отбита`, alert: false };
      return null;
    }
    const outcome = message.state === 'won' ? `Вторжение ${where} отбито!` : `Вторжение ${where} не отбито`;
    return { text: message.reward > 0 ? `${outcome} Ваша доля: +${formatCredits(message.reward)}` : outcome, alert: false };
  }

  /** Точка сбора пиратов — если вторжение идёт в моей системе. */
  point(mySystem: string): { x: number; y: number } | null {
    const m = this.last;
    if (!m || m.state !== 'wave' || m.system !== mySystem || (m.x === 0 && m.y === 0)) return null;
    return { x: m.x, y: m.y };
  }

  /** Система вторжения — для метки на карте галактики; null — нет. */
  system(now: number): string | null {
    const m = this.current(now);
    return m && (m.state === 'announce' || m.state === 'wave') ? m.system : null;
  }

  /** Табло; null — прятать. */
  lines(now: number, myName: string): InvasionLines | null {
    const m = this.current(now);
    if (!m) return null;
    const left = Math.max(0, m.secondsLeft - (now - this.secondsAt) / 1000);
    const title = `ВТОРЖЕНИЕ ПИРАТОВ · ${m.systemName}`;
    switch (m.state) {
      case 'announce':
        return { title, hint: `До начала ${clock(left)}`, alert: true };
      case 'wave': {
        const next = Math.max(0, m.nextIn - (now - this.secondsAt) / 1000);
        const wave = m.nextIn > 0 ? `Волна ${m.wave}/${m.waves} отбита · следующая ${clock(next)}` : `Волна ${m.wave}/${m.waves} · пиратов ${m.remaining}`;
        return { title, hint: `${wave} · ${clock(left)}`, alert: true };
      }
      default: {
        const hint =
          m.state === 'won'
            ? m.reward > 0
              ? `Отбито! Ваша доля ${formatCredits(m.reward)}`
              : 'Отбито!'
            : m.reward > 0
              ? `Не отбито. Утешительные ${formatCredits(m.reward)}`
              : 'Не отбито';
        return { title, hint, results: table(m.results ?? [], myName, m), alert: false };
      }
    }
  }

  clear(): void {
    this.last = null;
  }

  private current(now: number): InvasionMsg | null {
    const m = this.last;
    if (!m) return null;
    const final = m.state === 'won' || m.state === 'lost';
    if (now - this.seenAt > (final ? RESULT_MS : STALE_MS)) {
      this.last = null;
      return null;
    }
    return m;
  }
}

function table(results: InvasionScoreDto[], myName: string, m: InvasionMsg): InvasionLines['results'] {
  const rows = results.slice(0, TOP).map((r, i) => ({ place: i + 1, ...r, self: r.name === myName }));
  const mine = results.findIndex((r) => r.name === myName);
  if (mine >= TOP) rows.push({ place: mine + 1, ...results[mine], self: true });
  else if (mine < 0 && m.damage > 0) rows.push({ place: 0, name: myName, damage: m.damage, reward: m.reward, self: true });
  return rows;
}

/** Табло вторжения справа под трекером цели: заголовок, отсчёт или волна, в конце — таблица итогов. */
export class InvasionHud {
  private key = '';
  private readonly title: HTMLElement;
  private readonly hint: HTMLElement;
  private readonly results: HTMLElement;

  constructor(
    private readonly root: HTMLElement,
    onTap: () => void,
  ) {
    this.title = document.createElement('div');
    this.title.className = 'event-title';
    this.hint = document.createElement('div');
    this.hint.className = 'event-hint';
    this.results = document.createElement('div');
    this.results.className = 'event-results';
    root.append(this.title, this.hint, this.results);
    root.addEventListener('click', onTap);
    root.hidden = true;
  }

  update(lines: InvasionLines | null): void {
    const key = lines ? JSON.stringify(lines) : '';
    if (key === this.key) return;
    this.key = key;
    this.root.hidden = !lines;
    if (!lines) return;
    this.root.dataset.alert = String(lines.alert);
    this.title.textContent = lines.title;
    this.hint.textContent = lines.hint;
    this.results.replaceChildren();
    this.results.hidden = !lines.results?.length;
    for (const r of lines.results ?? []) {
      const row = document.createElement('div');
      row.className = r.self ? 'event-row event-self' : 'event-row';
      const name = document.createElement('span');
      name.textContent = `${r.place > 0 ? `${r.place}. ` : ''}${r.name}`;
      const numbers = document.createElement('span');
      numbers.textContent = `${r.damage} · ${formatCredits(r.reward)}`;
      row.append(name, numbers);
      this.results.append(row);
    }
  }
}
