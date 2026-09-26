import { keyHint, keymap } from '../input/keymap';
import { missionSprite, spriteUrl } from '../render/sprites';

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
  private readonly dot: HTMLElement;
  private readonly icon: HTMLElement;
  private collapsed = loadCollapsed();

  constructor(
    private readonly root: HTMLElement,
    onTap: () => void,
  ) {
    // Смысл несёт точка и цвет подписи, а не цветная полоса слева (SRO Steel: warn — задание).
    const head = document.createElement('div');
    head.className = 'objective-head';
    this.dot = document.createElement('span');
    this.dot.className = 'sro-dot sro-dot--warn';
    // У задания вместо точки — значок его вида, как на доске; у обучения — точка.
    this.icon = document.createElement('span');
    this.icon.className = 'mission-icon objective-icon sro-warn';
    this.icon.setAttribute('aria-hidden', 'true');
    this.icon.hidden = true;
    this.title = document.createElement('span');
    this.title.className = 'objective-title sro-label sro-warn';
    head.append(this.dot, this.icon, this.title);
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
  update(lines: { title: string; hint: string; kind?: string } | null): void {
    // В подсказках обучения — клавиши из раскладки игрока, а не стандартные.
    if (lines) lines = { ...lines, hint: keyHint(lines.hint, keymap) };
    const key = lines ? `${lines.kind ?? ''}\n${lines.title}\n${lines.hint}` : '';
    if (key === this.key) return;
    this.key = key;
    this.root.hidden = !lines;
    if (!lines) return;
    const sprite = lines.kind ? missionSprite(lines.kind) : null;
    if (sprite) this.icon.style.setProperty('--mission-icon', `url("${new URL(spriteUrl(sprite), document.baseURI).href}")`);
    this.icon.hidden = !sprite;
    this.dot.hidden = !!sprite;
    this.title.textContent = lines.title;
    this.hint.textContent = lines.hint;
    this.hint.hidden = !lines.hint;
    // Свёрнутый кружок молчит о новом шаге — пусть мигнёт, чтобы пилот знал: там что-то другое.
    if (this.collapsed) {
      this.root.classList.remove('objective--new');
      void this.root.offsetWidth; // перезапуск анимации, если шаги идут подряд
      this.root.classList.add('objective--new');
    }
  }
}
