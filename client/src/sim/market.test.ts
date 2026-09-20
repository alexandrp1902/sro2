import { describe, expect, it } from 'vitest';

import vectors from '../../../shared/test-vectors/market.json';
import {
  affordable,
  buyPrice,
  hasMarket,
  isIllegal,
  mid,
  norm,
  NO_MARKET,
  sellPrice,
  sells,
  rumourLine,
  stockLevel,
  tradeCost,
  trades,
  trend,
  type MarketRules,
} from './market';

const FOOD = 'food';
const ORE = 'ore';
const ARMS = 'arms';
const BASE = 30;

/** Станция делает продовольствие и оружие, скупает руду; в Ядре оружие вне закона. */
function rules(region = 'core'): MarketRules {
  return {
    goods: {
      [FOOD]: { baseline: 100 },
      [ORE]: { baseline: 100 },
      [ARMS]: { baseline: 50, illegal: ['core'] },
    },
    station: { produces: [FOOD, ARMS], consumes: [ORE] },
    region,
  };
}

describe('рынок станции', () => {
  it('без правил рынка ничем не торгует', () => {
    expect(hasMarket(NO_MARKET)).toBe(false);
    expect(trades(NO_MARKET, FOOD)).toBe(false);
    expect(sells(NO_MARKET, FOOD)).toBe(false);
  });

  it('продаёт только то, что производит, а скупает всё', () => {
    const r = rules();
    expect(sells(r, FOOD)).toBe(true);
    expect(sells(r, ORE)).toBe(false); // руду тут скупают, но не перепродают
    expect(trades(r, ORE)).toBe(true);
  });

  it('контрабанду не берут там, где она вне закона', () => {
    expect(isIllegal(rules('core'), ARMS)).toBe(true);
    expect(trades(rules('core'), ARMS)).toBe(false);
    expect(sells(rules('rim'), ARMS)).toBe(true);
  });

  it('у производителя склад больше, чем у потребителя', () => {
    const r = rules();
    expect(norm(r, FOOD)).toBeGreaterThan(norm(r, ORE));
  });

  it('производитель отдаёт дешевле нормы, потребитель платит дороже', () => {
    const r = rules();
    expect(buyPrice(r, FOOD, BASE, norm(r, FOOD))).toBeLessThan(BASE);
    expect(sellPrice(r, ORE, 10, norm(r, ORE))).toBeGreaterThan(10);
  });

  it('цена падает с ростом запаса', () => {
    const r = rules();
    const n = norm(r, FOOD);
    expect(buyPrice(r, FOOD, BASE, n / 4)).toBeGreaterThan(buyPrice(r, FOOD, BASE, n));
    expect(buyPrice(r, FOOD, BASE, n)).toBeGreaterThan(buyPrice(r, FOOD, BASE, n * 3));
  });

  it('цена зажата сверху и снизу', () => {
    const r = rules();
    expect(mid(r, FOOD, BASE, 0)).toBeLessThanOrEqual(BASE * 2.2 + 1e-9);
    expect(mid(r, FOOD, BASE, 1e9)).toBeGreaterThanOrEqual(BASE * 0.45 - 1e-9);
  });

  it('купить всегда дороже, чем продать — на любом запасе', () => {
    const r = rules();
    for (const stock of [0, 1, 50, 100, 100000]) {
      expect(buyPrice(r, FOOD, BASE, stock)).toBeGreaterThan(sellPrice(r, FOOD, BASE, stock));
    }
  });

  it('цена пачки шагает по единицам, а не берётся по первой штуке', () => {
    const r = rules();
    const n = norm(r, FOOD);
    const bulk = tradeCost(r, FOOD, BASE, n, 10, true);
    expect(bulk).toBeGreaterThan(buyPrice(r, FOOD, BASE, n) * 10);
  });

  it('«на сколько хватит» согласовано с ценой пачки', () => {
    const r = rules();
    const n = norm(r, FOOD);
    const credits = 200;
    const count = affordable(r, FOOD, BASE, n, 50, credits);

    expect(tradeCost(r, FOOD, BASE, n, count, true)).toBeLessThanOrEqual(credits);
    // Ещё одна штука уже не влезает в кошелёк.
    expect(tradeCost(r, FOOD, BASE, n, count + 1, true)).toBeGreaterThan(credits);
  });

  it('стрелка молчит около обычной цены', () => {
    expect(trend(31, 29, BASE)).toBe('even');
    expect(trend(60, 55, BASE)).toBe('up');
    expect(trend(15, 14, BASE)).toBe('down');
  });

  it('подсказка о складе', () => {
    expect(stockLevel(10, 100)).toBe('low');
    expect(stockLevel(100, 100)).toBe('normal');
    expect(stockLevel(300, 100)).toBe('high');
  });
});

