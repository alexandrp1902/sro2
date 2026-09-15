import { describe, expect, it } from 'vitest';
import { targetStep } from './fire';

const key = (code: string, shiftKey = false) => ({ code, shiftKey });

describe('targetStep', () => {
  it('steps back with Q, Shift+← and Shift+Tab', () => {
    expect(targetStep(key('KeyQ'))).toBe(-1);
    expect(targetStep(key('ArrowLeft', true))).toBe(-1);
    expect(targetStep(key('Tab', true))).toBe(-1);
  });

  it('steps forward with E, Shift+→ and Tab', () => {
    expect(targetStep(key('KeyE'))).toBe(1);
    expect(targetStep(key('ArrowRight', true))).toBe(1);
    expect(targetStep(key('Tab'))).toBe(1);
  });

  it('leaves plain arrows and other keys to flight', () => {
    expect(targetStep(key('ArrowLeft'))).toBe(0);
    expect(targetStep(key('ArrowRight'))).toBe(0);
    expect(targetStep(key('KeyA', true))).toBe(0);
    expect(targetStep(key('Space'))).toBe(0);
  });
});
