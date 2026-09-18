import { describe, expect, it } from 'vitest';
import { describeAlarm } from '../ui/meteorAlarm';
import { FALLBACK_SIZE, NO_METEORS, interceptRisk, meteorSize, positionAt, shapeFor, spinFor, type Body } from './meteors';

const rules = { warnSeconds: 4, warnMissFactor: 1.6 };
const ship = (extra: Partial<Body> = {}): Body => ({ x: 0, y: 0, vx: 0, vy: 0, size: 16, ...extra });
const rock = (extra: Partial<Body> = {}): Body => ({ x: 0, y: -500, vx: 0, vy: 250, size: 14, ...extra });

describe('positionAt', () => {
  it('moves along a straight line', () => {
    expect(positionAt({ x: 10, y: 20, vx: 100, vy: -50 }, 0.5)).toEqual({ x: 60, y: -5 });
  });

  it('does not run away when snapshots stop or the clock goes back', () => {
    expect(positionAt({ x: 0, y: 0, vx: 100, vy: 0 }, 5)).toEqual({ x: 100, y: 0 });
    expect(positionAt({ x: 0, y: 0, vx: 100, vy: 0 }, -1)).toEqual({ x: 0, y: 0 });
  });
});

describe('interceptRisk', () => {
  it('warns about a head-on course with the time to contact', () => {
    // 500 до центра, касание на 30: (500 − 30) / 250 = 1.88 с.
    const risk = interceptRisk(ship(), rock(), rules)!;
    expect(risk.seconds).toBeCloseTo(1.88, 9);
    expect(risk.miss).toBeCloseTo(0, 9);
  });

  it('counts the closing speed of both bodies', () => {
    const risk = interceptRisk(ship({ vy: -150 }), rock(), rules)!;
    expect(risk.seconds).toBeCloseTo(470 / 400, 9);
  });

  it('ignores a meteor that is too far in time', () => {
    expect(interceptRisk(ship(), rock({ y: -2000 }), rules)).toBeNull();
  });

  it('ignores a diverging meteor', () => {
    expect(interceptRisk(ship(), rock({ vy: -250 }), rules)).toBeNull();
  });

  it('ignores a parallel course and no relative motion', () => {
    expect(interceptRisk(ship({ vy: 250 }), rock(), rules)).toBeNull();
    expect(interceptRisk(ship(), rock({ vy: 0 }), rules)).toBeNull();
  });

  it('warns about a near miss inside the warning band, up to the closest point', () => {
    // Сумма радиусов 30, полоса предупреждения 48: промах 40 — тревога, 49 — нет.
    const near = interceptRisk(ship(), rock({ x: 40 }), rules)!;
    expect(near.miss).toBeCloseTo(40, 9);
    expect(near.seconds).toBeCloseTo(2, 9);
    expect(interceptRisk(ship(), rock({ x: 49 }), rules)).toBeNull();
  });

  it('reports zero when the bodies already touch', () => {
    expect(interceptRisk(ship(), rock({ y: -20 }), rules)!.seconds).toBe(0);
  });
});

describe('meteor look', () => {
  it('builds the same rough shape for the same id', () => {
    expect(shapeFor(7, 20)).toEqual(shapeFor(7, 20));
    expect(shapeFor(7, 20)).not.toEqual(shapeFor(8, 20));
    expect(shapeFor(7, 20)).toHaveLength(18);
  });

  it('keeps the vertices around the radius', () => {
    const points = shapeFor(123, 20);
    for (let i = 0; i < points.length; i += 2) {
      expect(Math.hypot(points[i], points[i + 1])).toBeGreaterThanOrEqual(20 * 0.78 - 1e-9);
      expect(Math.hypot(points[i], points[i + 1])).toBeLessThanOrEqual(20 * 1.08 + 1e-9);
    }
  });

  it('spins at a steady, id-bound rate', () => {
    expect(spinFor(5)).toBe(spinFor(5));
    expect(Math.abs(spinFor(5))).toBeGreaterThanOrEqual(0.4);
    expect(Math.abs(spinFor(5))).toBeLessThanOrEqual(1.5);
  });

  it('falls back to a medium rock for an unknown size', () => {
    expect(meteorSize(NO_METEORS, 'huge')).toBe(FALLBACK_SIZE);
  });
});

describe('describeAlarm', () => {
  it('is silent without threats', () => {
    expect(describeAlarm([])).toBeNull();
  });

  it('shows the soonest contact and escalates in the last second and a half', () => {
    expect(describeAlarm([{ id: 1, seconds: 2.44 }])).toEqual({ text: 'МЕТЕОРИТ · 2.4 с', critical: false });
    expect(describeAlarm([{ id: 1, seconds: 3 }, { id: 2, seconds: 1.2 }])).toEqual({
      text: 'МЕТЕОРИТЫ ×2 · 1.2 с',
      critical: true,
    });
  });
});
