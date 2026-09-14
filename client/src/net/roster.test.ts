import { describe, expect, it } from 'vitest';
import type { PlayerDto } from './protocol';
import { Roster } from './roster';

const OWN = 1;
const me: PlayerDto = { id: OWN, name: 'Я', online: true };
const bob: PlayerDto = { id: 2, name: 'Bob', online: true };

function started(...players: PlayerDto[]): Roster {
  const roster = new Roster();
  roster.update(players, OWN);
  return roster;
}

describe('Roster', () => {
  it('treats the first list after welcome as who is already here', () => {
    const roster = new Roster();
    expect(roster.update([me, bob], OWN)).toEqual([]);
    expect(roster.onlineCount).toBe(2);
  });

  it('reports joins, lost connections, returns and leaves of others', () => {
    const roster = started(me);
    expect(roster.update([me, bob], OWN)).toEqual([{ kind: 'joined', name: 'Bob' }]);
    expect(roster.update([me, { ...bob, online: false }], OWN)).toEqual([{ kind: 'lost', name: 'Bob' }]);
    expect(roster.onlineCount).toBe(1);
    expect(roster.update([me, bob], OWN)).toEqual([{ kind: 'back', name: 'Bob' }]);
    expect(roster.update([me], OWN)).toEqual([{ kind: 'left', name: 'Bob' }]);
  });

  it('reports renames', () => {
    const roster = started(me, bob);
    expect(roster.update([me, { ...bob, name: 'Robert' }], OWN)).toEqual([
      { kind: 'renamed', name: 'Robert', from: 'Bob' },
    ]);
  });

  it('says nothing about the own ship', () => {
    const roster = started(me, bob);
    expect(roster.update([{ ...me, name: 'Новое имя' }, bob], OWN)).toEqual([]);
  });

  it('keeps drones out of the feed and the online count', () => {
    const drone: PlayerDto = { id: 5, name: 'Учебный дрон', online: true, npc: true };
    const roster = started(me);
    expect(roster.update([me, drone], OWN)).toEqual([]);
    expect(roster.get(5)?.name).toBe('Учебный дрон');
    expect(roster.onlineCount).toBe(1);
    expect(roster.update([me], OWN)).toEqual([]);
  });

  it('starts over after a reconnect', () => {
    const roster = started(me);
    roster.reset();
    expect(roster.update([me, bob], OWN)).toEqual([]);
  });
});
