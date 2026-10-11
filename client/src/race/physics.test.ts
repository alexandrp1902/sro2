import { describe, expect, it } from 'vitest';
import vectors from '../../../shared/test-vectors/race.json';
import { body, crossing, damage, step } from './physics';
import type { Input, Rules, Track } from './types';

describe('shared C#/TypeScript race vectors', () => {
  for (const v of vectors.cases) it(v.name, () => {
    const track = vectors.track as Track, rules = vectors.rules as Rules;
    const b = { ...body(track, rules), ...v.initial };
    let fatal: string | null = null;
    for (const phase of v.phases) {
      for (let i = 0; i < phase.ticks; i++) { fatal = step(b, phase.input as Input, track, rules).fatalReason; if (fatal) break; }
      if (fatal) break;
    }
    expect(fatal).toBe(v.fatalReason);
    for (const key of ['heading', 'omega', 'hp'] as const) expect(b[key]).toBeCloseTo(v.expected[key], 9);
    for (const key of ['position', 'velocity'] as const) {
      expect(b[key].x).toBeCloseTo(v.expected[key].x, 9); expect(b[key].y).toBeCloseTo(v.expected[key].y, 9);
    }
  });
  it('only accepts forward swept gate crossings', () => {
    const g = { a: { x: 0, y: -2 }, b: { x: 0, y: 2 }, normal: { x: 1, y: 0 } };
    expect(crossing({ x: -3, y: 0 }, { x: 1, y: 0 }, g)).toBe(.75);
    expect(crossing({ x: 1, y: 0 }, { x: -3, y: 0 }, g)).toBeNull();
    expect(crossing({ x: 0, y: 0 }, { x: 0, y: 0 }, g)).toBeNull();
  });
  it('damage is continuous at nonfatal class boundaries', () => { expect(damage(1.99)).toBe(0); expect(damage(2)).toBe(2); expect(damage(7)).toBe(12); });
  it.each([1, -1])('reports sparks on the contacted side for a gentle wall hit (side %s)', side => {
    const rules = { ...vectors.rules, radius: .9, dragK: 0, lateralK: 0, coastBrakeAccel: 0 } as Rules;
    const track = { ...vectors.track, walls: [{ id: 'visual-wall', reason: 'WALL',
      a: { x: -20, y: side }, b: { x: 20, y: side } }], obstacles: [] } as Track;
    const b = body(track, rules); b.position = { x: 0, y: side * .095 }; b.velocity = { x: 0, y: side };
    const motion = step(b, { seq: 0, steerMode: 'turn', heading: 0, turn: 0, throttle: 0, brake: false, controlActive: true }, track, rules);
    expect(motion.fatalReason).toBeNull(); expect(b.hp).toBe(rules.hp);
    expect(motion.impacts).toHaveLength(1);
    expect(motion.impacts[0].position.y).toBeCloseTo(side);
    expect(motion.impacts[0].normal.y).toBe(-side);
    expect(motion.impacts[0].velocity.y).toBeCloseTo(side);
  });
});
