import { describe, expect, it } from 'vitest';
import { paintNebula } from './nebula';
import { fbm, periodicNoise, random } from './noise';

describe('periodicNoise', () => {
  it('repeats with its period on both axes, so the tile has no seam', () => {
    for (const [x, y] of [
      [0.3, 0.7],
      [2.5, 1.25],
      [-0.4, 3.9],
    ]) {
      const n = periodicNoise(x, y, 4, 7);
      expect(periodicNoise(x + 4, y, 4, 7)).toBeCloseTo(n, 10);
      expect(periodicNoise(x, y - 4, 4, 7)).toBeCloseTo(n, 10);
    }
  });

  it('stays in [-1, 1] and is zero on lattice points', () => {
    const rand = random(1);
    for (let i = 0; i < 2000; i++) {
      const n = periodicNoise(rand() * 16, rand() * 16, 16, 3);
      expect(Math.abs(n)).toBeLessThanOrEqual(1);
    }
    expect(periodicNoise(2, 3, 8, 3)).toBe(0);
  });
});

describe('fbm', () => {
  it('tiles with period 1 whatever the octaves', () => {
    expect(fbm(1.2, 0.35, 3, 5, 11)).toBeCloseTo(fbm(0.2, 1.35, 3, 5, 11), 10);
  });
});

describe('paintNebula', () => {
  it('is the same for the same seed and different for another', () => {
    expect(paintNebula(16, 1)).toEqual(paintNebula(16, 1));
    expect(paintNebula(16, 1)).not.toEqual(paintNebula(16, 2));
  });

  it('leaves empty space transparent so the stars behind show through', () => {
    const pixels = paintNebula(64, 0);
    const alphas = [];
    for (let i = 3; i < pixels.length; i += 4) alphas.push(pixels[i]);
    expect(Math.min(...alphas)).toBe(0);
    expect(Math.max(...alphas)).toBeGreaterThan(0);
  });
});
