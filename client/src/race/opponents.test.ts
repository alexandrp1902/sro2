import { describe, expect, it } from 'vitest';
import rules from '../../../shared/race/rules.json';
import track from '../../../shared/race/tracks/red-canyon.json';
import { LocalRace } from './localRace';
import { RaceOpponents } from './opponents';
import type { Config, Input } from './types';

const config: Config = { track, rules, hash: '', rankingKey: 'test' };
const idle: Input = { seq: 0, steerMode: 'turn', turn: 0, heading: 0, throttle: 0, brake: false, controlActive: true };
describe('computer opponents', () => {
  it('shares the countdown, uses distinct liveries and freezes when the player leaves', () => {
    const player = new LocalRace(config, 100, 3000), field = new RaceOpponents(player, 'blue');
    const positions = field.racers.map(r => ({ ...r.run.body.position }));
    expect(new Set(field.racers.map(r => r.livery)).size).toBe(3);
    expect(field.racers.some(r => r.livery === 'blue')).toBe(false);
    field.advance(3099, idle);
    expect(field.racers.map(r => r.run.body.position)).toEqual(positions);
    field.advance(3300, idle);
    expect(field.racers.every(r => r.run.state === 'RUNNING')).toBe(true);
    expect(field.racers[0].run.body.position).not.toEqual(positions[0]);
    player.abort();
    const stopped = field.racers.map(r => ({ ...r.run.body.position }));
    field.advance(3400, idle);
    expect(field.racers.map(r => r.run.body.position)).toEqual(stopped);
  });
  it('completes the real canyon in order without crashing or skipping gates', () => {
    const player = new LocalRace(config, 0, 0), field = new RaceOpponents(player, 'copper');
    player.body.position = { x: 1000, y: 1000 }; // Spectator: no obstruction from the player.
    let minimumSeparation = Infinity;
    const peaks = [0, 0, 0], braking = [false, false, false];
    for (let tick = 1; tick <= 60 * 300 && field.racers.some(r => r.run.active); tick++) {
      const before = field.racers.map(r => Math.hypot(r.run.body.velocity.x, r.run.body.velocity.y));
      field.advance(tick * 1000 / 60, idle);
      for (let i = 0; i < 3; i++) for (let j = i + 1; j < 3; j++) {
        const a = field.racers[i].run, b = field.racers[j].run;
        if (a.active && b.active) minimumSeparation = Math.min(minimumSeparation,
          Math.hypot(a.body.position.x - b.body.position.x, a.body.position.y - b.body.position.y));
      }
      field.racers.forEach((r, i) => {
        const speed = Math.hypot(r.run.body.velocity.x, r.run.body.velocity.y);
        peaks[i] = Math.max(peaks[i], speed);
        if (r.run.active && before[i] - speed > .3) braking[i] = true;
      });
    }
    expect(field.racers.map(r => ({ name: r.name, state: r.run.state, laps: r.run.lapTimesMs.length, cp: r.run.nextCp, hp: r.run.body.hp })))
      .toEqual(field.racers.map(r => ({ name: r.name, state: 'FINISHED', laps: 3, cp: 0, hp: rules.hp })));
    expect(new Set(field.racers.map(r => r.run.elapsedMs)).size).toBe(3);
    expect(peaks.every(speed => speed > rules.engineLimit * .95)).toBe(true);
    expect(braking).toEqual([true, true, true]);
    expect(minimumSeparation).toBeGreaterThan(rules.radius * 2 + .2);
    for (const racer of field.racers) {
      expect(racer.run.impacts).toHaveLength(0);
      racer.run.lapTimesMs.forEach((time, lap) => expect(Math.abs(time - (lap === 0 ? 26000 : 24000))).toBeLessThan(1000));
    }
  });
  it('all pilots reach the vehicle speed limit on a long straight', () => {
    const straight: Config = { ...config, track: { ...track, walls: [], obstacles: [],
      centerline: Array.from({ length: 800 }, (_, i) => ({ x: i * 2.5, y: 0 })) } };
    const field = new RaceOpponents(new LocalRace(straight, 0, 0), 'copper');
    field.player.body.position = { x: 1000, y: 1000 };
    for (let tick = 1; tick <= 60 * 10; tick++) field.advance(tick * 1000 / 60, idle);
    for (const racer of field.racers) {
      expect(Math.hypot(racer.run.body.velocity.x, racer.run.body.velocity.y)).toBeCloseTo(rules.engineLimit, 1);
    }
  });
  it.each([30, 144])('keeps lap targets and clear lanes at %i render frames per second', fps => {
    const player = new LocalRace(config, 0, 0), field = new RaceOpponents(player, 'white');
    player.body.position = { x: 1000, y: 1000 };
    for (let frame = 1; frame <= fps * 90 && field.racers.some(r => r.run.active); frame++) field.advance(frame * 1000 / fps, idle);
    for (const racer of field.racers) {
      expect(racer.run.state).toBe('FINISHED'); expect(racer.run.body.hp).toBe(100);
      racer.run.lapTimesMs.forEach((time, lap) => expect(Math.abs(time - (lap === 0 ? 26000 : 24000))).toBeLessThan(1000));
    }
  });
  it('orders completed laps before checkpoints, finishers by time and retired racers last', () => {
    const player = new LocalRace(config, 0, 0), field = new RaceOpponents(player, 'copper');
    player.nextCp = 8; field.racers[0].run.lapTimesMs.push(40000);
    field.racers[1].run.nextCp = 3;
    expect(field.place).toBe(2);
    player.state = 'FINISHED'; player.elapsedMs = 100000;
    field.racers[0].run.state = 'FINISHED'; field.racers[0].run.elapsedMs = 110000;
    expect(field.place).toBe(1);
    player.state = 'CRASHED';
    expect(field.place).toBe(4);
  });
});
