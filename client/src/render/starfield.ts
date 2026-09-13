import { Container, Texture, TilingSprite } from 'pixi.js';

interface StarLayer {
  parallax: number;
  count: number;
  radius: [number, number];
  alpha: [number, number];
}

const TILE = 512;

// Дальние слои двигаются медленнее — ощущение глубины и скорости.
const LAYERS: StarLayer[] = [
  { parallax: 0.15, count: 140, radius: [0.5, 1.0], alpha: [0.25, 0.55] },
  { parallax: 0.4, count: 60, radius: [0.7, 1.4], alpha: [0.45, 0.8] },
  { parallax: 0.8, count: 22, radius: [1.1, 2.0], alpha: [0.7, 1.0] },
];

export class Starfield {
  readonly view = new Container();
  private readonly layers: { sprite: TilingSprite; parallax: number }[] = [];

  constructor() {
    for (const layer of LAYERS) {
      const sprite = new TilingSprite({ texture: createStarTexture(layer), width: 1, height: 1 });
      this.view.addChild(sprite);
      this.layers.push({ sprite, parallax: layer.parallax });
    }
  }

  update(cameraX: number, cameraY: number, width: number, height: number): void {
    for (const { sprite, parallax } of this.layers) {
      sprite.width = width;
      sprite.height = height;
      sprite.tilePosition.set(-cameraX * parallax, -cameraY * parallax);
    }
  }
}

function createStarTexture(layer: StarLayer): Texture {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = TILE;
  const ctx = canvas.getContext('2d')!;
  for (let i = 0; i < layer.count; i++) {
    ctx.globalAlpha = lerp(layer.alpha, Math.random());
    ctx.fillStyle = Math.random() < 0.2 ? '#ffe9c4' : '#d8e6ff';
    ctx.beginPath();
    ctx.arc(Math.random() * TILE, Math.random() * TILE, lerp(layer.radius, Math.random()), 0, Math.PI * 2);
    ctx.fill();
  }
  return Texture.from(canvas);
}

function lerp([from, to]: [number, number], t: number): number {
  return from + (to - from) * t;
}
