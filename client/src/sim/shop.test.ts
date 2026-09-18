import { describe, expect, it } from 'vitest';
import { NO_SHOP, formatCredits, price, repairCost } from './shop';

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

  it('groups thousands', () => {
    expect(formatCredits(1800).replace(/\s/g, ' ')).toBe('1 800 кр');
    expect(formatCredits(40)).toBe('40 кр');
  });
});
