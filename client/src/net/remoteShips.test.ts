import { describe, expect, it } from 'vitest';
import { kindOf } from '../net/remoteShips';
describe('kindOf', () => {
  it('tells players, drones and pirates apart', () => {
    expect(kindOf(undefined)).toBe('player');
    expect(kindOf({ id: 1, name: 'A', online: true })).toBe('player');
    expect(kindOf({ id: 2, name: 'Дрон', online: true, npc: true })).toBe('drone');
    expect(kindOf({ id: 3, name: 'Пират Ур.1', online: true, npc: true, kind: 'pirate' })).toBe('pirate');
  });
});
