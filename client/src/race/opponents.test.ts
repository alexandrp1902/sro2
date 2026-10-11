import { describe, expect, it } from 'vitest';
import rules from '../../../shared/race/rules.json';
import track from '../../../shared/race/tracks/red-canyon.json';
import { LocalRace } from './localRace';
import { RaceOpponents } from './opponents';
import type { Config } from './types';

const config: Config = { track, rules, hash: '', rankingKey: 'test' };
describe('computer opponents', () => {
  it('shares the countdown, uses distinct liveries and freezes when the player leaves', () => {
    const player = new LocalRace(config, 100, 3000), field = new RaceOpponents(player, 'blue');
    const positions = field.racers.map(r => ({ ...r.run.body.position }));
    expect(new Set(field.racers.map(r => r.livery)).size).toBe(3);
    expect(field.racers.some(r => r.livery === 'blue')).toBe(false);
    field.advance(3099);
    expect(field.racers.map(r => r.run.body.position)).toEqual(positions);
    field.advance(3300);
    expect(field.racers.every(r => r.run.state === 'RUNNING')).toBe(true);
    expect(field.racers[0].run.body.position).not.toEqual(positions[0]);
    player.abort();
    const stopped = field.racers.map(r => ({ ...r.run.body.position }));
    field.advance(3400);
    expect(field.racers.map(r => r.run.body.position)).toEqual(stopped);
  });
  it('completes the real canyon in order without crashing or skipping gates', () => {
    const player = new LocalRace(config, 0, 0), field = new RaceOpponents(player, 'copper');
    for (let tick = 1; tick <= 60 * 300 && field.racers.some(r => r.run.active); tick++) field.advance(tick * 1000 / 60);
    expect(field.racers.map(r => ({ name: r.name, state: r.run.state, laps: r.run.lapTimesMs.length, cp: r.run.nextCp, hp: r.run.body.hp })))
      .toEqual(field.racers.map(r => ({ name: r.name, state: 'FINISHED', laps: 3, cp: 0, hp: rules.hp })));
    expect(new Set(field.racers.map(r => r.run.elapsedMs)).size).toBe(3);
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
