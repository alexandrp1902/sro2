import { Container, Texture, TilingSprite } from 'pixi.js';
import { random } from './noise';

type Rgb = readonly [number, number, number];

interface StarLayer {
  parallax: number;
  count: number;
  radius: [number, number];
  alpha: [number, number];
}

const TILE = 512;

// Дальние слои двигаются медленнее — ощущение глубины и скорости (заодно по ним виден занос).
const FAR_STARS: StarLayer = { parallax: 0.15, count: 240, radius: [0.4, 0.9], alpha: [0.2, 0.55] };
const NEAR_STARS: StarLayer[] = [
  { parallax: 0.4, count: 60, radius: [0.7, 1.4], alpha: [0.45, 0.8] },
  { parallax: 0.8, count: 22, radius: [1.1, 2.0], alpha: [0.7, 1.0] },
];

/** Яркие звёзды с ореолом и лучами. Размер плитки не кратен другим — узор слоёв не повторяется вместе. */
const BRIGHT_STARS = { tile: 1280, parallax: 0.25, count: 4 };

const STAR_TINTS: Rgb[] = [
  [216, 230, 255],
  [255, 233, 196],
];
const BRIGHT_TINTS: Rgb[] = [
  [190, 214, 255],
  [255, 244, 214],
  [255, 206, 150],
  [255, 170, 150],
];

/**
 * Звёздное небо системы: несколько слоёв звёзд с параллаксом. Сид задаёт вид неба.
 * Туманность — отдельный слой в мире (nebulaView.ts).
 */
export class Starfield {
  readonly view = new Container();
  private readonly layers: { sprite: TilingSprite; parallax: number }[] = [];
  constructor(seed = 0) {
    const rand = random(seed);
    this.addLayer(createStarTexture(FAR_STARS, rand), FAR_STARS.parallax);
    this.addLayer(createBrightStarTexture(rand), BRIGHT_STARS.parallax);
    for (const layer of NEAR_STARS) this.addLayer(createStarTexture(layer, rand), layer.parallax);
  }

  /** Слой сдвигается на долю сдвига мира на экране (с масштабом): при любом зуме звёзды дальше станции и кораблей. */
  update(cameraX: number, cameraY: number, zoom: number, width: number, height: number): void {
    for (const { sprite, parallax } of this.layers) {
      sprite.width = width;
      sprite.height = height;
      sprite.tilePosition.set(width / 2 - cameraX * parallax * zoom, height / 2 - cameraY * parallax * zoom);
    }
  }

  /** Звёзды нарисованы для этого неба — их текстуры уходят вместе с ним. */
  destroy(): void {
    for (const { sprite } of this.layers) sprite.texture.destroy(true);
    this.view.destroy({ children: true });
  }

  private addLayer(texture: Texture, parallax: number): void {
    const sprite = new TilingSprite({ texture, width: 1, height: 1 });
    this.view.addChild(sprite);
    this.layers.push({ sprite, parallax });
  }
}

function createStarTexture(layer: StarLayer, rand: () => number): Texture {
  const { canvas, ctx } = createCanvas(TILE);
  for (let i = 0; i < layer.count; i++) {
    const radius = lerp(layer.radius, rand());
    ctx.globalAlpha = lerp(layer.alpha, rand());
    ctx.fillStyle = rgba(rand() < 0.2 ? STAR_TINTS[1] : STAR_TINTS[0], 1);
    drawWrapped(TILE, rand() * TILE, rand() * TILE, radius, (x, y) => {
      ctx.beginPath();
      ctx.arc(x, y, radius, 0, Math.PI * 2);
      ctx.fill();
    });
  }
  return Texture.from(canvas);
}

function createBrightStarTexture(rand: () => number): Texture {
  const tile = BRIGHT_STARS.tile;
  const { canvas, ctx } = createCanvas(tile);
  ctx.globalCompositeOperation = 'lighter';
  for (let i = 0; i < BRIGHT_STARS.count; i++) {
    const radius = 1 + rand() * 1.2;
    const tint = pick(BRIGHT_TINTS, rand);
    drawWrapped(tile, rand() * tile, rand() * tile, radius * 14, (x, y) => drawBrightStar(ctx, x, y, radius, tint));
  }
  return Texture.from(canvas);
}

/** Звезда с ореолом и крестом лучей, как на снимках телескопа. */
function drawBrightStar(ctx: CanvasRenderingContext2D, x: number, y: number, radius: number, tint: Rgb): void {
  const halo = radius * 9;
  const glow = ctx.createRadialGradient(x, y, 0, x, y, halo);
  glow.addColorStop(0, rgba(tint, 0.9));
  glow.addColorStop(0.1, rgba(tint, 0.4));
  glow.addColorStop(0.35, rgba(tint, 0.08));
  glow.addColorStop(1, rgba(tint, 0));
  ctx.fillStyle = glow;
  ctx.fillRect(x - halo, y - halo, halo * 2, halo * 2);

  const spike = radius * 14;
  for (const horizontal of [true, false]) {
    const ray = horizontal
      ? ctx.createLinearGradient(x - spike, y, x + spike, y)
      : ctx.createLinearGradient(x, y - spike, x, y + spike);
    ray.addColorStop(0, rgba(tint, 0));
    ray.addColorStop(0.5, rgba(tint, 0.7));
    ray.addColorStop(1, rgba(tint, 0));
    ctx.fillStyle = ray;
    if (horizontal) ctx.fillRect(x - spike, y - 0.5, spike * 2, 1);
    else ctx.fillRect(x - 0.5, y - spike, 1, spike * 2);
  }

  ctx.fillStyle = 'rgba(255, 255, 255, 0.95)';
  ctx.beginPath();
  ctx.arc(x, y, radius * 0.8, 0, Math.PI * 2);
  ctx.fill();
}

/** Рисует фигуру ещё и у противоположных краёв плитки, если она за край заходит, — на стыке плиток не будет обрезков. */
function drawWrapped(tile: number, x: number, y: number, radius: number, draw: (x: number, y: number) => void): void {
  for (const dx of [-tile, 0, tile]) {
    for (const dy of [-tile, 0, tile]) {
      const cx = x + dx;
      const cy = y + dy;
      if (cx + radius < 0 || cx - radius > tile || cy + radius < 0 || cy - radius > tile) continue;
      draw(cx, cy);
    }
  }
}

function createCanvas(size: number): { canvas: HTMLCanvasElement; ctx: CanvasRenderingContext2D } {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  return { canvas, ctx: canvas.getContext('2d')! };
}

function rgba([r, g, b]: Rgb, alpha: number): string {
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

function pick<T>(items: readonly T[], rand: () => number): T {
  return items[Math.floor(rand() * items.length)];
}

function lerp([from, to]: [number, number], t: number): number {
  return from + (to - from) * t;
}
