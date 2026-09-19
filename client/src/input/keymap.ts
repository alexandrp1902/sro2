import { storage } from '../util/storage';

// Раскладка клавиш ПК (M10.5): какое действие на какой клавише. Хранится на устройстве (localStorage, «sro.keys»),
// на сервер не уходит. Клавиши — по e.code: раскладка не зависит от языка ввода.

export type KeyAction =
  | 'fire'
  | 'targetNext'
  | 'targetPrev'
  | 'targetClear'
  | 'thrust'
  | 'brake'
  | 'left'
  | 'right'
  | 'zoomIn'
  | 'zoomOut'
  | 'map'
  | 'nearestLoot';

/** Клавиша с Shift или без. Привязка без Shift срабатывает и с зажатым Shift — газ не отпускается от Shift. */
export interface KeyBinding {
  code: string;
  shift?: boolean;
}

/** У действия две ячейки: основная и запасная клавиша; null — пусто. */
export type KeySlots = [KeyBinding | null, KeyBinding | null];
export type Bindings = Record<KeyAction, KeySlots>;

/** Действия в порядке окна «Управление». */
export const KEY_ACTIONS: readonly { id: KeyAction; label: string }[] = [
  { id: 'fire', label: 'Огонь вкл/выкл · взять · док · прыжок' },
  { id: 'targetNext', label: 'Следующая цель' },
  { id: 'targetPrev', label: 'Предыдущая цель' },
  { id: 'targetClear', label: 'Снять цель' },
  { id: 'thrust', label: 'Тяга' },
  { id: 'brake', label: 'Тормоз' },
  { id: 'left', label: 'Поворот влево' },
  { id: 'right', label: 'Поворот вправо' },
  { id: 'zoomIn', label: 'Приблизить' },
  { id: 'zoomOut', label: 'Отдалить' },
  { id: 'map', label: 'Карта галактики' },
  { id: 'nearestLoot', label: 'Ближайший предмет' },
];

/** Движение корпуса: эти клавиши держат, а не нажимают. */
export const MOVE_ACTIONS: readonly KeyAction[] = ['thrust', 'brake', 'left', 'right'];

const key = (code: string, shift = false): KeyBinding => (shift ? { code, shift } : { code });

export function defaultBindings(): Bindings {
  return {
    fire: [key('Space'), null],
    targetNext: [key('KeyE'), key('Tab')],
    targetPrev: [key('KeyQ'), key('Tab', true)],
    targetClear: [key('Escape'), null],
    thrust: [key('KeyW'), key('ArrowUp')],
    brake: [key('KeyS'), key('ArrowDown')],
    left: [key('KeyA'), key('ArrowLeft')],
    right: [key('KeyD'), key('ArrowRight')],
    zoomIn: [key('Equal'), key('NumpadAdd')],
    zoomOut: [key('Minus'), key('NumpadSubtract')],
    map: [key('KeyM'), null],
    nearestLoot: [key('KeyF'), null],
  };
}

/** Модификаторы сами по себе клавишей не бывают: Shift — часть привязки, Ctrl/Alt/Meta заняты браузером. */
const MODIFIERS = new Set(['ShiftLeft', 'ShiftRight', 'ControlLeft', 'ControlRight', 'AltLeft', 'AltRight', 'MetaLeft', 'MetaRight']);

export function isModifier(code: string): boolean {
  return MODIFIERS.has(code);
}

function validBinding(value: unknown): KeyBinding | null {
  if (!value || typeof value !== 'object') return null;
  const { code, shift } = value as { code?: unknown; shift?: unknown };
  if (typeof code !== 'string' || !code || isModifier(code)) return null;
  return key(code, shift === true);
}

/** Раскладка из localStorage: чего нет или что битое — по умолчанию. */
export function parseBindings(json: string | null): Bindings {
  const result = defaultBindings();
  if (!json) return result;
  let data: unknown;
  try {
    data = JSON.parse(json);
  } catch {
    return result;
  }
  if (!data || typeof data !== 'object') return result;
  for (const { id } of KEY_ACTIONS) {
    const slots = (data as Record<string, unknown>)[id];
    if (!Array.isArray(slots)) continue;
    result[id] = [validBinding(slots[0]), validBinding(slots[1])];
  }
  return result;
}

export function sameBinding(a: KeyBinding | null, b: KeyBinding | null): boolean {
  return !!a && !!b && a.code === b.code && !!a.shift === !!b.shift;
}

/** Где клавиша уже занята. */
export interface KeyPlace {
  action: KeyAction;
  slot: 0 | 1;
}

const STORAGE_KEY = 'sro.keys';

interface KeyStore {
  get(key: string): string | null;
  set(key: string, value: string): void;
  remove(key: string): void;
}

