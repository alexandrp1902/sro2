import { Sprite, Texture } from 'pixi.js';

let glow: Texture | null = null;

/** Shared soft blue engine light, pointing aft (+Y); no per-frame texture allocation. */
export function neonExhaust(width: number, length: number): Sprite {
  if (!glow) {
    const canvas = document.createElement('canvas');
    canvas.width = 96;
    canvas.height = 192;
    const ctx = canvas.getContext('2d')!;
    ctx.scale(1, 2);
    const gradient = ctx.createRadialGradient(48, 32, 0, 48, 44, 47);
    gradient.addColorStop(0, '#efffff');
    gradient.addColorStop(0.12, '#b8f5ff');
    gradient.addColorStop(0.3, '#38ccffdd');
    gradient.addColorStop(0.55, '#139aff77');
    gradient.addColorStop(0.8, '#126aff22');
    gradient.addColorStop(1, '#126aff00');
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, 96, 96);
    glow = Texture.from(canvas);
  }
  const sprite = new Sprite(glow);
  sprite.anchor.set(0.5, 0.12);
  sprite.width = width;
  sprite.height = length;
  sprite.blendMode = 'add';
  return sprite;
}
