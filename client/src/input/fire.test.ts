import { describe, expect, it } from 'vitest';
import { targetStep } from './fire';
import { Keymap } from './keymap';

const key = (code: string, shiftKey = false) => ({ code, shiftKey });

function memoryStore() {
  const data = new Map<string, string>();
  return { get: (k: string) => data.get(k) ?? null, set: (k: string, v: string) => void data.set(k, v), remove: (k: string) => void data.delete(k) };
}

describe('targetStep', () => {
  it('steps back with Q and Shift+Tab', () => {
    expect(targetStep(key('KeyQ'))).toBe(-1);
    expect(targetStep(key('Tab', true))).toBe(-1);
  });

  it('steps forward with E and Tab', () => {
    expect(targetStep(key('KeyE'))).toBe(1);
    expect(targetStep(key('Tab'))).toBe(1);
  });

  it('leaves arrows and other keys to flight', () => {
    expect(targetStep(key('ArrowLeft'))).toBe(0);
    expect(targetStep(key('ArrowRight', true))).toBe(0);
    expect(targetStep(key('KeyA', true))).toBe(0);
    expect(targetStep(key('Space'))).toBe(0);
  });

  it('follows a rebound layout: Shift+→ selects the next target, E no longer does', () => {
    const keys = new Keymap(memoryStore());
    keys.assign('targetNext', 0, { code: 'ArrowRight', shift: true });
    expect(targetStep(key('ArrowRight', true), keys)).toBe(1);
    expect(targetStep(key('ArrowRight'), keys)).toBe(0); // без Shift — поворот
    expect(targetStep(key('KeyE'), keys)).toBe(0);
  });
});
