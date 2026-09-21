import { afterEach, describe, expect, it, vi } from 'vitest';
import { Keymap, mouseCode } from './keymap';
import { bindMouseButtons } from './mouseButtons';

/** Минимальная замена DOM: цель событий, которую можно дёрнуть руками. */
function target() {
  const handlers = new Map<string, ((e: unknown) => void)[]>();
  return {
    addEventListener(type: string, fn: (e: unknown) => void) {
      handlers.set(type, [...(handlers.get(type) ?? []), fn]);
    },
    fire(type: string, event: Record<string, unknown>) {
      for (const fn of handlers.get(type) ?? []) fn({ preventDefault: () => {}, ...event });
    },
  };
}

function memoryStore() {
  const data = new Map<string, string>();
  return { get: (k: string) => data.get(k) ?? null, set: (k: string, v: string) => void data.set(k, v), remove: (k: string) => void data.delete(k) };
}

const down = (button: number) => ({ button, pointerType: 'mouse' });

function setup() {
  const canvas = target();
  const win = target();
  vi.stubGlobal('window', win);
  const keys = new Keymap(memoryStore());
  const press = vi.fn();
  const hold = vi.fn();
  bindMouseButtons(canvas as unknown as HTMLElement, { press, hold }, keys);
  return { canvas, win, keys, press, hold };
}

afterEach(() => vi.unstubAllGlobals());

describe('bindMouseButtons', () => {
  it('назначенная ПКМ делает то же, что клавиша действия', () => {
    const { canvas, keys, press } = setup();
    keys.assign('fire', 1, { code: mouseCode(2) });
    canvas.fire('pointerdown', down(2));
    expect(press).toHaveBeenCalledWith('fire');
  });

  it('левая кнопка и неназначенные кнопки не трогаем: ЛКМ выбирает цель', () => {
    const { canvas, keys, press } = setup();
    keys.assign('fire', 1, { code: mouseCode(2) });
    canvas.fire('pointerdown', down(0));
    canvas.fire('pointerdown', down(1));
    canvas.fire('pointerdown', { button: 2, pointerType: 'touch' });
    expect(press).not.toHaveBeenCalled();
  });

  it('пока окно «Управление» ждёт кнопку, игра её не видит', () => {
    const { canvas, keys, press } = setup();
    keys.assign('fire', 1, { code: mouseCode(2) });
    keys.capturing = true;
    canvas.fire('pointerdown', down(2));
    expect(press).not.toHaveBeenCalled();
  });

  it('кнопка на движение держится и отпускается — в том числе когда вкладка потеряла фокус', () => {
    const { canvas, win, keys, hold } = setup();
    keys.assign('thrust', 1, { code: mouseCode(2) });
    canvas.fire('pointerdown', down(2));
    canvas.fire('pointerdown', down(2)); // повторное нажатие без отпускания — не второе «держим»
    expect(hold.mock.calls).toEqual([['Mouse2', true]]);
    win.fire('pointerup', { button: 2 });
    expect(hold.mock.calls).toEqual([['Mouse2', true], ['Mouse2', false]]);
    canvas.fire('pointerdown', down(2));
    win.fire('blur', {});
    expect(hold.mock.calls.at(-1)).toEqual(['Mouse2', false]);
  });
});
