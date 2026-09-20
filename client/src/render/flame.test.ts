import { describe, expect, it } from 'vitest';
import { flameShape } from './flame';

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

const even = (points: number[]) => points.filter((_, i) => i % 2 === 0);
const odd = (points: number[]) => points.filter((_, i) => i % 2 === 1);
