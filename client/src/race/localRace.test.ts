import { describe, expect, it } from 'vitest';
import rules from '../../../shared/race/rules.json';
import canyon from '../../../shared/race/tracks/red-canyon.json';
import { LocalRace } from './localRace';
import type { Config, Input } from './types';

const input: Input = { seq: 1, steerMode: 'turn', heading: 0, turn: 0, throttle: 0, brake: false, controlActive: true };
const gate = (x: number) => ({ a: { x, y: -10 }, b: { x, y: 10 }, normal: { x: 1, y: 0 } });
function straight(laps = 1, finish = .3, wall = false): Config {
  return { hash: '', rankingKey: 'local-test', rules: { ...rules, radius: .9, dragK: 0, lateralK: 0, coastBrakeAccel: 0 },
    track: { ...canyon, spawn: { x: 0, y: 0 }, spawnHeading: 0, lapCount: laps, length: 1000,
      centerline: [], obstacles: [], walls: wall ? [{ id: 'wall', a: { x: 1.2, y: -100 }, b: { x: 1.2, y: 100 }, reason: 'WALL' }] : [],
      checkpoints: [gate(.1), gate(.2)], finishLine: gate(finish) } };
}
describe('browser-owned race', () => {
  it('locks countdown and measures time from the local monotonic clock', () => {
    const run = new LocalRace(straight(), 100, 3000);
    run.advance(3099, { ...input, throttle: 1 }); expect(run.body.position.x).toBe(0); expect(run.elapsedMs).toBe(0);
    run.advance(3200, input); expect(run.state).toBe('RUNNING'); expect(run.elapsedMs).toBe(100);
    run.advance(13200, input); expect(run.elapsedMs).toBe(10100); // Not clamped to a single render frame.
  });
  it('interpolates ordered gates, finishes three laps and sums lap times', () => {
    const run = new LocalRace(straight(3), 0, 0);
    for (let i = 1; i <= 3; i++) { run.body.position = { x: 0, y: 0 }; run.body.velocity = { x: 30, y: 0 }; run.advance(i * 17, input); }
    expect(run.state).toBe('FINISHED'); expect(run.lapTimesMs).toHaveLength(3);
    expect(run.lapTimesMs.reduce((a, b) => a + b, 0)).toBe(run.elapsedMs);
  });
  it.each([[.25, 'FINISHED'], [.3, 'CRASHED'], [.4, 'CRASHED']])('orders finish at %s against a fatal impact', (finish, state) => {
    const run = new LocalRace(straight(1, Number(finish), true), 0, 0); run.body.velocity = { x: 30, y: 0 };
    run.advance(17, input); expect(run.state).toBe(state);
    if (state === 'FINISHED') expect(run.body.hp).toBe(100);
  });
  it('ignores reverse crossings and a finish without preceding checkpoints', () => {
    const run = new LocalRace(straight(), 0, 0); run.body.position = { x: .25, y: 0 }; run.body.velocity = { x: 30, y: 0 };
    run.advance(17, input); expect(run.lapTimesMs).toHaveLength(0);
    run.body.velocity = { x: -30, y: 0 }; run.advance(34, input); expect(run.nextCp).toBe(0);
  });
  it('retains the velocity at destruction for visual debris after stopping physics', () => {
    const run = new LocalRace(straight(1, .4, true), 0, 0); run.body.velocity = { x: 30, y: 0 };
    run.advance(17, input);
    expect(run.state).toBe('CRASHED'); expect(run.body.velocity.x).toBe(0);
    expect(run.crashVelocity!.x).toBeGreaterThan(20);
    expect(run.impacts[0].position.x).toBeCloseTo(1.2);
    expect(run.impacts[0].normal.x).toBe(-1);
  });
  it('completes the actual canyon locally with standard controls', () => {
    const config: Config = { track: canyon, rules, hash: '', rankingKey: 'canyon-local' };
    const run = new LocalRace(config, 0, 0), line = canyon.centerline;
    for (let tick = 1; tick < 36000 && run.active; tick++) {
      let nearest = Infinity, index = 0, projection = run.body.position;
      for (let i = 0; i < line.length; i++) {
        const a = line[i], b = line[(i + 1) % line.length], dx = b.x - a.x, dy = b.y - a.y;
        const t = Math.max(0, Math.min(1, ((run.body.position.x - a.x) * dx + (run.body.position.y - a.y) * dy) / (dx * dx + dy * dy)));
        const p = { x: a.x + dx * t, y: a.y + dy * t }, d = Math.hypot(p.x - run.body.position.x, p.y - run.body.position.y);
        if (d < nearest) { nearest = d; index = i; projection = p; }
      }
      let remaining = 4, target = projection;
      for (let i = 0; i < line.length && remaining > 0; i++) {
        const next = line[(index + 1) % line.length], dx = next.x - target.x, dy = next.y - target.y, d = Math.hypot(dx, dy);
        if (d > remaining) { target = { x: target.x + dx * remaining / d, y: target.y + dy * remaining / d }; break; }
        remaining -= d; target = next; index = (index + 1) % line.length;
      }
      run.advance(tick * 1000 / 60, { ...input, steerMode: 'heading', heading: Math.atan2(target.y - run.body.position.y, target.x - run.body.position.x), throttle: 6 / rules.engineLimit });
    }
    expect(run.state, `${run.reason} CP ${run.nextCp}`).toBe('FINISHED'); expect(run.lapTimesMs).toHaveLength(3);
  });
});
