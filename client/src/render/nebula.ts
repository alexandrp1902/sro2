import { fbm, random, smoothstep } from './noise';

type Rgb = readonly [number, number, number];

export interface NebulaPalette {
  /** Два облака газа разного цвета. */
  clouds: readonly [Rgb, Rgb];
  /** Горячее ядро там, где облака гуще всего. */
  core: Rgb;
}

/** Палитры в духе «Космических рейнджеров»; сид неба выбирает одну из них. */
export const PALETTES: readonly NebulaPalette[] = [
  // фиолетово-синяя с розовым ядром
  { clouds: [[110, 42, 170], [26, 72, 170]], core: [255, 120, 170] },
  // бирюзовая с золотым ядром
  { clouds: [[24, 140, 160], [50, 50, 170]], core: [255, 200, 120] },
  // багровая с оранжевым ядром
  { clouds: [[170, 36, 80], [80, 32, 150]], core: [255, 170, 90] },
];

/** Насколько (в долях плитки) завихрения сдвигают облака — отсюда рваные «языки» газа. */
const WARP = 0.3;
/** Плотность самого густого газа: сквозь туманность должны просвечивать звёзды, а корабли — читаться. */
const OPACITY = 0.8;

/**
 * Туманность на бесшовной квадратной плитке size×size, RGBA. Плитка растянута на всю систему,
 * поэтому частоты шума высокие: облака размером с пару экранов, а не со всю карту.
 * Облака газа закручены сдвигом по шуму, между ними пустоты, их рвут тёмные пылевые пятна,
 * внутри светятся тонкие волокна и горячее ядро.
 * Где газа нет, пиксель прозрачный: сквозь туманность видны звёзды, густой газ их закрывает.
 */
export function paintNebula(size: number, seed: number): Uint8ClampedArray<ArrayBuffer> {
  const palette = PALETTES[seed % PALETTES.length];
  const [c0, c1] = palette.clouds;
  const fiber = c0.map((c) => (c + 255) / 2);
  const rand = random(seed);
  const [warpX, warpY, region, cloudA, cloudB, dustSalt, fibers] = Array.from({ length: 7 }, () =>
    Math.floor(rand() * 0x7fffffff),
  );
  const pixels = new Uint8ClampedArray(size * size * 4);
  const rgb = [0, 0, 0];
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const u = x / size;
      const v = y / size;
      const pu = u + WARP * fbm(u, v, 3, 3, warpX);
      const pv = v + WARP * fbm(u, v, 3, 3, warpY);
      const presence = smoothstep(-0.5, 0.25, fbm(u, v, 2, 2, region));
      const dust = 1 - 0.8 * smoothstep(0.05, 0.4, fbm(pu, pv, 7, 3, dustSalt));
      const a = smoothstep(-0.1, 0.5, fbm(pu, pv, 6, 5, cloudA)) * presence * dust;
      const b = smoothstep(-0.05, 0.55, fbm(pu, pv, 4, 5, cloudB)) * (0.25 + 0.75 * presence) * (0.35 + 0.65 * dust);
      const thread = (1 - smoothstep(0, 0.09, Math.abs(fbm(pu, pv, 9, 3, fibers)))) * a;
      const core = smoothstep(0.35, 1.1, a * (0.6 + b));
      const dither = rand() - 0.5; // без него на тёмных переходах видны ступеньки

      let peak = 0;
      for (let c = 0; c < 3; c++) {
        rgb[c] = c0[c] * a + c1[c] * b * 0.8 + fiber[c] * thread * 0.35 + palette.core[c] * core * 0.8 + dither;
        peak = Math.max(peak, rgb[c]);
      }
      if (peak <= 0) continue; // газа нет — пиксель остаётся прозрачным
      // Плотность газа — по самому яркому каналу; цвет делим на неё, чтобы поверх чёрного вышел тот же цвет.
      const alpha = Math.min(1, peak / 255) * OPACITY;
      const i = (y * size + x) * 4;
      for (let c = 0; c < 3; c++) pixels[i + c] = rgb[c] / alpha;
      pixels[i + 3] = alpha * 255;
    }
  }
  return pixels;
}
