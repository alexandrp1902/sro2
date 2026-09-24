import { describe, expect, it } from 'vitest';
import vectors from '../../../shared/test-vectors/shop.json';
import { NO_SHOP, formatCredits, price, repairCost, sellHullPrice, sellShipPrice, type ShopRules } from './shop';

describe('shop', () => {
  it('knows what is not for sale', () => {
    expect(price({ laser: 800, pulse: 0 }, 'laser')).toBe(800);
    expect(price({ laser: 800, pulse: 0 }, 'pulse')).toBe(0);
    expect(price({ laser: 800 }, 'plasma')).toBeNull();
    expect(price(null, 'laser')).toBeNull();
  });

  it('rounds the repair up, like the server', () => {
    expect(repairCost({ ...NO_SHOP, repairPrice: 0.25 }, 101)).toBe(26);
    expect(repairCost({ ...NO_SHOP, repairPrice: 0.25 }, 0)).toBe(0);
    expect(repairCost(NO_SHOP, 500)).toBe(0);
  });

  it('charges more for an expensive hull, like the server since M12', () => {
    const shop = { ...NO_SHOP, repairPrice: 0.25, repairHullShare: 0.06 };
    // Крейсер за 60 000, полная прочность 100, снесено всё: 0.25 * 100 + 0.06 * 60000 = 3625.
    expect(repairCost(shop, 100, 100, 60000)).toBe(3625);
    // Половина прочности — половина надбавки.
    expect(repairCost(shop, 50, 100, 60000)).toBe(1813);
    // Стартовый корпус даром в прайсе: надбавки нет.
    expect(repairCost(shop, 100, 100, 0)).toBe(25);
    // Без maxHp считаем по-старому — на случай баланса без M12.
    expect(repairCost(shop, 100)).toBe(25);
  });

  it('groups thousands', () => {
    expect(formatCredits(1800).replace(/\s/g, ' ')).toBe('1 800 кр');
    expect(formatCredits(40)).toBe('40 кр');
  });
});

describe('выкуп корабля с оснащением совпадает с сервером (shared/test-vectors/shop.json)', () => {
  const shop = vectors.shop as unknown as ShopRules;

  it('корпус и каждая вещь округляются вниз по отдельности', () => {
    for (const c of vectors.sell) {
      expect(sellShipPrice(shop, c.hull, c.items), `${c.hull} + ${c.items.join(', ') || 'голый'}`).toBe(c.credits);
    }
  });

  it('за стартовый корпус в прайсе не дают ничего, за неизвестный — тоже', () => {
    expect(sellHullPrice(shop, 'light')).toBe(0);
    expect(sellHullPrice(shop, 'notInThePriceList')).toBe(0);
    expect(sellHullPrice(shop, 'fighter')).toBe(1500);
  });
});
