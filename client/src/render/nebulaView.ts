import { Container, Texture, TilingSprite } from 'pixi.js';
import { paintNebula } from './nebula';

type Pixels = Uint8ClampedArray<ArrayBuffer>;

/** Плитка шума: чем крупнее, тем чётче туманность, но тем дольше считается. */
const TILE = 1024;
/**
 * Размер туманности в мире. Чуть меньше системы (8000): облака остаются подробными,
 * а повтор узора виден только у дальних краёв карты — и то без стыка.
 */
const PERIOD = 6000;
/** Какая точка плитки (в долях) приходится на центр системы: у станции должен быть газ, а не пустота. */
const PHASE = [0.46, 0.5];
/** Полотно с запасом за границей мира: у края системы фон не обрывается. */
const COVER = 24000;
const FADE_MS = 1500;

/**
 * Туманность системы — часть мира, а не экрана: она стоит на месте в космосе, мимо неё летят корабли,
 * камера двигает и масштабирует её вместе со станцией и буями.
 * Считается в фоновом потоке (сотни миллисекунд) и проявляется плавно, поэтому в начале боя её ещё нет.
 */
export class Nebula {
  readonly view = new Container();
  private sprite: TilingSprite | null = null;
  private readyAt = 0;

  constructor(seed: number) {
    load(seed, (pixels) => {
      const sprite = new TilingSprite({ texture: createTexture(pixels), width: COVER, height: COVER });
      sprite.position.set(-COVER / 2, -COVER / 2);
      sprite.tileScale.set(PERIOD / TILE);
      sprite.tilePosition.set(COVER / 2 - PHASE[0] * PERIOD, COVER / 2 - PHASE[1] * PERIOD);
      sprite.alpha = 0;
      this.view.addChild(sprite);
      this.sprite = sprite;
      this.readyAt = performance.now();
    });
  }

  update(now: number): void {
    if (!this.sprite || this.sprite.alpha >= 1) return;
    this.sprite.alpha = Math.min(1, (now - this.readyAt) / FADE_MS);
  }
}

/** Туманность считается в фоновом потоке; если Worker недоступен — прямо здесь. */
function load(seed: number, onReady: (pixels: Pixels) => void): void {
  const paintHere = () => onReady(paintNebula(TILE, seed));
  let worker: Worker;
  try {
    worker = new Worker(new URL('./nebula.worker.ts', import.meta.url), { type: 'module' });
  } catch {
    paintHere();
    return;
  }
  worker.onmessage = (event: MessageEvent<Pixels>) => {
    worker.terminate();
    onReady(event.data);
  };
  worker.onerror = () => {
    worker.terminate();
    paintHere();
  };
  worker.postMessage({ size: TILE, seed });
}

function createTexture(pixels: Pixels): Texture {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = TILE;
  canvas.getContext('2d')!.putImageData(new ImageData(pixels, TILE), 0, 0);
  return Texture.from(canvas);
}
