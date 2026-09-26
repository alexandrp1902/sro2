import { keyHint, keymap } from '../input/keymap';

/** Телефон: плашка задания сворачивается в кружок, как табло события (invasion.ts). */
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

/**
 * Трекер цели (GDD §36, §54): шаг обучения или взятое задание — строка «что сделать» и подсказка «как».
 * Справа под миникартой. На ПК тап открывает карту галактики: там отмечена система цели.
 * На телефоне плашка закрывает пол-экрана над полем боя, поэтому тап сворачивает её в жёлтый кружок с «!»,
 * а тап по кружку разворачивает обратно; карта там и так открывается тапом по миникарте.
 */
export class ObjectiveHud {
  private key = '';
  private readonly title: HTMLElement;
  private readonly hint: HTMLElement;
  private collapsed = loadCollapsed();

  constructor(
    private readonly root: HTMLElement,
    onTap: () => void,
  ) {
    // Смысл несёт точка и цвет подписи, а не цветная полоса слева (SRO Steel: warn — задание).
    const head = document.createElement('div');
    head.className = 'objective-head';
    const dot = document.createElement('span');
    dot.className = 'sro-dot sro-dot--warn';
    this.title = document.createElement('span');
    this.title.className = 'objective-title sro-label sro-warn';
    head.append(dot, this.title);
    this.hint = document.createElement('div');
    this.hint.className = 'objective-hint sro-muted';
    const badge = document.createElement('span');
    badge.className = 'objective-badge';
    badge.textContent = '!';
    badge.setAttribute('aria-hidden', 'true');
    root.append(badge, head, this.hint);
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
  }

  /** Свёрнутость рисует только мобильный CSS: на ПК плашка раскрыта, даже если на телефоне её свернули. */
  private paint(): void {
    this.root.dataset.collapsed = String(this.collapsed);
  }

  /** null — прятать: заданий нет, в доке или нет связи. */
  update(lines: { title: string; hint: string } | null): void {
    // В подсказках обучения — клавиши из раскладки игрока, а не стандартные.
    if (lines) lines = { title: lines.title, hint: keyHint(lines.hint, keymap) };
    const key = lines ? `${lines.title}\n${lines.hint}` : '';
    if (key === this.key) return;
    this.key = key;
    this.root.hidden = !lines;
    if (!lines) return;
    this.title.textContent = lines.title;
    this.hint.textContent = lines.hint;
    this.hint.hidden = !lines.hint;
  }
}
