import { describe, expect, it } from 'vitest';
import type { GalaxyDto } from '../net/protocol';
import { gateBadges, mapViewBox, nodeBadges, regionLabelAt, routePoints } from './galaxyLayout';

const galaxy: GalaxyDto = {
  systems: [
    { id: 'sol', name: 'Sol', danger: 1, pvp: 'off', station: true, x: 10, y: 50, gates: ['vega', 'tau'] },
    { id: 'vega', name: 'Vega', danger: 2, pvp: 'border', station: true, x: 40, y: 20, gates: ['sol', 'nova'] },
    { id: 'tau', name: 'Tau', danger: 3, pvp: 'border', station: false, x: 40, y: 80, gates: ['sol'] },
    { id: 'nova', name: 'Nova', danger: 4, pvp: 'free', station: true, x: 70, y: 50, gates: ['vega'] },
  ],
  links: [
    { a: 'sol', b: 'vega' },
    { a: 'sol', b: 'tau' },
    { a: 'vega', b: 'nova' },
  ],
};

describe('mapViewBox', () => {
  it('обрамляет системы полями', () => {
    expect(mapViewBox(galaxy.systems, 10)).toEqual({ x: 0, y: 10, w: 80, h: 80 });
  });

  it('пустая галактика — прежний квадрат', () => {
    expect(mapViewBox([])).toEqual({ x: 0, y: 0, w: 100, h: 100 });
  });
});

describe('regionLabelAt', () => {
  it('стоит над верхней системой посередине', () => {
    expect(regionLabelAt(galaxy.systems.slice(0, 3), 10)).toEqual({ x: 30, y: 10 });
  });
});

describe('nodeBadges', () => {
  it('занимает слоты по порядку появления', () => {
    const badges = nodeBadges({ objective: true, invasion: true }, 10);
    expect(badges.map((b) => b.kind)).toEqual(['objective', 'invasion']);
    expect(badges[0]).toMatchObject({ dx: 7.07, dy: -7.07 }); // северо-восток
    expect(badges[1]).toMatchObject({ dx: -7.07, dy: -7.07 }); // северо-запад
  });

  it('дом идёт первым, спрос последним', () => {
    expect(nodeBadges({ demand: true, home: true }).map((b) => b.kind)).toEqual(['home', 'demand']);
  });

  it('без флагов — пусто', () => {
    expect(nodeBadges({})).toEqual([]);
  });
});

describe('routePoints', () => {
  it('укорачивает оба конца, середину не трогает', () => {
    const points = routePoints([{ x: 0, y: 0 }, { x: 30, y: 0 }, { x: 30, y: 40 }], 5);
    expect(points).toEqual([{ x: 5, y: 0 }, { x: 30, y: 0 }, { x: 30, y: 35 }]);
  });

  it('одна точка — пусто', () => {
    expect(routePoints([{ x: 1, y: 1 }])).toEqual([]);
  });
});

describe('gateBadges', () => {
  it('у текущей системы — на каждой её связи, со своим номером', () => {
    const badges = gateBadges(galaxy, 'sol', []);
    expect(badges).toHaveLength(2);
    expect(badges.map((b) => b.n).sort()).toEqual([1, 2]);
    expect(badges.every((b) => !b.route)).toBe(true);
    // Бейдж отходит от Sol к Vega, а не наоборот.
    const toVega = badges.find((b) => b.n === 1)!;
    expect(toVega.x).toBeGreaterThan(10);
    expect(toVega.y).toBeLessThan(50);
  });

  it('на курсе — у начала каждого прыжка, и свой прыжок отмечен как курс', () => {
    const badges = gateBadges(galaxy, 'sol', ['sol', 'vega', 'nova']);
    const route = badges.filter((b) => b.route);
    expect(route).toHaveLength(2);
    expect(route.map((b) => b.n)).toEqual([1, 2]); // Sol→Vega — врата 1 в Sol, Vega→Nova — врата 2 в Vega
    // Связь Sol–Tau не на курсе, но у текущей системы номер всё равно есть.
    expect(badges.filter((b) => !b.route)).toHaveLength(1);
  });

  it('без списка врат у сервера бейджей нет', () => {
    const bare = { ...galaxy, systems: galaxy.systems.map((s) => ({ ...s, gates: null })) };
    expect(gateBadges(bare, 'sol', ['sol', 'vega'])).toEqual([]);
  });
});
