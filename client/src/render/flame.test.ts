import { describe, expect, it } from 'vitest';
import type { HullParams, ShipState } from '../sim/movement';
import { engineGlow, flameShape } from './flame';

describe('flameShape', () => {
  const layers = flameShape(64, 48);

  it('строит два слоя: ореол и ядро внутри него', () => {
    expect(layers).toHaveLength(2);
    const [glow, core] = layers;
    const halfWidth = (l: { points: number[] }) => Math.max(...even(l.points).map(Math.abs));
    const length = (l: { points: number[] }) => Math.max(...odd(l.points));
    // Ядро уже и короче ореола — иначе оно бы из него выпирало.
    expect(halfWidth(core)).toBeLessThan(halfWidth(glow));
    expect(length(core)).toBeLessThan(length(glow));
    // Ядро ярче и плотнее: это горячая часть выхлопа.
    expect(core.alpha).toBeGreaterThan(glow.alpha);
  });

  it('начинается на линии сопел и растёт назад', () => {
    for (const layer of layers) {
      // y = 0 — линия сопел; ни одна точка не уходит вперёд корабля.
      expect(Math.min(...odd(layer.points))).toBe(0);
      expect(Math.max(...odd(layer.points))).toBeGreaterThan(0);
      // Факел симметричен: сумма x по всем точкам — ноль.
      expect(even(layer.points).reduce((sum, x) => sum + x, 0)).toBeCloseTo(0, 9);
    }
  });

  it('масштабируется: у крупного корабля факел шире и длиннее', () => {
    const big = flameShape(128, 96);
    for (let i = 0; i < layers.length; i++) {
      expect(Math.max(...even(big[i].points))).toBeCloseTo(2 * Math.max(...even(layers[i].points)), 9);
      expect(Math.max(...odd(big[i].points))).toBeCloseTo(2 * Math.max(...odd(layers[i].points)), 9);
    }
  });

  it('вырождается в точку при нулевом корабле, а не в мусор', () => {
    for (const layer of flameShape(0, 0)) {
      expect(layer.points.every((v) => v === 0)).toBe(true);
    }
  });
});

describe('flameShape: палитра', () => {
  it('неон холодный, горячее пламя рыжее — а форма у них одна', () => {
    const hot = flameShape(64, 48, 'hot');
    const neon = flameShape(64, 48, 'neon');
    const blue = (color: number) => color & 0xff;
    const red = (color: number) => color >> 16;
    for (let i = 0; i < hot.length; i++) {
      expect(neon[i].points).toEqual(hot[i].points);
      expect(blue(neon[i].color)).toBeGreaterThan(blue(hot[i].color));
      expect(red(neon[i].color)).toBeLessThan(red(hot[i].color));
    }
  });
});

describe('engineGlow', () => {
  const hull = { maxSpeed: 100 } as HullParams;
  /** Корабль носом вверх, летящий вперёд с этой скоростью. */
  const flying = (speed: number): ShipState => ({ x: 0, y: 0, rot: 0, vx: 0, vy: -speed });

  it('пять ступеней: 0, 25, 50, 75, 100 — и ничего между ними', () => {
    const steps = [0, 0.25, 0.5, 0.75, 1];
    for (const step of steps) expect(engineGlow(flying(90), step, hull)).toBeCloseTo(step, 9);
    // Любая тяга подтягивается к ближайшей ступени.
    expect(engineGlow(flying(90), 0.34, hull)).toBeCloseTo(0.25, 9);
    expect(engineGlow(flying(90), 0.4, hull)).toBeCloseTo(0.5, 9);
    expect(engineGlow(flying(90), 0.93, hull)).toBeCloseTo(1, 9);
    for (const throttle of [0.1, 0.3, 0.45, 0.6, 0.8, 0.99]) {
      expect(steps).toContainEqual(engineGlow(flying(90), throttle, hull));
    }
  });

  it('нет тяги — нет огня', () => {
    expect(engineGlow(flying(90), 0, hull)).toBe(0);
  });

  it('висит на месте или пятится — не горит, сколько бы ни жал газ', () => {
    expect(engineGlow(flying(0), 1, hull)).toBe(0);
    expect(engineGlow(flying(5), 1, hull)).toBe(0);
    expect(engineGlow(flying(-40), 1, hull)).toBe(0);
  });

  it('тяга в развороте не считается полётом: смотрим вдоль носа', () => {
    // Скорость есть, но она поперёк корпуса — корабль сносит боком.
    expect(engineGlow({ x: 0, y: 0, rot: 0, vx: 90, vy: 0 }, 1, hull)).toBe(0);
  });
});

const even = (points: number[]) => points.filter((_, i) => i % 2 === 0);
const odd = (points: number[]) => points.filter((_, i) => i % 2 === 1);
