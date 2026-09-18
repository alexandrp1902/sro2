import { describe, expect, it } from 'vitest';
import weapons from '../../../shared/weapons.json';
import type { WeaponParams } from '../sim/combat';
import {
  LOOT_MOUSE_RADIUS_PX,
  cycle,
  nearest,
  nearestLoot,
  pickArrow,
  pickAt,
  type EdgeArrow,
  type ScreenView,
  type TargetCandidate,
} from './targeting';

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

  it('gives a small item a wider mouse radius, so it can be clicked at all', () => {
    // Обломок радиусом 11: без запаса мышь промахивается уже в 20 px от него.
    const drop = [ship(9, 0, 0, 11)];
    expect(pickAt(220, 400, drop, view, false)).toBeNull();
    expect(pickAt(220, 400, drop, view, false, LOOT_MOUSE_RADIUS_PX)).toBe(9);
    expect(pickAt(240, 400, drop, view, false, LOOT_MOUSE_RADIUS_PX)).toBeNull();
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

describe('nearestLoot', () => {
  const own = { x: 0, y: 0 };

  it('takes the nearest item inside the radius', () => {
    expect(nearestLoot(own, [ship(1, 0, 400), ship(2, 0, 200)], 1200)).toBe(2);
    expect(nearestLoot(own, [ship(1, 300, 400)], 500)).toBe(1); // ровно на границе
  });

  it('ignores everything outside the radius', () => {
    expect(nearestLoot(own, [ship(1, 0, 400)], 100)).toBeNull();
    expect(nearestLoot(own, [], 1200)).toBeNull();
  });
});

describe('cycle', () => {
  const own = { x: 0, y: 0 };
  // Ближнее кольцо по часовой стрелке от носа: вверх, вправо, вниз, влево. Пятый — во втором кольце.
  const up = ship(1, 0, -100);
  const right = ship(2, 200, 0);
  const down = ship(3, 0, 300);
  const left = ship(4, -400, 0);
  const far = ship(5, 0, -1000); // 1000 > 700 — следующий виток спирали
  const ships = [far, down, up, left, right];

  it('walks the near ring clockwise, then the next one', () => {
    expect(cycle(own, ships, 1)).toBe(2);
    expect(cycle(own, ships, 2)).toBe(3);
    expect(cycle(own, ships, 3)).toBe(4);
    expect(cycle(own, ships, 4)).toBe(5); // кольцо кончилось — виток дальше
  });

  it('is a carousel: past the last one comes the first again', () => {
    expect(cycle(own, ships, 5)).toBe(1);
    expect(cycle(own, ships, 1, -1)).toBe(5);
  });

  it('goes counter-clockwise with the other step', () => {
    expect(cycle(own, ships, 3, -1)).toBe(2);
    expect(cycle(own, ships, 2, -1)).toBe(1);
  });

  it('starts at the nearest object, not at the top of the spiral', () => {
    // Ничего не выделено: берём ближайший (up на 100), хотя по спирали первым мог оказаться другой.
    expect(cycle(own, ships, 0)).toBe(1);
    expect(cycle(own, [down, far], 0)).toBe(3);
    expect(cycle(own, [], 0)).toBeNull();
  });

  it('keeps everything in one ring when the ring is wide', () => {
    // Кольцо шире всех дистанций — остаётся чистый обход по часовой стрелке.
    expect(cycle(own, ships, 4, 1, 100_000)).toBe(5);
    expect(cycle(own, ships, 5, 1, 100_000)).toBe(1);
  });
});
