import { describe, expect, it } from 'vitest';
import { RaceParticles } from './particles';

describe('race visual particles', () => {
  it('carries fire and both detached engines forward with the crashed vehicle', () => {
    const fx = new RaceParticles(() => .5);
    fx.explode({ x: 10, y: 20 }, { x: 30, y: 4 }, 0, .6, 1);
    const engines = fx.particles.filter(p => p.kind === 'engine');
    expect(engines).toHaveLength(2);
    expect(engines[0].y).toBeCloseTo(19.4); expect(engines[1].y).toBeCloseTo(20.6);
    expect(engines.every(p => p.vx > 25)).toBe(true);
    fx.update(.05);
    expect(engines.every(p => p.x > 12)).toBe(true);
    expect(fx.particles.filter(p => p.kind === 'fire').some(p => p.x > 11)).toBe(true);
    for (let i = 0; i < 160; i++) fx.update(.05);
    expect(fx.particles).toHaveLength(0);
  });
  it('emits sparks at the contact point toward the free side of the wall', () => {
    const fx = new RaceParticles(() => .5), normal = { x: -1, y: 0 };
    fx.hit({ x: 4, y: 9 }, { x: 6, y: 0 }, normal, 3);
    expect(fx.particles.length).toBeGreaterThan(0);
    expect(fx.particles.every(p => p.x === 4 && p.y === 9 && p.vx < 0 && p.kind === 'spark')).toBe(true);
  });
  it('emits more lingering smoke as health falls and stops emission on repair', () => {
    const source = { position: { x: 0, y: 0 }, velocity: { x: 10, y: 0 }, hp: 100, maxHp: 100 };
    const healthy = new RaceParticles(() => .5), damaged = new RaceParticles(() => .5), critical = new RaceParticles(() => .5);
    for (let i = 0; i < 20; i++) {
      healthy.update(.05, source); damaged.update(.05, { ...source, hp: 40 }); critical.update(.05, { ...source, hp: 5 });
    }
    expect(healthy.particles).toHaveLength(0);
    expect(critical.particles.length).toBeGreaterThan(damaged.particles.length);
    expect(critical.particles.some(p => p.x > 1)).toBe(true);
    for (let i = 0; i < 70; i++) critical.update(.05, source);
    expect(critical.particles).toHaveLength(0);
  });
  it('keeps trajectories stable across frame rates and resets between races', () => {
    const a = new RaceParticles(() => .5), b = new RaceParticles(() => .5);
    for (const fx of [a, b]) fx.hit({ x: 0, y: 0 }, { x: 8, y: 0 }, { x: 0, y: 1 }, 2);
    for (let i = 0; i < 6; i++) a.update(1 / 30);
    for (let i = 0; i < 12; i++) b.update(1 / 60);
    expect(a.particles[0].x).toBeCloseTo(b.particles[0].x, 10);
    expect(a.particles[0].y).toBeCloseTo(b.particles[0].y, 10);
    a.reset(); expect(a.particles).toHaveLength(0);
  });
});
