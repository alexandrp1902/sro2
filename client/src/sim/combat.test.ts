import { describe, expect, it } from 'vitest';
import vectors from '../../../shared/test-vectors/combat.json';
import { assess, assessBest, cooldownTicks, evasion, hitChance, hitChanceByEvasion, inArc, inRange, longestRange, type AimTarget, type WeaponParams } from './combat';
import type { HullParams } from './movement';

/** Одни и те же операции в .NET и V8; допуск — на последний бит atan2. */
const TOLERANCE = 1e-9;

const weapons = vectors.weapons as Record<string, WeaponParams>;
const hulls = vectors.hulls as Record<string, HullParams>;

// Эталоны генерирует C# (server/Sro.Sim.Tests/CombatVectorTests.cs): карточка цели обязана считать как сервер.
describe('combat matches the server (shared/test-vectors/combat.json)', () => {
  it('hit chance and range', () => {
    for (const c of vectors.hitChance) {
      const actual = hitChance(weapons[c.weapon], c.distance, hulls[c.hull], c.speed);
      if (Math.abs(actual - c.chance) > TOLERANCE) {
        throw new Error(`${JSON.stringify(c)}: got ${actual}`);
      }
      expect(inRange(weapons[c.weapon], c.distance)).toBe(c.inRange);
    }
  });

  it('hit chance by evasion', () => {
    for (const c of vectors.hitChanceByEvasion) {
      const actual = hitChanceByEvasion(weapons[c.weapon], c.distance, c.evasion);
      if (Math.abs(actual - c.chance) > TOLERANCE) throw new Error(`${JSON.stringify(c)}: got ${actual}`);
    }
  });

  it('weapon arc', () => {
    for (const c of vectors.arc) expect(inArc(c.rot, c.dx, c.dy, c.arc), JSON.stringify(c)).toBe(c.inArc);
  });
});

describe('combat helpers', () => {
  const pulse = weapons.pulse;
  const light = hulls.light;
  const target = (x: number, y: number, extra: Partial<AimTarget> = {}): AimTarget => ({
    x,
    y,
    vx: 0,
    vy: 0,
    dead: false,
    protected: false,
    ...extra,
  });
  const shooter = { x: 0, y: 0, rot: 0 };
  const lightStill = evasion(light, 0);

  it('rounds the cooldown up to whole ticks', () => {
    expect(cooldownTicks(pulse)).toBe(20);
    expect(cooldownTicks(weapons.laser)).toBe(10);
    expect(cooldownTicks({ ...pulse, cooldown: 0.01 })).toBe(1);
  });

  it('explains why the gun does not fire', () => {
    expect(assess(shooter, pulse, target(0, -300), lightStill)).toEqual({ state: 'ready', distance: 300, chance: 50 });
    // Метеорит: уклонения нет, шанс — точность пушки.
    expect(assess(shooter, pulse, target(0, -300), 0).chance).toBe(pulse.accuracy);
    expect(assess(shooter, pulse, target(0, 300), lightStill).state).toBe('arc');
    expect(assess(shooter, pulse, target(0, -800), lightStill).state).toBe('range');
    expect(assess(shooter, pulse, target(0, -300, { protected: true }), lightStill).state).toBe('protected');
    expect(assess(shooter, pulse, target(0, -300, { dead: true, protected: true }), lightStill).state).toBe('dead');
  });
});

describe('assessBest', () => {
  const target: AimTarget = { x: 600, y: 0, vx: 0, vy: 0, dead: false, protected: false };
  const shooter = { x: 0, y: 0, rot: Math.PI / 2 };
  const short = { ...weapons[Object.keys(weapons)[0]], maxRange: 400, optimalRange: 300 };
  const long = { ...short, maxRange: 800, optimalRange: 700 };

  it('takes the gun that is ready to fire', () => {
    expect(assessBest(shooter, [short, long], target, 0)?.weapon).toBe(long);
  });

  it('otherwise shows the longest-range gun, and nothing without guns', () => {
    const far = { ...target, x: 2000 };
    expect(assessBest(shooter, [short, long], far, 0)?.weapon).toBe(long);
    expect(assessBest(shooter, [short, long], far, 0)?.aim.state).toBe('range');
    expect(assessBest(shooter, [], target, 0)).toBeNull();
    expect(longestRange([short, long])).toBe(long);
    expect(longestRange([])).toBeNull();
  });
});
