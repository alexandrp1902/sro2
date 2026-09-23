import { COMBAT_THEMES, type AudioPrefs, type AudioPrefsPort, type CombatTheme } from '../audio/settings';

/**
 * Окно «Звук» (M17): общая громкость, музыка и звуки. Устроено как окно «Управление» (ui/controlsWindow.ts) —
 * то же затемнение, та же шапка, щелчок мимо карточки закрывает.
 *
 * Работает через порт настроек, а не через звуковой движок: витрине ?demo=audio никакой AudioContext не нужен.
 */

interface Row {
  id: keyof AudioPrefs;
  label: string;
  hint: string;
}

const ROWS: Row[] = [
  { id: 'master', label: 'Общая', hint: 'Весь звук игры' },
  { id: 'music', label: 'Музыка', hint: 'Космос в покое, бой — сам по себе' },
  { id: 'sfx', label: 'Звуки', hint: 'Выстрелы, взрывы, стыковка' },
];

/** Две боевые темы на выбор — плейтест решит, какая останется. */
const THEME_LABELS: Record<CombatTheme, string> = { organ: 'Орган', march: 'Марш' };
const THEME_HINT = 'Орган — барабаны и церковный орган, тревога. Марш — медь, малый барабан и струнные, космическая опера.';

export class AudioWindow {
  constructor(
    private readonly root: HTMLElement,
    private readonly prefs: AudioPrefsPort,
    /** Кнопка «Проверить»: играет взрыв на текущей громкости. */
    private readonly onPreview: () => void = () => {},
  ) {
    root.addEventListener('pointerdown', (e) => {
      if (e.target === root) this.hide();
    });
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  toggle(): void {
    if (this.open) this.hide();
    else this.show();
  }

  show(): void {
    this.root.hidden = false;
    this.render();
  }

  hide(): void {
    this.root.hidden = true;
  }

  private render(): void {
    const card = el('div', 'audio-card sro-pane sro-pane--window');
    const head = el('div', 'galaxy-head sro-head');
    head.append(el('div', 'galaxy-title sro-head__title', 'Звук'));
    const close = button('✕', 'galaxy-close sro-btn sro-btn--icon', () => this.hide());
    close.setAttribute('aria-label', 'Закрыть');
    head.append(close);
    card.append(head);

    const table = el('div', 'audio-table');
    for (const row of ROWS) table.append(this.slider(row));
    table.append(this.themeRow());
    card.append(table);

    const note = el('div', 'audio-note sro-muted');
    note.textContent = 'Музыка сама переходит на боевую, когда по вам стреляют, и отпускает через несколько секунд после боя.';
    card.append(note);

    const foot = el('div', 'audio-foot');
    foot.append(button('Проверить', 'sro-btn sro-btn--sm', () => this.onPreview()));
    card.append(foot);

    this.root.replaceChildren(card);
  }

  private slider(row: Row): HTMLElement {
    const line = el('div', 'audio-row');
    const label = el('label', 'audio-label', row.label) as HTMLLabelElement;
    const value = el('div', 'audio-value sro-num');
    const input = document.createElement('input');
    input.type = 'range';
    input.className = 'sro-range';
    input.min = '0';
    input.max = '100';
    input.step = '5';
    const current = this.prefs.prefs[row.id];
    input.value = String(Math.round((typeof current === 'number' ? current : 0) * 100));
    value.textContent = `${input.value} %`;
    label.htmlFor = `audio-${row.id}`;
    input.id = `audio-${row.id}`;
    input.addEventListener('input', () => {
      value.textContent = `${input.value} %`;
      this.prefs.set({ [row.id]: Number(input.value) / 100 } as Partial<AudioPrefs>);
    });
    line.append(label, input, value, el('div', 'audio-hint sro-muted', row.hint));
    return line;
  }

  private themeRow(): HTMLElement {
    const line = el('div', 'audio-row audio-row--themes');
    const label = el('div', 'audio-label', 'Бой');
    const tabs = el('div', 'audio-themes sro-tabs');
    tabs.setAttribute('role', 'group');
    tabs.setAttribute('aria-label', 'Боевая тема');
    for (const theme of COMBAT_THEMES) {
      const tab = button(THEME_LABELS[theme], 'sro-tab', () => {
        this.prefs.set({ combat: theme });
        this.render();
      });
      tab.setAttribute('aria-pressed', String(this.prefs.prefs.combat === theme));
      tab.dataset.theme = theme;
      tabs.append(tab);
    }
    line.append(label, tabs, el('div', 'audio-value'), el('div', 'audio-hint sro-muted', THEME_HINT));
    return line;
  }
}

function el(tag: string, className: string, text?: string): HTMLElement {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

/** Кнопка без фокуса после клика: иначе пробел нажал бы её снова вместо огня. */
function button(text: string, className: string, onClick: () => void): HTMLButtonElement {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = className;
  b.textContent = text;
  b.addEventListener('click', () => {
    b.blur();
    onClick();
  });
  return b;
}
