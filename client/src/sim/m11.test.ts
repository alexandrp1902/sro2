import { describe, expect, it } from 'vitest';
import {
  MAX_UTILITY_SLOTS,
  UTILITY,
  canInstall,
  effectiveHull,
  fitCooldown,
  fitGet,
  fitPower,
  fitRepair,
  fitWith,
  hullUtilitySlots,
  tierBadge,
  tierOf,
  utilityIndex,
  utilitySlot,
  type ModuleConfig,
  type ShipFit,
} from './fitting';
import { ION_SLOW, slowedHull, type HullParams } from './movement';
import { NO_SHOP, sells, type ShopRules } from './shop';
import type { WeaponConfig } from './combat';

const HULL: HullParams = {
  name: '«Пчела»',
  maxSpeed: 165,
  acceleration: 180,
  brakeAcceleration: 220,
  turnRate: 150,
  lateralDampTime: 0.65,
  lateralToForward: 0,
  size: 24,
  hp: 400,
  shield: 150,
  shieldRegen: 20,
  evasion: 25,
  moveEvasion: 8,
  cargo: 20,
  fuel: 100,
  radar: 2000,
  class: 'S',
  weaponSlots: ['S', 'S'],
  utilitySlots: 2,
};

const MODULES: ModuleConfig = {
  engineS: { name: 'Двигатель', slot: 'engine', class: 'S', power: 5 },
  shieldS: { name: 'Щит', slot: 'shield', class: 'S', power: 10, shield: 150, shieldRegen: 20 },
  radarS: { name: 'Радар', slot: 'radar', class: 'S', power: 5, radar: 2000 },
  tankS: { name: 'Бак', slot: 'tank', class: 'S', fuel: 100 },
  generatorS: { name: 'Генератор', slot: 'generator', class: 'S', output: 200 },
  repair: { name: 'Ремонтный блок', slot: UTILITY, class: 'S', power: 10, repair: 8 },
  cooling: { name: 'Охлаждение', slot: UTILITY, class: 'S', power: 12, cooling: 0.1 },
  cargoPod: { name: 'Грузовой расширитель', slot: UTILITY, class: 'S', power: 4, cargo: 10 },
  bigPod: { name: 'Тяжёлый расширитель', slot: UTILITY, class: 'L', power: 4, cargo: 40 },
};

const WEAPONS: WeaponConfig = {
  pulse: { name: 'Импульсная', kind: 'bolt', color: '#fff', class: 'S', power: 15, damage: 100, accuracy: 75, cooldown: 1, optimalRange: 500, maxRange: 700, rangePenalty: 10, closeRange: 0, closePenalty: 0, arc: 180 },
};

const FIT: ShipFit = { weapons: ['pulse', null], engine: 'engineS', shield: 'shieldS', radar: 'radarS', tank: 'tankS', generator: 'generatorS' };

describe('utility-слоты (M11)', () => {
  it('читает и пишет слоты u0…u2', () => {
    expect(utilityIndex('u0')).toBe(0);
    expect(utilityIndex(`u${MAX_UTILITY_SLOTS}`)).toBeNull();
    expect(utilityIndex('w0')).toBeNull();
    const fit = fitWith(FIT, utilitySlot(1), 'repair');
    expect(fitGet(fit, 'u1')).toBe('repair');
    expect(fitGet(fit, 'u0')).toBeNull();
    expect(fitGet(FIT, 'u0')).toBeNull(); // старое оснащение без utility
  });

  it('прибавляет трюм, ремонт и охлаждение', () => {
    const fit = fitWith(fitWith(FIT, 'u0', 'cargoPod'), 'u1', 'repair');
    expect(effectiveHull(HULL, fit, MODULES).cargo).toBe(30);
    expect(fitRepair(fit, MODULES)).toBe(8);
    expect(fitCooldown(fit, MODULES)).toBe(1);

    const cooled = fitWith(fitWith(FIT, 'u0', 'cooling'), 'u1', 'cooling');
    expect(fitCooldown(cooled, MODULES)).toBeCloseTo(0.8, 6);
    expect(fitPower(fitWith(FIT, 'u0', 'repair'), WEAPONS, MODULES)).toBe(15 + 5 + 10 + 5 + 10);
  });

  it('проверяет слот и класс так же, как сервер', () => {
    expect(canInstall(HULL, FIT, 'u0', 'repair', WEAPONS, MODULES)).toBeNull();
    expect(canInstall(HULL, FIT, 'u2', 'repair', WEAPONS, MODULES)).toBe('slot'); // слотов два
    expect(canInstall(HULL, FIT, 'u0', 'bigPod', WEAPONS, MODULES)).toBe('class');
    expect(canInstall(HULL, FIT, 'u0', 'shieldS', WEAPONS, MODULES)).toBe('slot');
    expect(hullUtilitySlots({ ...HULL, utilitySlots: undefined })).toBe(0);
  });
});

describe('тиры (M11)', () => {
  it('читает тир из id и подписывает значок', () => {
    expect(tierOf('ion')).toBe(1);
    expect(tierOf('ion_mk2')).toBe(2);
    expect(tierOf('ion_mk3')).toBe(3);
    expect(tierBadge('ion')).toBeNull();
    expect(tierBadge('ion_mk3')).toBe('Mk3');
  });
});

describe('ассортимент станции (M11)', () => {
  const shop: ShopRules = { ...NO_SHOP, items: { pulse: 300, railgun: 6000 }, stock: ['pulse'] };

  it('купить можно только то, что здесь продают', () => {
    expect(sells(shop, 'pulse', shop.items)).toBe(true);
    expect(sells(shop, 'railgun', shop.items)).toBe(false); // цена известна, но здесь не торгуют
    expect(sells(shop, 'laser', shop.items)).toBe(false);
  });

  it('без списка ассортимента продаётся всё, что в прайсе (старый сервер)', () => {
    const old: ShopRules = { ...NO_SHOP, items: { pulse: 300 } };
    expect(sells(old, 'pulse', old.items)).toBe(true);
    expect(sells(old, 'railgun', old.items)).toBe(false);
  });
});

describe('замедление ионкой (M11)', () => {
  it('режет скорость и разгон, но не торможение', () => {
    const slow = slowedHull(HULL, ION_SLOW);
    expect(slow.maxSpeed).toBeCloseTo(99, 6);
    expect(slow.acceleration).toBeCloseTo(108, 6);
    expect(slow.brakeAcceleration).toBe(HULL.brakeAcceleration);
  });
});
