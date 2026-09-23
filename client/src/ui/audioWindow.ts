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
  { id: 'sfx', label: 'Звуки', hint: 'Выстрелы, взрывы, стыковка, двигатель' },
  { id: 'radio', label: 'Эфир', hint: 'Голоса торговцев, рейнджеров и пиратов' },
];

/** Три боевые темы на выбор — плейтест решит, какая останется. */
const THEME_LABELS: Record<CombatTheme, string> = { taiko: 'Тайко', chase: 'Погоня', duel: 'Дуэль' };
const THEME_HINT = 'Тайко — барабаны, струнные и флейта. Погоня — быстрее всех, бас и валторны. Дуэль — тяжёлая полудоля, хор и смычок.';

export class AudioWindow {
  constructor(
    private readonly root: HTMLElement,
    private readonly prefs: AudioPrefsPort,
    /** Кнопки проверки: взрыв или реплика эфира на текущей громкости. */
    private readonly onPreview: (what: 'sfx' | 'radio') => void = () => {},
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
    table.append(
      this.choiceRow('Бой', COMBAT_THEMES, THEME_LABELS, this.prefs.prefs.combat, (combat) => this.prefs.set({ combat }), THEME_HINT, 'Боевая тема'),
    );
    table.append(
      this.choiceRow(
        'Субтитры',
        ['on', 'off'] as const,
        { on: 'Вкл', off: 'Выкл' },
        this.prefs.prefs.subtitles ? 'on' : 'off',
        (v) => this.prefs.set({ subtitles: v === 'on' }),
        'Реплики эфира текстом в ленте — читать можно и с выключенным голосом',
        'Субтитры эфира',
      ),
    );
    card.append(table);

    const note = el('div', 'audio-note sro-muted');
    note.textContent = 'Музыка сама переходит на боевую, когда по вам стреляют, и отпускает через несколько секунд после боя.';
    card.append(note);

    const foot = el('div', 'audio-foot');
    foot.append(button('Проверить звук', 'sro-btn sro-btn--sm', () => this.onPreview('sfx')));
    foot.append(button('Проверить эфир', 'sro-btn sro-btn--sm', () => this.onPreview('radio')));
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

  /** Строка с сегментным выбором вместо ползунка: боевая тема, субтитры. */
  private choiceRow<T extends string>(
    label: string,
    options: readonly T[],
    labels: Record<T, string>,
    current: T,
    onPick: (value: T) => void,
    hint: string,
    aria: string,
  ): HTMLElement {
    const line = el('div', 'audio-row audio-row--themes');
    const tabs = el('div', 'audio-themes sro-tabs');
    tabs.setAttribute('role', 'group');
    tabs.setAttribute('aria-label', aria);
    for (const option of options) {
      const tab = button(labels[option], 'sro-tab', () => {
        onPick(option);
        this.render();
      });
      tab.setAttribute('aria-pressed', String(current === option));
      tab.dataset.value = option;
      tabs.append(tab);
    }
    line.append(el('div', 'audio-label', label), tabs, el('div', 'audio-value'), el('div', 'audio-hint sro-muted', hint));
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
