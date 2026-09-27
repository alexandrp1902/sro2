import { keyHint, keymap } from '../input/keymap';
import { missionSprite, spriteUrl } from '../render/sprites';
import type { TrackerLines } from '../sim/missions';

/** Телефон: плашка задания сворачивается в кружки, как табло события (invasion.ts). */
const NARROW = '(max-width: 520px)';
const narrowScreen = (): boolean => typeof matchMedia === 'function' && matchMedia(NARROW).matches;

/** Свёрнута ли плашка на телефоне: выбор пилота переживает перезагрузку. */
const COLLAPSED_KEY = 'sro.objective.collapsed';

function loadCollapsed(): boolean {
  try {
    return localStorage.getItem(COLLAPSED_KEY) === '1';
  } catch {
    return false;
  }
}

function saveCollapsed(collapsed: boolean): void {
  try {
    localStorage.setItem(COLLAPSED_KEY, collapsed ? '1' : '0');
  } catch {
    // хранилище недоступно — плашка просто развернётся после перезагрузки
  }
}

/** Одна строка трекера со своим кружком для свёрнутого вида. */
class Row {
  // Не пустая: первое update(null) должно спрятать строку, а пустой ключ и есть «прятать».
  key = '\0';
  readonly view: HTMLElement;
  private readonly title: HTMLElement;
  private readonly hint: HTMLElement;
  private readonly dot: HTMLElement;
  private readonly icon: HTMLElement;

  /** story — сюжет: зелёный, со значком «галочка в кружке» вместо значка вида задания. */
  constructor(
    readonly badge: HTMLElement,
    private readonly story: boolean,
  ) {
    const tone = story ? 'ok' : 'warn';
    this.view = document.createElement('div');
    this.view.className = story ? 'objective-row objective-row--story' : 'objective-row';
    const head = document.createElement('div');
    head.className = 'objective-head';
    this.dot = document.createElement('span');
    this.dot.className = `sro-dot sro-dot--${tone}`;
    // У задания вместо точки — значок его вида, как на доске; у сюжета — галочка; у обучения — точка.
    this.icon = document.createElement('span');
    this.icon.className = story ? 'mission-icon mission-icon--story objective-icon' : `mission-icon objective-icon sro-${tone}`;
    this.icon.setAttribute('aria-hidden', 'true');
    this.icon.hidden = !story;
    this.dot.hidden = story;
    this.title = document.createElement('span');
    this.title.className = `objective-title sro-label sro-${tone}`;
    head.append(this.dot, this.icon, this.title);
    this.hint = document.createElement('div');
    this.hint.className = 'objective-hint sro-muted';
    this.view.append(head, this.hint);
  }

  /** @returns true — строка сменилась. */
  update(lines: TrackerLines | null): boolean {
    // В подсказках обучения — клавиши из раскладки игрока, а не стандартные.
    if (lines) lines = { ...lines, hint: keyHint(lines.hint, keymap) };
    const key = lines ? `${lines.kind ?? ''}\n${lines.title}\n${lines.hint}` : '';
    if (key === this.key) return false;
    this.key = key;
    this.view.hidden = !lines;
    this.badge.hidden = !lines;
    if (!lines) return true;
    if (!this.story) {
      const sprite = lines.kind ? missionSprite(lines.kind) : null;
      if (sprite) this.icon.style.setProperty('--mission-icon', `url("${new URL(spriteUrl(sprite), document.baseURI).href}")`);
      this.icon.hidden = !sprite;
      this.dot.hidden = !!sprite;
    }
    this.title.textContent = lines.title;
    this.hint.textContent = lines.hint;
    this.hint.hidden = !lines.hint;
    return true;
  }
}

/**
 * Трекер цели (GDD §36, §54): шаг обучения или задание с доски — жёлтая строка, сюжет — зелёная
 * (плейтест 2026-09-26: у сюжета свой слот, и игрок всегда знает, куда дальше). Справа под миникартой.
 * На ПК тап открывает карту галактики: там отмечены системы целей. На телефоне плашка закрывает пол-экрана
 * над полем боя, поэтому тап сворачивает её в кружки — жёлтый «!» за доску и зелёную галочку за сюжет, —
 * а тап по ним разворачивает обратно; карта там и так открывается тапом по миникарте.
 */
export class ObjectiveHud {
  private readonly board: Row;
  private readonly story: Row;
  private collapsed = loadCollapsed();

  constructor(
    private readonly root: HTMLElement,
    onTap: () => void,
  ) {
    const badges = document.createElement('div');
    badges.className = 'objective-badges';
    badges.setAttribute('aria-hidden', 'true');
    const warn = document.createElement('span');
    warn.className = 'objective-badge objective-badge--board';
    warn.textContent = '!';
    const check = document.createElement('span');
    check.className = 'objective-badge objective-badge--story';
    check.append(document.createElement('span'));
    badges.append(warn, check);
    this.board = new Row(warn, false);
    this.story = new Row(check, true);
    root.append(badges, this.board.view, this.story.view);
    root.addEventListener('click', () => {
      if (!narrowScreen()) {
        onTap();
        return;
      }
      this.collapsed = !this.collapsed;
      saveCollapsed(this.collapsed);
      this.paint();
    });
    this.paint();
    this.update(null, null);
  }

  /** Свёрнутость рисует только мобильный CSS: на ПК плашка раскрыта, даже если на телефоне её свернули. */
  private paint(): void {
    this.root.dataset.collapsed = String(this.collapsed);
  }

  /**
   * board — обучение или задание с доски, story — сюжет. null — строку прятать; обе null — прятать всё
   * (заданий нет, в доке или нет связи).
   */
  update(board: TrackerLines | null, story: TrackerLines | null): void {
    const changed = [this.board, this.story].filter((row, i) => row.update(i === 0 ? board : story));
    this.root.hidden = !board && !story;
    // Свёрнутый кружок молчит о новом — пусть мигнёт тот, чья строка сменилась.
    if (!this.collapsed) return;
    for (const row of changed) {
      if (row.badge.hidden) continue;
      row.badge.classList.remove('objective--new');
      void row.badge.offsetWidth; // перезапуск анимации, если шаги идут подряд
      row.badge.classList.add('objective--new');
    }
  }
}
