import { describe, expect, it } from 'vitest';
import { kindOf } from '../net/remoteShips';
import { lairs, type NpcRules } from './npcs';

const npcs: NpcRules = {
  stationSafeRadius: 900,
  patrolRadius: 250,
  types: { pirate: { name: 'Пират' }, heavyPirate: { name: 'Тяжёлый пират' } },
  spawns: [
    { type: 'pirate', level: 1, x: 0, y: -2100 },
    { type: 'pirate', level: 2, x: -2300, y: -1300, count: 2 },
    { type: 'heavyPirate', level: 3, x: 2300, y: -2500 },
    { type: 'pirate', level: 2, x: 2300, y: -2500 },
  ],
};

describe('lairs', () => {
  it('groups spawns at one point into a lair and names its members', () => {
    expect(lairs(npcs)).toEqual([
      { x: 0, y: -2100, radius: 250, label: 'Пират Ур.1' },
      { x: -2300, y: -1300, radius: 250, label: 'Пират Ур.2 ×2' },
      { x: 2300, y: -2500, radius: 250, label: 'Тяжёлый пират Ур.3 + Пират Ур.2' },
    ]);
  });

  it('is empty without spawns', () => {
    expect(lairs({ stationSafeRadius: 900, patrolRadius: 250 })).toEqual([]);
  });
});

describe('kindOf', () => {
  it('tells players, drones and pirates apart', () => {
    expect(kindOf(undefined)).toBe('player');
    expect(kindOf({ id: 1, name: 'A', online: true })).toBe('player');
    expect(kindOf({ id: 2, name: 'Дрон', online: true, npc: true })).toBe('drone');
    expect(kindOf({ id: 3, name: 'Пират Ур.1', online: true, npc: true, kind: 'pirate' })).toBe('pirate');
  });
});
