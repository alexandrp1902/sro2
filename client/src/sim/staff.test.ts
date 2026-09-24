import { describe, expect, it } from 'vitest';
import { staffOf, type StaffRole } from './staff';

const ROLES: StaffRole[] = ['missions', 'cargo', 'hulls', 'ships'];
const PLACES = ['st:sol', 'pl:terra', 'st:vega', 'pl:vega1', 'st:alpha', 'st:tau', 'pl:tau1', 'st:nova', 'st:castor', 'st:sigma'];

describe('люди дока', () => {
  it('у каждого имя и фамилия, а не должность', () => {
    for (const place of PLACES) {
      for (const role of ROLES) {
        const person = staffOf(place, role);
        expect(person.name).toMatch(/^\S+ \S+$/);
        expect(person.name.toLowerCase()).not.toContain(person.role);
        expect(person.line.length).toBeGreaterThan(10);
      }
    }
  });

  it('одно место — всегда те же люди с теми же словами', () => {
    expect(staffOf('st:vega', 'missions', 'ring-office')).toEqual(staffOf('st:vega', 'missions', 'ring-office'));
    expect(staffOf('st:vega', 'hulls')).toEqual(staffOf('st:vega', 'hulls'));
  });

  it('имя совпадает с портретом: пол и происхождение берутся с картинки', () => {
    // ring-office — темнокожая женщина: имя из африканского пула, фамилия без «-ов».
    const ring = staffOf('st:sol', 'missions', 'ring-office');
    expect(['Амара', 'Зара', 'Абени', 'Нжери', 'Айо', 'Фолами', 'Имани']).toContain(ring.name.split(' ')[0]);
    // trade-office — мужчина-азиат.
    const trade = staffOf('st:sol', 'missions', 'trade-office');
    expect(['Вэй', 'Кэндзи', 'Хару', 'Джун', 'Тао', 'Рю', 'Кайто', 'Шэн', 'Ичиро', 'Тэо']).toContain(trade.name.split(' ')[0]);
    // fortress-trader — полинезиец с татуировками.
    const fortress = staffOf('st:sol', 'cargo', 'fortress-trader');
    expect(['Кале', 'Тама', 'Ману', 'Каи', 'Ноа', 'Ранги']).toContain(fortress.name.split(' ')[0]);
    // mining-trader — мужчина из степей.
    const mining = staffOf('st:sol', 'cargo', 'mining-trader');
    expect(['Нурлан', 'Ерлан', 'Азамат', 'Санжар', 'Бекзат', 'Даулет']).toContain(mining.name.split(' ')[0]);
  });

  it('общий портрет — разные люди одного типа: имена свои, пул один', () => {
    // Ледяная Вега и Станция Мороз пока делят одну картинку (пачка L заказывает каждой свою):
    // торговки там разные, но обе — скандинавки, как на портрете.
    const vega = staffOf('pl:vegaOne', 'cargo', 'ice-trader');
    const frost = staffOf('pl:sigmaIce', 'cargo', 'ice-trader');
    expect(vega.name).not.toBe(frost.name);
    const nordicF = ['Ингрид', 'Астрид', 'Фрейя', 'Сигрид', 'Хельга', 'Линнея', 'Сольвейг', 'Карин', 'Ингер', 'Тира', 'Рагна', 'Эбба'];
    expect(nordicF).toContain(vega.name.split(' ')[0]);
    expect(nordicF).toContain(frost.name.split(' ')[0]);
    // И приветствия свои в каждом доке.
    const lines = new Set(PLACES.map((p) => staffOf(p, 'missions', 'ring-office').line));
    expect(lines.size).toBeGreaterThan(3);
  });

  it('во всех местах галактики, деливших сцену, имена не совпадают', () => {
    // Реальные группы из shared/galaxy.json: у кого один набор сцен — те не должны быть тёзками.
    const groups: [string, string[]][] = [
      ['barren', ['pl:alphaTwo', 'pl:castorOre']],
      ['ice', ['pl:vegaOne', 'pl:sigmaIce']],
      ['lava', ['pl:aldPyre', 'pl:edgeAsh']],
      ['station', ['st:tau', 'st:sigma', 'st:edge']],
    ];
    for (const [set, places] of groups) {
      for (const art of [`${set}-office`, `${set}-trader`] as const) {
        const role = art.endsWith('office') ? 'missions' : 'cargo';
        const names = places.map((p) => staffOf(p, role, art).name);
        expect(new Set(names).size).toBe(places.length);
      }
    }
  });

  it('торговец джунглей — ящер: одно имя без фамилии', () => {
    const alien = staffOf('pl:grove', 'cargo', 'jungle-trader');
    expect(alien.name).toMatch(/^\S+$/);
    expect(alien.role).toBe('торговец');
    expect(alien.line.length).toBeGreaterThan(10);
  });

  it('верфь и ангар без портрета: люди свои у каждого места', () => {
    const names = new Set(PLACES.map((p) => staffOf(p, 'hulls', 'station-shipyard').name));
    expect(names.size).toBeGreaterThan(PLACES.length / 2);
  });

  it('женская фамилия склоняется только у славян и степей, и всегда при женском имени', () => {
    for (let i = 0; i < 300; i++) {
      const [first, last] = staffOf(`k${i}`, 'ships').name.split(' ');
      if (/(ова|ева|ёва|ина)$/.test(last)) {
        // Женская форма: имя обязано быть из женского пула, а тот весь на «-а/-я» либо степной.
        expect(/[аяь]$/.test(first) || ['Айгуль', 'Асель', 'Жанар', 'Салтанат'].includes(first)).toBe(true);
      }
    }
  });
});