/**
 * Те же числа, что у сервера: shared/test-vectors/market.json пишет MarketVectorTests.
 * Перегенерация после правки формулы: SRO_UPDATE_VECTORS=1 dotnet test server/Sro.sln.
 */
describe('эталоны рынка совпадают с сервером', () => {
  const stations = vectors.stations as Record<string, MarketRules>;

  it('цена штуки', () => {
    expect(vectors.price.length).toBeGreaterThan(0);
    for (const c of vectors.price) {
      const r = stations[c.station];
      expect([c.station, c.good, c.stock, buyPrice(r, c.good, c.base, c.stock)]).toEqual([c.station, c.good, c.stock, c.buy]);
      expect([c.station, c.good, c.stock, sellPrice(r, c.good, c.base, c.stock)]).toEqual([c.station, c.good, c.stock, c.sell]);
    }
  });

  it('сделка целиком', () => {
    for (const c of vectors.trade) {
      const credits = tradeCost(stations[c.station], c.good, c.base, c.stock, c.count, c.buying);
      expect([c.station, c.good, c.count, c.buying, credits]).toEqual([c.station, c.good, c.count, c.buying, c.credits]);
    }
  });

  it('на сколько хватит кредитов', () => {
    for (const c of vectors.afford) {
      const count = affordable(stations[c.station], c.good, c.base, c.stock, c.max, c.credits);
      expect([c.station, c.good, c.credits, count]).toEqual([c.station, c.good, c.credits, c.count]);
    }
  });
});

describe('слухи торговца', () => {
  it('маршрут: куда везти и сколько с этого', () => {
    const line = rumourLine(
      { kind: 'route', good: 'food', system: 'aldebaran', name: 'Альдебаран', hops: 3, price: 64, profit: 41 },
      'Продовольствие',
    );
    expect(line).toContain('Альдебаран');
    expect(line).toContain('в трёх прыжках');
    expect(line).toContain('64 кр');
    expect(line).toContain('41 кр с штуки');
  });

  it('дефицит объясняется по-человечески, а не числом', () => {
    const epidemic = rumourLine(
      { kind: 'route', good: 'medicine', system: 'epsilon', name: 'Эпсилон', hops: 2, price: 120, scarce: true },
      'Медикаменты',
    );
    const famine = rumourLine(
      { kind: 'route', good: 'food', system: 'epsilon', name: 'Эпсилон', hops: 2, price: 70, scarce: true },
      'Продовольствие',
    );
    expect(epidemic).toContain('эпидемия');
    expect(famine).toContain('голодают');
  });

  it('без дефицита обходится без выдумок про эпидемию', () => {
    const line = rumourLine(
      { kind: 'route', good: 'medicine', system: 'nova', name: 'Nova', hops: 1, price: 90 },
      'Медикаменты',
    );
    expect(line).not.toContain('эпидемия');
    expect(line).toContain('Nova');
  });

  it('завал: где взять дёшево', () => {
    const line = rumourLine({ kind: 'glut', good: 'ore', system: 'castor', name: 'Кастор', hops: 2, price: 5 }, 'Руда');
    expect(line).toContain('завал');
    expect(line).toContain('Кастор');
    expect(line).toContain('5 кр');
  });

  it('незнакомый товар не ломает строку', () => {
    const line = rumourLine({ kind: 'route', good: 'ghost', system: 'x', name: 'X', hops: 7, price: 10, scarce: true }, 'Нечто');
    expect(line).toContain('в 7 прыжках');
    expect(line).toContain('Нечто'.toLowerCase());
  });
});
