import { describe, expect, it } from 'vitest';
import { Keymap, bindingLabel, defaultBindings, keyHint, keyLabel, mouseCode, parseBindings } from './keymap';

function memoryStore(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial));
  return {
    data,
    get: (k: string) => data.get(k) ?? null,
    set: (k: string, v: string) => void data.set(k, v),
    remove: (k: string) => void data.delete(k),
  };
}

const press = (code: string, shiftKey = false) => ({ code, shiftKey });

describe('parseBindings', () => {
  it('falls back to defaults on missing or broken data', () => {
    expect(parseBindings(null)).toEqual(defaultBindings());
    expect(parseBindings('{not json')).toEqual(defaultBindings());
    expect(parseBindings('42')).toEqual(defaultBindings());
  });

  it('keeps valid slots, drops junk and bare modifiers', () => {
    const b = parseBindings(JSON.stringify({ fire: [{ code: 'KeyV' }, { code: 'ShiftLeft' }], thrust: 'nope' }));
    expect(b.fire).toEqual([{ code: 'KeyV' }, null]);
    expect(b.thrust).toEqual(defaultBindings().thrust);
  });
});

describe('Keymap', () => {
  it('prefers a Shift binding and lets plain bindings work with Shift held', () => {
    const keys = new Keymap(memoryStore());
    expect(keys.actionFor(press('Tab'))).toBe('targetNext');
    expect(keys.actionFor(press('Tab', true))).toBe('targetPrev');
    expect(keys.actionFor(press('KeyW', true))).toBe('thrust');
    expect(keys.actionFor(press('KeyZ'))).toBeNull();
  });

  it('saves a rebound key and reads it back after a reload', () => {
    const store = memoryStore();
    const keys = new Keymap(store);
    keys.assign('fire', 0, { code: 'KeyV' });
    expect(new Keymap(store).actionFor(press('KeyV'))).toBe('fire');
    expect(new Keymap(store).actionFor(press('Space'))).toBeNull();
  });

  it('reports a conflict and swaps keys on assign', () => {
    const keys = new Keymap(memoryStore());
    expect(keys.conflict({ code: 'KeyE' })).toEqual({ action: 'targetNext', slot: 0 });
    expect(keys.conflict({ code: 'Space' }, { action: 'fire', slot: 0 })).toBeNull();
    keys.assign('fire', 0, { code: 'KeyE' });
    expect(keys.actionFor(press('KeyE'))).toBe('fire');
    expect(keys.actionFor(press('Space'))).toBe('targetNext'); // Space переехал на место E
  });

  it('clears a slot and resets to defaults', () => {
    const store = memoryStore();
    const keys = new Keymap(store);
    keys.clear('thrust', 1);
    expect(keys.holds('thrust', 'ArrowUp')).toBe(false);
    let changes = 0;
    keys.onChange(() => changes++);
    keys.reset();
    expect(keys.bindings).toEqual(defaultBindings());
    expect(store.data.has('sro.keys')).toBe(false);
    expect(changes).toBe(1);
  });
});

describe('кнопки мыши', () => {
  it('ПКМ живёт в раскладке как обычная клавиша: рядом с пробелом, сохраняется и подписывается', () => {
    const store = memoryStore();
    const keys = new Keymap(store);
    keys.assign('fire', 1, { code: mouseCode(2) });
    expect(keys.bindings.fire).toEqual([{ code: 'Space' }, { code: 'Mouse2' }]);
    expect(keys.actionFor(press('Mouse2'))).toBe('fire');
    // Пробел не потерялся: ПКМ встала во вторую ячейку.
    expect(keys.actionFor(press('Space'))).toBe('fire');
    expect(new Keymap(store).bindings.fire).toEqual([{ code: 'Space' }, { code: 'Mouse2' }]);
  });

  it('кнопка мыши на удержание опознаётся так же, как клавиша', () => {
    const keys = new Keymap(memoryStore());
    keys.assign('thrust', 1, { code: mouseCode(2) });
    expect(keys.holds('thrust', 'Mouse2')).toBe(true);
    expect(keys.holds('brake', 'Mouse2')).toBe(false);
  });
});

describe('labels', () => {
  it('names keys shortly', () => {
    expect(keyLabel('KeyE')).toBe('E');
    expect(keyLabel('Digit3')).toBe('3');
    expect(keyLabel('Numpad4')).toBe('Num 4');
    expect(keyLabel('Space')).toBe('Пробел');
    expect(keyLabel(mouseCode(2))).toBe('ПКМ');
    expect(bindingLabel({ code: 'Tab', shift: true })).toBe('Shift+Tab');
  });

  it('puts current keys into tutorial hints', () => {
    const keys = new Keymap(memoryStore());
    const hint = 'Выберите цель: тапом, кликом или Q / E и включите огонь: пробел или «ОГОНЬ»';
    expect(keyHint(hint, keys)).toBe(hint);
    keys.assign('fire', 0, { code: 'KeyV' });
    keys.assign('targetPrev', 0, { code: 'KeyZ' });
    expect(keyHint(hint, keys)).toBe('Выберите цель: тапом, кликом или Z / E и включите огонь: V или «ОГОНЬ»');
  });

  it('names the brake key where the hint says {brake} (M18)', () => {
    const keys = new Keymap(memoryStore());
    expect(keyHint('удерживайте {brake}', keys)).toBe('удерживайте S');
    keys.assign('brake', 0, { code: 'KeyX' });
    expect(keyHint('удерживайте {brake}', keys)).toBe('удерживайте X');
  });
});
