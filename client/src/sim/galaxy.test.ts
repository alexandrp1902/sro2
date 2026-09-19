import { describe, expect, it } from 'vitest';
import type { GalaxyDto } from '../net/protocol';
import { dangerColor, describeSystem, gateIndex, gateMarkId, hops, jumpCost, jumpOutlook, neighbours } from './galaxy';

const galaxy: GalaxyDto = {
  systems: [
    { id: 'sol', name: 'Sol', danger: 1, pvp: 'off', station: true, x: 20, y: 55 },
    { id: 'vega', name: 'Vega', danger: 2, pvp: 'border', station: true, x: 50, y: 25 },
    { id: 'tau', name: 'Tau', danger: 3, pvp: 'border', station: false, x: 50, y: 75 },
    { id: 'sigma', name: 'Sigma', danger: 5, pvp: 'free', station: false, x: 92, y: 82 },
  ],
  links: [
    { a: 'sol', b: 'vega', cost: 20 },
    { a: 'sol', b: 'tau', cost: 30 },
    { a: 'tau', b: 'sigma', cost: 25 },
  ],
};

describe('galaxy', () => {
  it('finds the jump cost both ways', () => {
    expect(jumpCost(galaxy, 'sol', 'tau')).toBe(30);
    expect(jumpCost(galaxy, 'tau', 'sol')).toBe(30);
    expect(jumpCost(galaxy, 'sol', 'sigma')).toBeNull();
    expect(neighbours(galaxy, 'sol')).toEqual([
      { id: 'vega', cost: 20 },
      { id: 'tau', cost: 30 },
    ]);
  });

  it('warns about one-way jumps into systems without a station', () => {
    expect(jumpOutlook(galaxy, 'sol', 'sol', 100)).toBe('here');
    expect(jumpOutlook(galaxy, 'sol', 'sigma', 100)).toBe('far');
    expect(jumpOutlook(galaxy, 'sol', 'tau', 29)).toBe('noFuel');
    expect(jumpOutlook(galaxy, 'sol', 'tau', 40)).toBe('oneWay'); // в Tau не заправиться, обратно — ещё 30
    expect(jumpOutlook(galaxy, 'sol', 'tau', 60)).toBe('ok');
    expect(jumpOutlook(galaxy, 'sol', 'vega', 20)).toBe('ok'); // в Vega есть станция
  });

  it('counts hops from the current system', () => {
    const map = hops(galaxy, 'sol');
    expect(map.get('sol')).toBe(0);
    expect(map.get('tau')).toBe(1);
    expect(map.get('sigma')).toBe(2);
  });

  it('describes a system for the feed', () => {
    expect(describeSystem(galaxy.systems[2])).toBe('Система Tau · средняя опасность · PvP вне станции · станции нет');
    expect(describeSystem(galaxy.systems[0])).toBe('Система Sol · безопасная · PvP нет');
  });

  it('keeps gate mark ids away from the station and the server ids', () => {
    expect(gateMarkId(0)).toBe(-2);
    expect(gateIndex(gateMarkId(3))).toBe(3);
    expect(dangerColor(9)).toBe(dangerColor(6)); // выше шестой опасности цвета нет (M11)
  });
});
