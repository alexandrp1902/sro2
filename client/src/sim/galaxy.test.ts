import { describe, expect, it } from 'vitest';
import type { GalaxyDto } from '../net/protocol';
import {
  dangerColor,
  describeSystem,
  gateIndex,
  gateIndexTo,
  gateLabel,
  gateLetters,
  gateMarkId,
  gateName,
  hops,
  jumpOutlook,
  linkGateNumbers,
  linked,
  neighbours,
} from './galaxy';

const galaxy: GalaxyDto = {
  systems: [
    { id: 'sol', name: 'Sol', danger: 1, pvp: 'off', station: true, x: 20, y: 55, gates: ['vega', 'tau'] },
    { id: 'vega', name: 'Vega', danger: 2, pvp: 'border', station: true, x: 50, y: 25, gates: ['sol'] },
    { id: 'tau', name: 'Tau', danger: 3, pvp: 'border', station: false, x: 50, y: 75, gates: ['sigma', 'sol'] },
    { id: 'sigma', name: 'Sigma', danger: 5, pvp: 'free', station: false, x: 92, y: 82, gates: ['tau'] },
  ],
  links: [
    { a: 'sol', b: 'vega' },
    { a: 'sol', b: 'tau' },
    { a: 'tau', b: 'sigma' },
  ],
};

describe('galaxy', () => {
  it('finds the route both ways', () => {
    expect(linked(galaxy, 'sol', 'tau')).toBe(true);
    expect(linked(galaxy, 'tau', 'sol')).toBe(true);
    expect(linked(galaxy, 'sol', 'sigma')).toBe(false);
    expect(neighbours(galaxy, 'sol')).toEqual(['vega', 'tau']);
  });

  // Топливо отменено (M15.6): прыжку мешает только отсутствие прямого маршрута.
  it('tells here from a neighbour from an unreachable system', () => {
    expect(jumpOutlook(galaxy, 'sol', 'sol')).toBe('here');
    expect(jumpOutlook(galaxy, 'sol', 'sigma')).toBe('far');
    expect(jumpOutlook(galaxy, 'sol', 'tau')).toBe('ok');
    expect(jumpOutlook(galaxy, 'sol', 'vega')).toBe('ok');
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

  it('numbers gates by their order in the system', () => {
    expect(gateName(0)).toBe('Врата 1');
    expect(gateLabel({ to: 'vega', name: 'Vega', x: 0, y: 0 }, 2)).toBe('Врата 3 · → Vega');
    expect(gateIndexTo(['vega', 'tau'], 'tau')).toBe(1);
    expect(gateIndexTo(['vega', 'tau'], 'sigma')).toBe(-1);
  });

  it('подписывает врата на миникарте буквой системы за ними', () => {
    const at = (...names: string[]) => gateLetters(names.map((name) => ({ name })));
    expect(at('Vega', 'Альфа Центавра', 'Tau')).toEqual(['V', 'А', 'T']);
    // Одна буква на двоих — удлиняем обе подписи, пока не различатся.
    expect(at('Sol', 'Sigma', 'Nova')).toEqual(['So', 'Si', 'No']);
    expect(at('Альфа Центавра', 'Альдебаран')).toEqual(['Альф', 'Альд']);
    // Пустое имя не роняет подпись, а один выход обходится одной буквой.
    expect(at('  ')).toEqual(['?']);
    expect(at('Край')).toEqual(['К']);
  });

  it('numbers both ends of a link on their own', () => {
    // Из Sol в Tau — вторые врата, а обратно из Tau в Sol — тоже вторые, но по своему списку.
    expect(linkGateNumbers(galaxy, 'sol', 'tau')).toEqual({ a: 2, b: 2 });
    expect(linkGateNumbers(galaxy, 'sol', 'vega')).toEqual({ a: 1, b: 1 });
    expect(linkGateNumbers(galaxy, 'tau', 'sigma')).toEqual({ a: 1, b: 1 });
    // Врат между ними нет — подписывать нечего.
    expect(linkGateNumbers(galaxy, 'sol', 'sigma')).toBeNull();
    // Старый сервер списка врат не прислал: номеров нет, карта просто рисует линию.
    expect(linkGateNumbers({ ...galaxy, systems: galaxy.systems.map(({ gates, ...s }) => s) }, 'sol', 'tau')).toBeNull();
  });
});
