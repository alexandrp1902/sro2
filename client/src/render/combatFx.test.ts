import { describe, expect, it } from 'vitest';
import { isPseudoWeapon, RAM_WEAPON, shotStyle, SPLASH_WEAPON } from './combatFx';

describe('shotStyle', () => {
  it('таран метеорита рисуется без снаряда', () => {
    expect(shotStyle(RAM_WEAPON, false)).toBe('ram');
  });

  it('осколки взрыва рисуются без трассера', () => {
    // M15.5: снаряда не было, рвануло на самом соседе — тянуть к нему линию неоткуда.
    expect(shotStyle(SPLASH_WEAPON, false)).toBe('splash');
  });

  it('ракета долетает сама, поэтому трассера у неё тоже нет', () => {
    expect(shotStyle('missiles', true)).toBe('missile');
  });

  it('обычная пушка рисует трассер', () => {
    expect(shotStyle('plasma', false)).toBe('tracer');
  });
});

describe('isPseudoWeapon', () => {
  it('у тарана и осколков пушки в каталоге нет', () => {
    expect(isPseudoWeapon(RAM_WEAPON)).toBe(true);
    expect(isPseudoWeapon(SPLASH_WEAPON)).toBe(true);
  });

  it('настоящие пушки в каталоге есть', () => {
    expect(isPseudoWeapon('plasma')).toBe(false);
    expect(isPseudoWeapon('torpedoes')).toBe(false);
  });
});
