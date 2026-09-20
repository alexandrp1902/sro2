import { describe, expect, it } from 'vitest';
import vectors from '../../../shared/test-vectors/fitting.json';
import type { WeaponConfig } from './combat';
import { canInstall, effectiveHull, fitGet, fitOutput, fitPower, fitWith, hullSlots, weaponIndex, type ModuleConfig, type ShipFit } from './fitting';
import type { HullConfig } from './movement';

const hulls = vectors.hulls as unknown as HullConfig;
const weapons = vectors.weapons as unknown as WeaponConfig;
const modules = vectors.modules as unknown as ModuleConfig;
const starter: ShipFit = {
  weapons: ['pulse'],
  engine: 'engineS',
  shield: 'shieldS',
  radar: 'radarS',
  generator: 'generatorS',
};

describe('effectiveHull', () => {
  it('matches the server on the shared vectors', () => {
    expect(vectors.cases.length).toBeGreaterThan(2);
    for (const c of vectors.cases) {
      const fit = c.fit as ShipFit;
      expect(effectiveHull(hulls[c.hull], fit, modules)).toEqual(c.effective);
      expect(fitPower(fit, weapons, modules)).toBe(c.power);
      expect(fitOutput(fit, modules)).toBe(c.output);
    }
  });

  it('is the hull itself without modules or without a fit', () => {
    expect(effectiveHull(hulls.light, starter, null)).toBe(hulls.light);
    expect(effectiveHull(hulls.light, null, modules)).toBe(hulls.light);
  });
});

describe('canInstall', () => {
  it('mirrors the server checks: slot, class, required, power', () => {
    const light = hulls.light;
    expect(canInstall(light, starter, 'w1', 'pulse', weapons, modules)).toBeNull();
    expect(canInstall(light, starter, 'w2', 'pulse', weapons, modules)).toBe('slot');
    expect(canInstall(light, starter, 'w0', 'plasma', weapons, modules)).toBe('class');
    expect(canInstall(light, starter, 'engine', 'engineL', weapons, modules)).toBe('class');
    expect(canInstall(light, starter, 'engine', null, weapons, modules)).toBe('required');
    expect(canInstall(light, starter, 'shield', null, weapons, modules)).toBeNull();
    const hungry = { ...weapons, hungry: { ...weapons.pulse, power: 45 } };
    expect(canInstall(light, starter, 'w1', 'hungry', hungry, modules)).toBe('power');
    expect(canInstall(light, fitWith(starter, 'shield', null), 'w0', 'hungry', hungry, modules)).toBeNull();
  });
});

describe('slots', () => {
  it('addresses weapon slots and modules by name', () => {
    const fit = fitWith(fitWith({ weapons: [] }, 'w2', 'pulse'), 'tank', 'tankS');
    expect(fit.weapons).toEqual([null, null, 'pulse']);
    expect(fitGet(fit, 'w2')).toBe('pulse');
    expect(fitGet(fit, 'tank')).toBe('tankS');
    expect(weaponIndex('w3')).toBe(3);
    expect(weaponIndex('engine')).toBeNull();
    expect(hullSlots(hulls.heavy)).toEqual(['L', 'M']);
    expect(hullSlots({ ...hulls.light, weaponSlots: undefined, class: undefined })).toEqual(['L']);
  });
});
