import { describe, expect, it } from 'vitest';
import {
  NO_REP,
  allowsLevel,
  atLeast,
  gated,
  levelById,
  levelOf,
  repLabel,
  repPrice,
  type ReputationRules,
} from './reputation';

// Зеркало Sro.Sim.Tests/ReputationRulesTests: ступени и цены должны совпадать с сервером до кредита.
const RULES: ReputationRules = {
  limit: 100,
  levels: [
    { id: 'enemy', name: 'Враг', from: -100, price: 1.1, color: '#c0392b' },
    { id: 'distrust', name: 'Недоверие', from: -50, price: 1.05 },
    { id: 'neutral', name: 'Нейтрал', from: -10 },
    { id: 'friend', name: 'Друг', from: 30, price: 0.95 },
    { id: 'hero', name: 'Герой', from: 70, price: 0.9 },
  ],
  gate: { level: 'friend', tiers: [3], hulls: ['cruiser'] },
};

describe('репутация', () => {
  it('ступень берётся по нижней границе включительно', () => {
    for (const [value, id] of [
      [-100, 'enemy'],
      [-51, 'enemy'],
      [-50, 'distrust'],
      [-10, 'neutral'],
      [29, 'neutral'],
      [30, 'friend'],
      [69, 'friend'],
      [70, 'hero'],
    ] as const) {
      expect(levelOf(RULES, value).id, `${value}`).toBe(id);
    }
  });

  it('цена гнётся ступенью и округляется как в магазине', () => {
    // Те же числа, что в серверном ReputationRulesTests.Price_BendsByTheLevelAndRoundsLikeTheShop.
    expect(repPrice(3000, 1)).toBe(3000);
    expect(repPrice(101, 1)).toBe(101);
    expect(repPrice(3000, 0.95)).toBe(2850);
    expect(repPrice(3000, 0.9)).toBe(2700);
    expect(repPrice(3000, 1.1)).toBe(3300);
    expect(repPrice(80, 0.95)).toBe(76);
  });

  it('старший тир и топовый корпус — только друзьям', () => {
    expect(gated(RULES, 'plasma_mk3', false)).toBe(true);
    expect(gated(RULES, 'plasma_mk2', false)).toBe(false);
    expect(gated(RULES, 'cruiser', true)).toBe(true);
    expect(gated(RULES, 'medium', true)).toBe(false);
  });

  it('гейт открывается по названию ступени, как её прислал сервер', () => {
    expect(allowsLevel(RULES, 'neutral', 'plasma_mk3', false)).toBe(false);
    expect(allowsLevel(RULES, 'friend', 'plasma_mk3', false)).toBe(true);
    expect(allowsLevel(RULES, 'hero', 'cruiser', true)).toBe(true);
    // Незнакомая ступень никого не пускает: лучше не продать, чем продать не тому.
    expect(allowsLevel(RULES, 'ally', 'cruiser', true)).toBe(false);
    // Негейченное продаётся всем, даже врагу.
    expect(allowsLevel(RULES, 'enemy', 'medium', true)).toBe(true);
  });

  it('atLeast считает по очкам', () => {
    expect(atLeast(RULES, 29, 'friend')).toBe(false);
    expect(atLeast(RULES, 30, 'friend')).toBe(true);
  });

  it('подпись пишет знак только у положительных', () => {
    expect(repLabel(levelById(RULES, 'friend'), 42)).toBe('Друг (+42)');
    expect(repLabel(levelById(RULES, 'enemy'), -60)).toBe('Враг (-60)');
    expect(repLabel(levelById(RULES, 'neutral'), 0)).toBe('Нейтрал (0)');
  });

  it('без правил ничего не меняется', () => {
    expect(levelOf(NO_REP, -100).id).toBe('neutral');
    expect(gated(NO_REP, 'plasma_mk3', false)).toBe(false);
    expect(allowsLevel(NO_REP, null, 'plasma_mk3', false)).toBe(true);
  });
});
