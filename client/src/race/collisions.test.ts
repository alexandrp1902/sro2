import { describe, expect, it } from 'vitest';
import rules from '../../../shared/race/rules.json';
import track from '../../../shared/race/tracks/red-canyon.json';
import { LocalRace } from './localRace';
import { collideRacers } from './collisions';
import { RaceOpponents } from './opponents';
import type { Config, Input } from './types';

const config: Config = { track: { ...track, walls: [], obstacles: [] }, rules, hash: '', rankingKey: 'test' };
const idle: Input = { seq: 0, steerMode: 'turn', turn: 0, heading: 0, throttle: 0, brake: false, controlActive: true };
const racer = (x: number, vx: number, y = 0) => {
  const run = new LocalRace(config, 0, 0); run.state = 'RUNNING';
  run.body.position = { x, y }; run.body.velocity = { x: vx, y: 0 }; return run;
};
describe('pod collisions', () => {
  it('separates pods, transfers momentum and damages both on a moderate hit', () => {
    const a = racer(1, 2), b = racer(3, -2);
    collideRacers([a, b], [{ x: 0, y: 0 }, { x: 4, y: 0 }], new Set(), 17);
    expect(a.body.velocity.x).toBeCloseTo(-.1); expect(b.body.velocity.x).toBeCloseTo(.1);
    expect(a.body.hp).toBe(98); expect(b.body.hp).toBe(98);
    expect(b.body.position.x - a.body.position.x).toBeGreaterThanOrEqual(rules.radius * 2);
    expect(a.impacts).toHaveLength(1); expect(b.impacts).toHaveLength(1);
  });
  it('detects a swept head-on hit even when the pods exchange sides in one tick', () => {
    const a = racer(10, 600), b = racer(0, -600);
    collideRacers([a, b], [{ x: 0, y: 0 }, { x: 10, y: 0 }], new Set(), 17);
    expect(a.state).toBe('CRASHED'); expect(b.state).toBe('CRASHED');
    expect(a.reason).toBe('BOLIDE'); expect(b.reason).toBe('BOLIDE');
    expect(a.body.hp).toBe(0); expect(a.crashVelocity?.x).toBe(600);
    expect(a.elapsedMs).toBeLessThan(17);
  });
  it('does not damage parallel pods, or damage the same sustained contact twice', () => {
    const a = racer(1, 5), b = racer(1, 5, 4);
    collideRacers([a, b], [{ x: 0, y: 0 }, { x: 0, y: 4 }], new Set(), 17);
    expect(a.body.hp).toBe(100); expect(b.body.hp).toBe(100);
    b.body.position = { x: 3, y: 0 }; b.body.velocity.x = -5;
    collideRacers([a, b], [{ x: 0, y: 0 }, { x: 4, y: 0 }], new Set(['0:1']), 34);
    expect(a.body.hp).toBe(100); expect(b.body.hp).toBe(100);
  });
  it('includes player-to-opponent contact in the shared race tick', () => {
    const player = new LocalRace(config, 0, 0), field = new RaceOpponents(player, 'copper');
    player.body.position = { x: 50, y: 0 }; player.body.velocity = { x: 30, y: 0 };
    field.racers[0].run.body.position = { x: 53.8, y: 0 };
    field.racers[1].run.body.position = { x: 1000, y: 1000 };
    field.racers[2].run.body.position = { x: 2000, y: 2000 };
    field.advance(17, idle);
    expect(player.body.hp).toBeLessThan(100);
    expect(field.racers[0].run.body.hp).toBeLessThan(100);
    expect(player.impacts.length).toBeGreaterThan(0);
  });
});
