import { describe, expect, it } from 'vitest';
import type { HullParams, ShipState } from '../sim/movement';
import { engineGlow } from './flame';

describe('engineGlow', () => {
  const hull = { maxSpeed: 100 } as HullParams;
  /** Корабль носом вверх, летящий вперёд с этой скоростью. */
  const flying = (speed: number): ShipState => ({ x: 0, y: 0, rot: 0, vx: 0, vy: -speed });

  it('пять ступеней: 0, 25, 50, 75, 100 — и ничего между ними', () => {
    const steps = [0, 0.25, 0.5, 0.75, 1];
    for (const step of steps) expect(engineGlow(flying(90), step, hull)).toBeCloseTo(step, 9);
    // Любая тяга подтягивается к ближайшей ступени.
    expect(engineGlow(flying(90), 0.34, hull)).toBeCloseTo(0.25, 9);
    expect(engineGlow(flying(90), 0.4, hull)).toBeCloseTo(0.5, 9);
    expect(engineGlow(flying(90), 0.93, hull)).toBeCloseTo(1, 9);
    for (const throttle of [0.1, 0.3, 0.45, 0.6, 0.8, 0.99]) {
      expect(steps).toContainEqual(engineGlow(flying(90), throttle, hull));
    }
  });

  it('нет тяги — нет огня', () => {
    expect(engineGlow(flying(90), 0, hull)).toBe(0);
  });

  it('висит на месте или пятится — не горит, сколько бы ни жал газ', () => {
    expect(engineGlow(flying(0), 1, hull)).toBe(0);
    expect(engineGlow(flying(5), 1, hull)).toBe(0);
    expect(engineGlow(flying(-40), 1, hull)).toBe(0);
  });

  it('тяга в развороте не считается полётом: смотрим вдоль носа', () => {
    // Скорость есть, но она поперёк корпуса — корабль сносит боком.
    expect(engineGlow({ x: 0, y: 0, rot: 0, vx: 90, vy: 0 }, 1, hull)).toBe(0);
  });
});
