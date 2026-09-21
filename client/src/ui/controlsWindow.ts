import {
  KEY_ACTIONS,
  bindingLabel,
  isModifier,
  keymap as defaultKeymap,
  mouseCode,
  type KeyAction,
  type KeyBinding,
  type Keymap,
} from '../input/keymap';

/** Ячейка, которая ждёт клавишу, и что делать, если клавиша уже занята. */
interface Capture {
  action: KeyAction;
  slot: 0 | 1;
  /** Нажата занятая клавиша — ждём «Поменять местами» или «Отмена». */
  pending: KeyBinding | null;
}

/**
 * Окно «Управление» (M10.5, только ПК): какая клавиша что делает, переназначение, сброс. Клик по ячейке —
 * «нажмите клавишу…»; Esc отменяет, Backspace очищает ячейку. Раскладка сохраняется на устройстве сразу.
 */
export class ControlsWindow {
  private capture: Capture | null = null;

  constructor(
    private readonly root: HTMLElement,
    private readonly keys: Keymap = defaultKeymap,
  ) {
    root.addEventListener('pointerdown', (e) => {
      if (e.target === root) this.hide();
    });
    // Фаза перехвата на window — раньше игровых обработчиков: пока ждём клавишу, игра её не видит.
    window.addEventListener('keydown', (e) => this.onKey(e), { capture: true });
    // Кнопки мыши назначаются так же, как клавиши: ПКМ рядом с пробелом — привычный «взять / огонь».
    window.addEventListener('pointerdown', (e) => this.onMouse(e), { capture: true });
    keys.onChange(() => {
      if (this.open) this.render();
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
    this.setCapture(null);
    this.root.hidden = true;
  }

  private setCapture(capture: Capture | null): void {
    this.capture = capture;
    this.keys.capturing = capture !== null;
    if (this.open) this.render();
  }

  private onKey(e: KeyboardEvent): void {
    const capture = this.capture;
    if (!capture) return;
    e.preventDefault();
    e.stopPropagation();
    if (e.repeat || isModifier(e.code)) return; // Shift ждём вместе с клавишей
    if (e.code === 'Escape') {
      this.setCapture(null);
      return;
    }
    if (e.code === 'Backspace' && !e.shiftKey) {
      this.keys.clear(capture.action, capture.slot);
      this.setCapture(null);
      return;
    }
    if (e.ctrlKey || e.altKey || e.metaKey) return; // такие сочетания забирает браузер
    this.take(capture, e.shiftKey ? { code: e.code, shift: true } : { code: e.code });
  }

  /**
   * Кнопка мыши в ожидающей ячейке. ЛКМ не назначается — ею выбирают цель в космосе; её пропускаем как есть,
   * иначе нечем было бы нажать «Поменять местами» и выбрать другую ячейку. Назначенная кнопка работает
   * только по игровому полю (input/mouseButtons.ts).
   */
  private onMouse(e: PointerEvent): void {
    const capture = this.capture;
    if (!capture || e.pointerType !== 'mouse' || e.button === 0) return;
    e.preventDefault();
    e.stopPropagation();
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    const code = mouseCode(e.button);
    this.take(capture, e.shiftKey ? { code, shift: true } : { code });
  }

  /** Занять ячейку привязкой: если клавиша уже занята — сначала спросить, менять ли местами. */
  private take(capture: Capture, binding: KeyBinding): void {
    if (this.keys.conflict(binding, capture)) {
      this.setCapture({ ...capture, pending: binding });
      return;
    }
    this.keys.assign(capture.action, capture.slot, binding);
    this.setCapture(null);
  }

  private render(): void {
    const card = el('div', 'controls-card sro-pane sro-pane--window');
    const head = el('div', 'galaxy-head sro-head');
    head.append(el('div', 'galaxy-title sro-head__title', 'Управление'));
    const close = button('✕', 'galaxy-close sro-btn sro-btn--icon', () => this.hide());
    close.setAttribute('aria-label', 'Закрыть');
    head.append(close);
    card.append(head);

    const table = el('div', 'controls-table');
    for (const { id, label } of KEY_ACTIONS) {
      const row = el('div', 'controls-row');
      row.append(el('div', 'controls-action', label));
      for (const slot of [0, 1] as const) {
        const binding = this.keys.bindings[id][slot];
        const waiting = this.capture?.action === id && this.capture.slot === slot;
        const text = waiting ? 'нажмите клавишу…' : binding ? bindingLabel(binding) : '—';
        const cell = button(text, 'controls-key sro-btn sro-btn--sm', () => this.setCapture(waiting ? null : { action: id, slot, pending: null }));
        cell.dataset.waiting = String(waiting);
        cell.dataset.empty = String(!binding);
        row.append(cell);
      }
      table.append(row);
    }
    card.append(table);

    const note = el('div', 'controls-note sro-muted');
    const pending = this.capture?.pending;
    if (pending && this.capture) {
      const taken = this.keys.conflict(pending, this.capture)!;
      const takenLabel = KEY_ACTIONS.find((a) => a.id === taken.action)!.label;
      note.dataset.warn = 'true';
      note.append(el('span', '', `«${bindingLabel(pending)}» уже занята: ${takenLabel}. `));
      const capture = this.capture;
      note.append(
        button('Поменять местами', 'controls-swap sro-btn sro-btn--sm', () => {
          this.keys.assign(capture.action, capture.slot, pending);
          this.setCapture(null);
        }),
        button('Отмена', 'controls-cancel sro-btn sro-btn--ghost sro-btn--sm', () => this.setCapture(null)),
      );
    } else if (this.capture) {
      note.textContent =
        'Нажмите клавишу или кнопку мыши, кроме левой (можно с Shift). Esc — отмена, Backspace — очистить ячейку.';
    } else {
      note.textContent =
        'Нажмите на ячейку, чтобы назначить клавишу или кнопку мыши; назначенная кнопка мыши работает по космосу. ' +
        'Кнопки 1–4, X и колесо мыши меняют тягу; Ctrl+колесо — масштаб.';
    }
    card.append(note);

    const foot = el('div', 'controls-foot');
    foot.append(button('Сбросить по умолчанию', 'controls-reset sro-btn sro-btn--sm', () => {
      this.capture = null;
      this.keys.capturing = false;
      this.keys.reset();
    }));
    card.append(foot);

    this.root.replaceChildren(card);
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