export class Keymap {
  bindings: Bindings;
  /** Окно «Управление» ждёт нажатия: игра клавиш не слышит. */
  capturing = false;
  private readonly listeners = new Set<() => void>();

  constructor(private readonly store: KeyStore = storage) {
    this.bindings = parseBindings(store.get(STORAGE_KEY));
  }

  /** Раскладка сменилась — подсказки и окно перерисовываются. */
  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /**
   * Действие клавиши. Сначала привязки с Shift (Shift+Tab — предыдущая цель, хотя Tab — следующая),
   * потом без: они срабатывают при любом Shift.
   */
  actionFor(e: { code: string; shiftKey: boolean }): KeyAction | null {
    let plain: KeyAction | null = null;
    for (const { id } of KEY_ACTIONS) {
      for (const b of this.bindings[id]) {
        if (!b || b.code !== e.code) continue;
        if (b.shift) {
          if (e.shiftKey) return id;
        } else {
          plain ??= id;
        }
      }
    }
    return plain;
  }

  /** Клавиша держит это действие — для движения, где важно, что зажато, а не что нажали. */
  holds(action: KeyAction, code: string): boolean {
    return this.bindings[action].some((b) => b?.code === code);
  }

  /** Кем занята клавиша, кроме этой ячейки. */
  conflict(binding: KeyBinding, except?: KeyPlace): KeyPlace | null {
    for (const { id } of KEY_ACTIONS) {
      for (const slot of [0, 1] as const) {
        if (except && except.action === id && except.slot === slot) continue;
        if (sameBinding(this.bindings[id][slot], binding)) return { action: id, slot };
      }
    }
    return null;
  }

  /**
   * Записать клавишу в ячейку. Если она занята другим действием — поменять их местами: там окажется
   * прежняя клавиша этой ячейки (или пусто).
   */
  assign(action: KeyAction, slot: 0 | 1, binding: KeyBinding): void {
    const taken = this.conflict(binding, { action, slot });
    if (taken) this.bindings[taken.action][taken.slot] = this.bindings[action][slot];
    this.bindings[action][slot] = key(binding.code, !!binding.shift);
    this.save();
  }

  clear(action: KeyAction, slot: 0 | 1): void {
    this.bindings[action][slot] = null;
    this.save();
  }

  reset(): void {
    this.bindings = defaultBindings();
    this.store.remove(STORAGE_KEY);
    this.changed();
  }

  /** Подпись действия для подсказок: первая назначенная клавиша; «—», если ничего. */
  label(action: KeyAction): string {
    const b = this.bindings[action].find((x) => x);
    return b ? bindingLabel(b) : '—';
  }

  private save(): void {
    this.store.set(STORAGE_KEY, JSON.stringify(this.bindings));
    this.changed();
  }

  private changed(): void {
    for (const listener of this.listeners) listener();
  }
}

const NAMED: Record<string, string> = {
  Space: 'Пробел',
  Escape: 'Esc',
  Tab: 'Tab',
  Enter: 'Enter',
  Backspace: 'Backspace',
  ArrowUp: '↑',
  ArrowDown: '↓',
  ArrowLeft: '←',
  ArrowRight: '→',
  Equal: '+',
  Minus: '−',
  NumpadAdd: 'Num +',
  NumpadSubtract: 'Num −',
  NumpadMultiply: 'Num *',
  NumpadDivide: 'Num /',
  NumpadEnter: 'Num Enter',
  NumpadDecimal: 'Num .',
  Backquote: '`',
  BracketLeft: '[',
  BracketRight: ']',
  Semicolon: ';',
  Quote: "'",
  Comma: ',',
  Period: '.',
  Slash: '/',
  Backslash: '\\',
  CapsLock: 'Caps Lock',
  PageUp: 'PgUp',
  PageDown: 'PgDn',
  Delete: 'Del',
  Insert: 'Ins',
};

/** Короткое имя клавиши: KeyE → «E», Digit1 → «1», Numpad4 → «Num 4». */
export function keyLabel(code: string): string {
  if (NAMED[code]) return NAMED[code];
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit\d$/.test(code)) return code.slice(5);
  if (/^Numpad\d$/.test(code)) return `Num ${code.slice(6)}`;
  return code;
}

export function bindingLabel(b: KeyBinding): string {
  return (b.shift ? 'Shift+' : '') + keyLabel(b.code);
}

/**
 * Подсказки обучения приходят с сервера со стандартными клавишами («пробел», «Q / E») — подставляем текущие.
 */
export function keyHint(text: string, keys: Keymap): string {
  const fire = keys.label('fire');
  return text
    .replace(/пробел/g, fire === 'Пробел' ? 'пробел' : fire)
    .replace(/Q \/ E/g, `${keys.label('targetPrev')} / ${keys.label('targetNext')}`);
}

/** Раскладка этого устройства. */
export const keymap = new Keymap();
