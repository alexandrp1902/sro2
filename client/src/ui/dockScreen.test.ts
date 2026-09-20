import { describe, expect, it } from 'vitest';
import type { LootRules } from '../sim/loot';
import type { MarketRules } from '../sim/market';
import type { ReputationRules } from '../sim/reputation';
import {
  clampQty,
  maxBuyable,
  offerState,
  placeKind,
  repChip,
  repGateNote,
  repLogLine,
  sceneUrl,
  slotOffer,
  weaponLabel,
} from './dockScreen';

describe('offerState', () => {
  it('puts what is on the ship first, then the hangar', () => {
    expect(offerState(true, true, 0, 0)).toBe('active');
    expect(offerState(true, false, 3000, 0)).toBe('owned'); // купленное ставится бесплатно
  });

  it('sells only what is priced, and only with enough credits', () => {
    expect(offerState(false, false, 800, 1000)).toBe('buy');
    expect(offerState(false, false, 800, 800)).toBe('buy');
    expect(offerState(false, false, 800, 799)).toBe('poor');
    expect(offerState(false, false, null, 1e9)).toBe('none');
  });
});

describe('weaponLabel', () => {
  it('shows what matters for choosing a gun', () => {
    const label = weaponLabel({
      name: 'Лазер Mk1', damage: 40, accuracy: 90, cooldown: 0.5, optimalRange: 300, maxRange: 500,
      rangePenalty: 35, closeRange: 0, closePenalty: 0, arc: 180, kind: 'beam', color: '#6ff0ff',
    });
    expect(label).toBe('урон 40 · раз в 0.5 с · точность 90% · дальность 500');
  });
});

describe('slotOffer', () => {
  it('prefers what is installed, then the storage, then the shop', () => {
    expect(slotOffer(true, 3, 100, 0, null)).toEqual({ action: 'installed' });
    expect(slotOffer(false, 1, 100, 0, 'power')).toEqual({ action: 'install', problem: 'power' });
    expect(slotOffer(false, 0, 100, 99, null)).toEqual({ action: 'buy', cost: 100, problem: null, poor: true });
    expect(slotOffer(false, 0, 100, 100, null)).toEqual({ action: 'buy', cost: 100, problem: null, poor: false });
    expect(slotOffer(false, 0, null, 1e9, null)).toEqual({ action: 'none' });
  });
});

describe('счётчик на рынке', () => {
  it('не уходит за границы', () => {
    expect(clampQty(5, 10)).toBe(5);
    expect(clampQty(0, 10)).toBe(1);
    expect(clampQty(-3, 10)).toBe(1);
    expect(clampQty(40, 10)).toBe(10);
    expect(clampQty(5, 0)).toBe(0); // покупать нечего — кнопок не будет
  });
});

describe('maxBuyable', () => {
  const loot: LootRules = {
    items: {
      food: { name: 'Продовольствие', rarity: 'common', volume: 1, price: 30 },
      machinery: { name: 'Машины', rarity: 'uncommon', volume: 3, price: 90 },
    },
  } as unknown as LootRules;
  const market: MarketRules = {
    goods: { food: { baseline: 100 }, machinery: { baseline: 100 } },
    station: { produces: ['food', 'machinery'] },
    region: 'core',
  };

  it('упирается в склад станции', () => {
    const quote = { id: 'food', buy: 23, sell: 19, stock: 4, norm: 250 };
    expect(maxBuyable(market, loot, quote, 1e6, 1000)).toBe(4);
  });

  it('упирается в трюм — и объём товара считается', () => {
    const quote = { id: 'machinery', buy: 70, sell: 60, stock: 999, norm: 250 };
    // Свободно 10 единиц объёма, «машины» занимают по 3 — влезут три штуки.
    expect(maxBuyable(market, loot, quote, 1e6, 10)).toBe(3);
  });

  it('упирается в кошелёк', () => {
    const quote = { id: 'food', buy: 23, sell: 19, stock: 999, norm: 250 };
    const count = maxBuyable(market, loot, quote, 100, 1000);
    expect(count).toBeGreaterThan(0);
    expect(count).toBeLessThan(10);
  });

  it('без места не даёт купить ничего', () => {
    const quote = { id: 'food', buy: 23, sell: 19, stock: 999, norm: 250 };
    expect(maxBuyable(market, loot, quote, 1e6, 0)).toBe(0);
  });
});

