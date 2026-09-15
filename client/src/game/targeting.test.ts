import { describe, expect, it } from 'vitest';
import weapons from '../../../shared/weapons.json';
import type { WeaponParams } from '../sim/combat';
import { cycle, nearest, pickArrow, pickAt, type EdgeArrow, type ScreenView, type TargetCandidate } from './targeting';

/** Сектор ±60° — чтобы проверять выбор «сначала в секторе» независимо от баланса в weapons.json. */
const pulse = { ...(weapons.pulse as WeaponParams), arc: 60 };
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

describe('pickArrow', () => {
  // Стрелка у правого края, подпись левее неё.
  const arrow = (id: number, x: number, y: number): EdgeArrow => ({ id, x, y, label: { x: x - 100, y: y - 8, width: 70, height: 16 } });

  it('picks a ship by its arrow or its label', () => {
    const arrows = [arrow(1, 374, 300)];
    expect(pickArrow(374, 300, arrows, false)).toBe(1);
    expect(pickArrow(300, 300, arrows, false)).toBe(1); // по подписи: она занимает x 274…344
    expect(pickArrow(374, 330, arrows, false)).toBeNull(); // мышью мимо
    expect(pickArrow(374, 330, arrows, true)).toBe(1); // пальцем — радиус шире
  });

  it('takes the nearest of two arrows and nothing far away', () => {
    const arrows = [arrow(1, 374, 300), arrow(2, 374, 330)];
    expect(pickArrow(374, 322, arrows, true)).toBe(2);
    expect(pickArrow(100, 600, arrows, true)).toBeNull();
    expect(pickArrow(0, 0, [], true)).toBeNull();
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

  it('takes simply the nearest ship when the gun fires all around', () => {
    expect(nearest(own, [ship(1, 0, 200), ship(2, 0, -400)], { ...pulse, arc: 180 })).toBe(1);
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

  it('goes back from the farthest to the nearest and around; starts at the nearest', () => {
    const ships = [ship(3, 0, 900), ship(1, 100, 0), ship(2, 0, -300)];
    expect(cycle({ x: 0, y: 0 }, ships, 3, -1)).toBe(2);
    expect(cycle({ x: 0, y: 0 }, ships, 2, -1)).toBe(1);
    expect(cycle({ x: 0, y: 0 }, ships, 1, -1)).toBe(3);
    expect(cycle({ x: 0, y: 0 }, ships, 0, -1)).toBe(1);
  });
});
