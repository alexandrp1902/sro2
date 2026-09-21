import { describe, expect, it } from 'vitest';
import type { GalaxyDto } from '../net/protocol';
import { courseLine, courseView, hopsWord, linkKey, routeLinks } from './course';
import { route } from './galaxy';

const galaxy: GalaxyDto = {
  systems: [
    { id: 'sol', name: 'Sol', danger: 1, pvp: 'off', station: true, x: 20, y: 55, gates: ['vega', 'tau'] },
    { id: 'vega', name: 'Vega', danger: 2, pvp: 'border', station: true, x: 50, y: 25, gates: ['sol'] },
    { id: 'tau', name: 'Tau', danger: 3, pvp: 'border', station: false, x: 50, y: 75, gates: ['sigma', 'sol'] },
    { id: 'sigma', name: 'Sigma', danger: 5, pvp: 'free', station: false, x: 92, y: 82, gates: ['tau'] },
    { id: 'lost', name: 'Lost', danger: 6, pvp: 'free', station: false, x: 5, y: 5, gates: [] },
  ],
  links: [
    { a: 'sol', b: 'vega' },
    { a: 'sol', b: 'tau' },
    { a: 'tau', b: 'sigma' },
  ],
};

const gatesOf = (id: string): string[] => galaxy.systems.find((s) => s.id === id)!.gates!;

describe('route', () => {
  it('walks the whole way, ends included', () => {
    expect(route(galaxy, 'sol', 'sigma')).toEqual(['sol', 'tau', 'sigma']);
    expect(route(galaxy, 'sigma', 'sol')).toEqual(['sigma', 'tau', 'sol']);
  });

  it('answers «you are here» and «no way there»', () => {
    expect(route(galaxy, 'sol', 'sol')).toEqual(['sol']);
    expect(route(galaxy, 'sol', 'lost')).toEqual([]);
  });
});

describe('courseView', () => {
  it('counts the hops and names the gate to take', () => {
    const view = courseView(galaxy, 'sol', 'sigma', gatesOf('sol'))!;
    expect(view).toMatchObject({ hops: 2, next: 'tau', gate: 2, done: false, lost: false });
    expect(courseLine(view)).toBe('Маршрут: 2 прыжка, ближайшие врата — Врата 2');
    expect(courseLine(view, 'Sigma')).toBe('Курс на Sigma: 2 прыжка, ближайшие врата — Врата 2');
  });

  it('knows the course is over', () => {
    const view = courseView(galaxy, 'sigma', 'sigma', gatesOf('sigma'))!;
    expect(view).toMatchObject({ hops: 0, next: null, gate: null, done: true });
    expect(courseLine(view)).toBe('Вы на месте: маршрут пройден');
  });

  it('knows there is no way from here', () => {
    const view = courseView(galaxy, 'sol', 'lost', gatesOf('sol'))!;
    expect(view).toMatchObject({ path: [], hops: 0, lost: true, gate: null });
    expect(courseLine(view, 'Lost')).toBe('Маршрута до Lost отсюда нет');
  });

  it('is nothing at all without a destination', () => {
    expect(courseView(galaxy, 'sol', null, gatesOf('sol'))).toBeNull();
  });
});

describe('course texts and links', () => {
  it('counts hops in Russian', () => {
    expect([1, 2, 5, 11, 21, 22, 25].map(hopsWord)).toEqual([
      '1 прыжок',
      '2 прыжка',
      '5 прыжков',
      '11 прыжков',
      '21 прыжок',
      '22 прыжка',
      '25 прыжков',
    ]);
  });

  it('marks the links of the route, whichever way they run', () => {
    const links = routeLinks(['sol', 'tau', 'sigma']);
    expect(links.has(linkKey('tau', 'sol'))).toBe(true);
    expect(links.has(linkKey('sigma', 'tau'))).toBe(true);
    expect(links.has(linkKey('sol', 'vega'))).toBe(false);
    expect(routeLinks([]).size).toBe(0);
  });
});