describe('weaponLabel for a launcher', () => {
  it('says the missile homes instead of showing accuracy', () => {
    const label = weaponLabel({
      name: 'Ракетница', damage: 220, accuracy: 100, cooldown: 5, optimalRange: 700, maxRange: 700,
      rangePenalty: 0, closeRange: 0, closePenalty: 0, arc: 90, kind: 'missile', color: '#ff6b3d',
      class: 'M', power: 25, missile: { speed: 330, turnRate: 120, lifetime: 5, hitRadius: 10 },
    });
    expect(label).toBe('M · урон 220 · раз в 5 с · самонаведение · дальность 700 · энергия 25');
  });
});

describe('репутация в доке', () => {
  const RULES: ReputationRules = {
    limit: 100,
    levels: [
      { id: 'enemy', name: 'Враг', from: -100, price: 1.1, color: '#c0392b' },
      { id: 'neutral', name: 'Нейтрал', from: -10 },
      { id: 'friend', name: 'Друг', from: 30, price: 0.95, color: '#3f9f6a' },
    ],
    gate: { level: 'friend', tiers: [3], hulls: ['cruiser'] },
  };

  it('плашка берёт подпись и цвет у ступени', () => {
    expect(repChip(RULES, 42)).toEqual({ text: 'Друг (+42)', color: '#3f9f6a' });
    expect(repChip(RULES, -60)).toEqual({ text: 'Враг (-60)', color: '#c0392b' });
  });

  it('строка журнала читается без словаря', () => {
    expect(repLogLine({ code: 'traderKill', delta: -12, key: 'sys:vega', value: -18 }, 'Vega'))
      .toBe('-12 Vega · торговец уничтожен');
    expect(repLogLine({ code: 'missionDone', delta: 8, key: 'st:sol', value: 24 }, 'Sol'))
      .toBe('+8 Sol · задание выполнено');
  });

  it('без имени места берётся id из ключа', () => {
    expect(repLogLine({ code: 'pirate', delta: 1, key: 'sys:castor', value: 3 })).toBe('+1 castor · пират уничтожен');
  });

  it('незнакомый повод не ломает строку', () => {
    expect(repLogLine({ code: 'whatever', delta: -1, key: 'sys:sol', value: -1 }, 'Sol')).toBe('-1 Sol · whatever');
  });

  it('подпись замка называет нужную ступень', () => {
    expect(repGateNote(RULES, 'neutral', 'plasma_mk3', false)).toBe('Только для уровня «Друг»');
    expect(repGateNote(RULES, 'friend', 'plasma_mk3', false)).toBeNull();
    // Негейченное доступно всем — подписи нет даже у врага.
    expect(repGateNote(RULES, 'enemy', 'plasma', false)).toBeNull();
  });

  it('витрина под замком важнее пустого кошелька', () => {
    expect(offerState(false, false, 6000, 100, true)).toBe('locked');
    expect(offerState(false, false, 6000, 100, false)).toBe('poor');
    // Своё и стоящее на корабле замок не трогает.
    expect(offerState(true, false, 6000, 100, true)).toBe('owned');
  });
});

describe('placeKind', () => {
  it('tells a settlement from a station by its key', () => {
    expect(placeKind('pl:terra')).toBe('planet');
    expect(placeKind('st:vega')).toBe('station');
    // Места ещё нет (сервер старше M15 или корабль в космосе) — рисуем станцию, как раньше.
    expect(placeKind(null)).toBe('station');
  });
});

describe('sceneUrl', () => {
  it('falls back to the shared scenes of a station or a settlement', () => {
    expect(sceneUrl('station', 'missions')).toBe('dock/station-office.webp');
    // Общие сцены планеты и есть земное поселение: своего набора у terran нет.
    expect(sceneUrl('planet', 'missions')).toBe('dock/planet-office.webp');
    expect(sceneUrl('planet', 'fitting')).toBe('dock/planet-hangar.webp');
  });

  it('uses a set only for the scenes that were drawn for it', () => {
    expect(sceneUrl('planet', 'missions', 'lava')).toBe('dock/lava-office.webp');
    expect(sceneUrl('planet', 'cargo', 'orbital-platform')).toBe('dock/orbital-platform-trader.webp');
    // У поста рейнджеров нарисован только офис — остальное берётся общее.
    expect(sceneUrl('station', 'missions', 'ranger')).toBe('dock/ranger-office.webp');
    expect(sceneUrl('station', 'cargo', 'ranger')).toBe('dock/station-trader.webp');
    // Незнакомый набор не должен уводить на несуществующую картинку.
    expect(sceneUrl('planet', 'missions', 'swamp')).toBe('dock/planet-office.webp');
  });
});

