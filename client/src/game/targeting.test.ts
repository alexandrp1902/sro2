import { describe, expect, it } from 'vitest';
import weapons from '../../../shared/weapons.json';
import type { WeaponParams } from '../sim/combat';
import { cycle, nearest, pickAt, type ScreenView, type TargetCandidate } from './targeting';

const pulse = weapons.pulse as WeaponParams;
/** Камера в начале координат, экран 400×800, масштаб 1: мировая точка (0, 0) — в центре экрана (200, 400). */
const view: ScreenView = { x: 0, y: 0, zoom: 1, width: 400, height: 800 };
const ship = (id: number, x: number, y: number, size = 16): TargetCandidate => ({ id, x, y, size });

describe('pickAt', () => {
  it('gives a finger a wider radius than the mouse', () => {
    const ships = [ship(1, 0, 0)];
    // 30 px от центра корабля размером 16: палец попадает (радиус 36), мышь — нет (22).
    expect(pickAt(230, 400, ships, view, true)).toBe(1);
    expect(pickAt(230, 400, ships, view, false)).toBeNull();
    expect(pickAt(215, 400, ships, view, false)).toBe(1);
  });

  it('takes the nearest ship and scales with zoom', () => {
    const ships = [ship(1, 0, 0), ship(2, 40, 0)];
    expect(pickAt(225, 400, ships, view, true)).toBe(2);
    expect(pickAt(200 + 40 * 0.5, 400, ships, { ...view, zoom: 0.5 }, false)).toBe(2);
    expect(pickAt(380, 100, ships, view, true)).toBeNull();
  });
});

describe('nearest', () => {
  const own = { x: 0, y: 0, rot: 0 }; // нос вверх

  it('prefers a ship in the weapon arc over a closer one behind', () => {
    expect(nearest(own, [ship(1, 0, 200), ship(2, 0, -400)], pulse)).toBe(2);
  });

  it('falls back to the nearest ship in range, and to nothing', () => {
    expect(nearest(own, [ship(1, 0, 500), ship(2, 0, 300)], pulse)).toBe(2);
    expect(nearest(own, [ship(1, 0, -900)], pulse)).toBeNull();
  });
});

describe('cycle', () => {
  it('goes from the nearest to the farthest and around', () => {
    const ships = [ship(3, 0, 900), ship(1, 100, 0), ship(2, 0, -300)];
    expect(cycle({ x: 0, y: 0 }, ships, 0)).toBe(1);
    expect(cycle({ x: 0, y: 0 }, ships, 1)).toBe(2);
    expect(cycle({ x: 0, y: 0 }, ships, 2)).toBe(3);
    expect(cycle({ x: 0, y: 0 }, ships, 3)).toBe(1);
    expect(cycle({ x: 0, y: 0 }, [], 3)).toBeNull();
  });
});
