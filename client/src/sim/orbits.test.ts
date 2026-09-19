import { describe, expect, it } from 'vitest';
import { frameRotation, orbitAt, toWorld } from './orbits';

describe('orbits', () => {
  const orbit = { radius: 1000, periodMinutes: 60, phase: 90 };

  it('goes around once per period, like OrbitDef on the server', () => {
    expect(orbitAt(orbit, 0).x).toBeCloseTo(0, 6);
    expect(orbitAt(orbit, 0).y).toBeCloseTo(1000, 6);
    expect(orbitAt(orbit, 15 * 60).x).toBeCloseTo(-1000, 6);
    expect(orbitAt(orbit, 15 * 60).y).toBeCloseTo(0, 6);
  });

  it('keeps precision at unix time', () => {
    const now = 1_789_000_000;
    expect(orbitAt(orbit, now).x).toBeCloseTo(orbitAt(orbit, now + 3600 * 1000).x, 3);
  });

  it('puts +y of the station frame away from the sun', () => {
    for (const t of [0, 600, 1234, 3000]) {
      const spawn = toWorld(orbit, t, { x: 0, y: 420 });
      expect(Math.hypot(spawn.x, spawn.y)).toBeCloseTo(1420, 6);
    }
  });

  it('matches the rotation of a Pixi container holding the station frame', () => {
    const t = 777;
    const local = { x: 120, y: -40 };
    const r = frameRotation(orbit, t);
    const s = orbitAt(orbit, t);
    // Pixi: world = s + R(r) · local.
    const x = s.x + local.x * Math.cos(r) - local.y * Math.sin(r);
    const y = s.y + local.x * Math.sin(r) + local.y * Math.cos(r);
    const expected = toWorld(orbit, t, local);
    expect(x).toBeCloseTo(expected.x, 6);
    expect(y).toBeCloseTo(expected.y, 6);
  });

  it('leaves a centred station in world axes', () => {
    const centre = { radius: 0, periodMinutes: 60, phase: 0 };
    expect(toWorld(centre, 123, { x: 0, y: 420 })).toEqual({ x: 0, y: 420 });
    expect(frameRotation(centre, 123)).toBe(0);
  });
});
