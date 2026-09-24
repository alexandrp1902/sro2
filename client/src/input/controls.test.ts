import { describe, expect, it } from 'vitest';
import { Controls } from './controls';

describe('Controls: замок «Стоп» (M20c)', () => {
  it('держит тягу нулём, пока кнопку не нажмут снова', () => {
    const controls = new Controls();
    controls.setThrottle(0.8);
    expect(controls.toggleStop()).toBe(true);
    expect(controls.throttle).toBe(0);
    controls.setThrottle(1); // стик отклонён — тяга всё равно не проходит
    expect(controls.throttle).toBe(0);
    expect(controls.toggleStop()).toBe(false);
    controls.setThrottle(1);
    expect(controls.throttle).toBe(1);
  });

  it('направление проходит и при замке: стиком крутятся на месте', () => {
    const controls = new Controls();
    controls.setStop(true);
    controls.setDirection(1, 0);
    expect(controls.input()).toEqual({ dx: 1, dy: 0, throttle: 0 });
  });

  it('явный газ с клавиатуры замок снимает — горящей кнопки рядом с W нет', () => {
    const controls = new Controls();
    controls.setStop(true);
    controls.thrust(1);
    expect(controls.stopLock).toBe(false);
    expect(controls.throttle).toBe(1);
  });

  it('удержание набранной скорости замок не снимает: это не команда игрока', () => {
    const controls = new Controls();
    controls.setStop(true);
    controls.setThrottle(0.5); // так клавиатура фиксирует круиз после отпускания газа
    expect(controls.stopLock).toBe(true);
    expect(controls.throttle).toBe(0);
  });

  it('прыжок, стыковка и гибель снимают замок: дальше игрок полетит, а не встанет', () => {
    const controls = new Controls();
    controls.setStop(true);
    controls.release();
    expect(controls.stopLock).toBe(false);
    expect(controls.throttle).toBe(0);
    controls.setThrottle(0.4);
    expect(controls.throttle).toBe(0.4);
  });
});
