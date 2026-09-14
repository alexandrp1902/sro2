import { describe, expect, it } from 'vitest';
import { CombatEvents } from './combatEvents';
import type { ShotDto, SnapshotMsg } from './protocol';

const OWN = 1;

function shot(from: number, to: number): ShotDto {
  return { from, to, w: 'pulse', hit: true, dmg: 100, sh: 100, ch: 50 };
}

function snapshot(tick: number, extra: Partial<SnapshotMsg>): SnapshotMsg {
  return { t: 'snapshot', tick, ships: [], ...extra };
}

describe('CombatEvents', () => {
  it('plays own shots and own destruction at once', () => {
    const events = new CombatEvents();
    const now = events.push(snapshot(10, { shots: [shot(OWN, 2), shot(3, OWN), shot(2, 3)], kills: [{ id: OWN, by: 3 }] }), OWN);
    expect(now.map((e) => e.kind)).toEqual(['shot', 'shot', 'kill']);
    expect(events.take(9.5)).toEqual([]);
    expect(events.take(10)).toEqual([{ kind: 'shot', tick: 10, shot: shot(2, 3) }]);
  });

  it('releases other ships’ events when the render clock reaches their tick', () => {
    const events = new CombatEvents();
    events.push(snapshot(10, { shots: [shot(2, 3)] }), OWN);
    events.push(snapshot(11, { kills: [{ id: 3, by: 2 }] }), OWN);
    expect(events.take(10.4).map((e) => e.tick)).toEqual([10]);
    expect(events.take(10.9)).toEqual([]);
    expect(events.take(11.1).map((e) => e.kind)).toEqual(['kill']);
  });

  it('drops events far behind the render clock and clears on reconnect', () => {
    const events = new CombatEvents();
    events.push(snapshot(10, { shots: [shot(2, 3)] }), OWN);
    expect(events.take(30)).toEqual([]);
    events.push(snapshot(40, { shots: [shot(2, 3)] }), OWN);
    events.clear();
    expect(events.take(41)).toEqual([]);
  });

  it('waits while the render clock has not started', () => {
    const events = new CombatEvents();
    events.push(snapshot(10, { shots: [shot(2, 3)] }), OWN);
    expect(events.take(Number.NaN)).toEqual([]);
    expect(events.take(10)).toHaveLength(1);
  });
});
