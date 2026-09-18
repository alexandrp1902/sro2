import { describe, expect, it } from 'vitest';
import { offerState, weaponLabel } from './dockScreen';

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
