/** Детерминированный генератор чисел в [0, 1) (mulberry32): один и тот же сид даёт одно и то же небо. */
export function random(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const GRADIENTS = Array.from({ length: 8 }, (_, i) => [Math.cos((i * Math.PI) / 4), Math.sin((i * Math.PI) / 4)]);
/** Градиентный шум с единичными градиентами укладывается в ±√½ — растягиваем до ±1. */
const NOISE_SCALE = Math.SQRT2;

/**
 * Градиентный шум, повторяющийся с периодом period (целое число ячеек) по обеим осям:
 * текстура из него стыкуется сама с собой без шва. Значения в [-1, 1].
 */
export function periodicNoise(x: number, y: number, period: number, salt: number): number {
  const fx = Math.floor(x);
  const fy = Math.floor(y);
  const tx = x - fx;
  const ty = y - fy;
  const x0 = mod(fx, period);
  const y0 = mod(fy, period);
  const x1 = (x0 + 1) % period;
  const y1 = (y0 + 1) % period;
  const a = dot(hash(x0, y0, salt), tx, ty);
  const b = dot(hash(x1, y0, salt), tx - 1, ty);
  const c = dot(hash(x0, y1, salt), tx, ty - 1);
  const d = dot(hash(x1, y1, salt), tx - 1, ty - 1);
  const u = fade(tx);
  const v = fade(ty);
  return (a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v) * NOISE_SCALE;
}

/**
 * Фрактальный шум на плитке [0, 1)²: base — сколько ячеек укладывается в плитку у крупной октавы,
 * каждая следующая октава вдвое мельче и вдвое слабее. Периодичен с периодом 1, значения в [-1, 1].
 */
export function fbm(u: number, v: number, base: number, octaves: number, salt: number): number {
  let sum = 0;
  let norm = 0;
  let amp = 1;
  let freq = base;
  for (let o = 0; o < octaves; o++) {
    sum += amp * periodicNoise(u * freq, v * freq, freq, salt + o * 1013);
    norm += amp;
    amp *= 0.5;
    freq *= 2;
  }
  return sum / norm;
}

export function smoothstep(from: number, to: number, x: number): number {
  const t = Math.min(1, Math.max(0, (x - from) / (to - from)));
  return t * t * (3 - 2 * t);
}

function hash(x: number, y: number, salt: number): number {
  let h = Math.imul(x, 0x27d4eb2d) ^ Math.imul(y, 0x165667b1) ^ Math.imul(salt, 0x9e3779b1);
  h = Math.imul(h ^ (h >>> 15), 0x85ebca6b);
  h = Math.imul(h ^ (h >>> 13), 0xc2b2ae35);
  return (h ^ (h >>> 16)) >>> 0;
}

function dot(h: number, x: number, y: number): number {
  const g = GRADIENTS[h & 7];
  return g[0] * x + g[1] * y;
}

function fade(t: number): number {
  return t * t * t * (t * (t * 6 - 15) + 10);
}

function mod(a: number, n: number): number {
  return ((a % n) + n) % n;
}
