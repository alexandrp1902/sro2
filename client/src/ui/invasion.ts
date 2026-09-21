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

/** Телефон: табло свёрнуто в значок, пока по нему не тапнут. */
const NARROW = '(max-width: 520px)';
const narrowScreen = (): boolean => typeof matchMedia === 'function' && matchMedia(NARROW).matches;

/**
 * Табло вторжения справа под трекером цели: заголовок, отсчёт или волна, в конце — таблица итогов.
 * На телефоне оно занимало полэкрана над миникартой, поэтому свёрнуто в красный треугольник с «!»:
 * тап разворачивает его на месте, следующий — снова сворачивает (M16c). На ПК тап открывает карту.
 */
export class InvasionHud {
  private key = '';
  private readonly title: HTMLElement;
  private readonly hint: HTMLElement;
  private readonly results: HTMLElement;
  private open = false;
  /** Заголовок показанного события: сменился — значит событие другое, и табло снова сворачивается. */
  private topic = '';

  constructor(
    private readonly root: HTMLElement,
    onTap: () => void,
  ) {
    root.append(alertBadge());
    this.title = document.createElement('div');
    this.title.className = 'event-title sro-label';
    this.hint = document.createElement('div');
    this.hint.className = 'event-hint sro-num';
    this.results = document.createElement('div');
    this.results.className = 'event-results sro-num';
    root.append(this.title, this.hint, this.results);
    root.addEventListener('click', () => {
      // Свёрнутый значок сначала раскрывается; на ПК он всегда раскрыт, и тап сразу ведёт на карту.
      if (narrowScreen()) this.setOpen(!this.open);
      else onTap();
    });
    this.setOpen(false);
    root.hidden = true;
  }

  update(lines: InvasionLines | null): void {
    const key = lines ? JSON.stringify(lines) : '';
    if (key === this.key) return;
    this.key = key;
    this.root.hidden = !lines;
    // Новое событие приходит свёрнутым; отсчёт в подсказке тикает каждую секунду и складывать
    // раскрытое табло не должен — поэтому смотрим на заголовок, а не на весь текст.
    const topic = lines?.title ?? '';
    if (topic !== this.topic) {
      this.topic = topic;
      this.setOpen(false);
    }
    if (!lines) return;
    this.root.dataset.alert = String(lines.alert);
    this.root.classList.toggle('sro-pane--alert', lines.alert);
    this.title.textContent = lines.title;
    this.hint.textContent = lines.hint;
    this.results.replaceChildren();
    this.results.hidden = !lines.results?.length;
    for (const r of lines.results ?? []) {
      const row = document.createElement('div');
      row.className = r.self ? 'event-row event-self sro-strong' : 'event-row';
      const name = document.createElement('span');
      name.textContent = `${r.place > 0 ? `${r.place}. ` : ''}${r.name}`;
      const numbers = document.createElement('span');
      numbers.textContent = `${r.damage} · ${formatCredits(r.reward)}`;
      row.append(name, numbers);
      this.results.append(row);
    }
  }

  private setOpen(open: boolean): void {
    this.open = open;
    this.root.dataset.open = String(open);
  }
}

/** Значок свёрнутого табло: «!» в скруглённом треугольнике. Виден только на телефоне (style.css). */
function alertBadge(): SVGSVGElement {
  const NS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('class', 'event-badge');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const triangle = document.createElementNS(NS, 'path');
  // Скруглённые углы: из середины каждой стороны дугой в следующую.
  triangle.setAttribute('d', 'M12 3.2 21 19a2 2 0 0 1-1.7 3H4.7A2 2 0 0 1 3 19Z');
  triangle.setAttribute('stroke-linejoin', 'round');
  triangle.setAttribute('stroke-width', '2');
  const mark = document.createElementNS(NS, 'path');
  mark.setAttribute('d', 'M12 9.5v5');
  mark.setAttribute('stroke-linecap', 'round');
  mark.setAttribute('stroke-width', '2.2');
  const dot = document.createElementNS(NS, 'circle');
  dot.setAttribute('cx', '12');
  dot.setAttribute('cy', '18');
  dot.setAttribute('r', '1.2');
  svg.append(triangle, mark, dot);
  return svg;
}
