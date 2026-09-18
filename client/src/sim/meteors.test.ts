import { describe, expect, it } from 'vitest';
import vectors from '../../../shared/test-vectors/meteors.json';
import {
  FALLBACK_SIZE,
  NO_METEORS,
  advance,
  meteorSize,
  pull,
  rockPoints,
  rotatePoints,
  silhouette,
  spinFor,
  step,
  type MeteorRules,
  type MeteorState,
} from './meteors';

/** Одни и те же операции в .NET и V8; допуск — на последний бит. */
const TOLERANCE = 1e-9;

const rules: MeteorRules = {
  maxAlive: 10,
  gravity: vectors.gravity,
  gravityMinRadius: vectors.gravityMinRadius,
};

// Эталоны генерирует C# (server/Sro.Sim.Tests/MeteorVectorTests.cs): дуга на экране обязана совпадать с серверной.
describe('meteor flight matches the server (shared/test-vectors/meteors.json)', () => {
  it('runs the same arc tick by tick', () => {
    for (const c of vectors.flight) {
      let s: MeteorState = { x: c.x0, y: c.y0, vx: c.vx0, vy: c.vy0 };
      for (let i = 0; i < c.ticks; i++) s = step(rules, s, 0.05);
      for (const [got, want] of [
        [s.x, c.x],
        [s.y, c.y],
        [s.vx, c.vx],
        [s.vy, c.vy],
      ]) {
        if (Math.abs(got - want) > TOLERANCE) throw new Error(`${JSON.stringify(c)}: got ${s.x}, ${s.y}, ${s.vx}, ${s.vy}`);
      }
    }
  });
});

describe('gravity', () => {
  it('pulls towards the centre of the system', () => {
    const { ax, ay } = pull(rules, 1000, 0);
    expect(ax).toBeLessThan(0);
    expect(Math.abs(ay)).toBe(0);
  });

  it('stops growing below the softening radius', () => {
    const edge = pull(rules, rules.gravityMinRadius, 0).ax;
    expect(pull(rules, 10, 0).ax).toBeGreaterThan(edge); // слабее, а не бесконечность
    expect(pull(rules, 0, 0)).toEqual({ ax: 0, ay: 0 });
  });

  it('is off when the server sends none', () => {
    expect(pull(NO_METEORS, 1000, 0)).toEqual({ ax: 0, ay: 0 });
  });
});

describe('advance', () => {
  const from: MeteorState = { x: 1500, y: -3000, vx: 0, vy: 250 };

  it('matches whole ticks of stepping', () => {
    let stepped = from;
    for (let i = 0; i < 10; i++) stepped = step(rules, stepped, 0.05);
    const advanced = advance(rules, from, 0.5);
    expect(advanced.x).toBeCloseTo(stepped.x, 9);
    expect(advanced.y).toBeCloseTo(stepped.y, 9);
  });

  it('adds the part of a tick that is left', () => {
    const half = advance(rules, from, 0.025);
    expect(half.y).toBeGreaterThan(from.y);
    expect(half.y).toBeLessThan(advance(rules, from, 0.05).y);
  });

  it('does not run away when snapshots stop or the clock goes back', () => {
    expect(advance(rules, from, 30).y).toEqual(advance(rules, from, 1).y);
    expect(advance(rules, from, -1)).toEqual(from);
  });

  it('bends the track towards the centre', () => {
    // Прямо бы летел с x = 1500; тяготение сносит камень внутрь.
    expect(advance(rules, from, 1).x).toBeLessThan(1500);
  });
});

describe('rock shape', () => {
  it('builds the same lumpy cloud for the same id', () => {
    expect(rockPoints(7, 20)).toEqual(rockPoints(7, 20));
    expect(rockPoints(7, 20)).not.toEqual(rockPoints(8, 20));
    expect(rockPoints(7, 20)).toHaveLength(26);
  });

  it('keeps the points around the radius', () => {
    for (const p of rockPoints(123, 20)) {
      const r = Math.hypot(p.x, p.y, p.z);
      expect(r).toBeGreaterThanOrEqual(20 * 0.74 - 1e-9);
      expect(r).toBeLessThanOrEqual(20 * 1.08 + 1e-9);
    }
  });

  it('tumbles around three axes at an id-bound rate', () => {
    const spin = spinFor(5);
    expect(spin).toEqual(spinFor(5));
    for (const rate of [spin.x, spin.y, spin.z]) {
      expect(Math.abs(rate)).toBeGreaterThanOrEqual(0.15);
      expect(Math.abs(rate)).toBeLessThanOrEqual(0.8);
    }
  });

  it('rotation keeps every point at its distance from the centre', () => {
    const before = rockPoints(42, 30);
    const after = rotatePoints(before, 0.7, -1.2, 2.5);
    for (let i = 0; i < before.length; i++) {
      const a = before[i];
      const b = after[i];
      expect(Math.hypot(b.x, b.y, b.z)).toBeCloseTo(Math.hypot(a.x, a.y, a.z), 9);
    }
  });

  it('turns the cloud into a silhouette that holds every point', () => {
    const points = rockPoints(9, 25);
    const hull = silhouette(points);
    expect(hull.length).toBeGreaterThanOrEqual(6);
    expect(hull.length % 2).toBe(0);

    // Оболочка выпуклая и накрывает все точки: обход против часовой стрелки не меняет знак векторного произведения.
    for (let i = 0; i < hull.length; i += 2) {
      const [ax, ay] = [hull[i], hull[i + 1]];
      const [bx, by] = [hull[(i + 2) % hull.length], hull[(i + 3) % hull.length]];
      for (const p of points) {
        expect((bx - ax) * (p.y - ay) - (by - ay) * (p.x - ax)).toBeGreaterThan(-1e-9);
      }
    }
  });

  it('falls back to a medium rock for an unknown size', () => {
    expect(meteorSize(NO_METEORS, 'huge')).toBe(FALLBACK_SIZE);
  });
});
