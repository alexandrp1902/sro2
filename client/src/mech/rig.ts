import { Assets, Container, Sprite, Texture } from 'pixi.js';
import meta from './rigMeta.json';
import { DIRECTION_NAMES, type MechPart } from './rules';

/**
 * Мех из слоёв (порт art/mechs/production/rig-renderer.js): шасси, корпус и две руки, у каждого слоя на каждое из 8
 * направлений свой кадр, сдвиг, масштаб и порядок. Числа — из medium-rig.json, нарезку делает tools/mechs.py.
 * Разбитая рука просто прячется — нарисованных обрубков пока нет (P3).
 */

type Layer = 'chassis' | 'body' | 'left' | 'right';
const LAYERS: Layer[] = ['chassis', 'body', 'left', 'right'];

/** Тайлы поля (стенд-ин до P4). */
export const TILE_NAMES = ['ground-1', 'ground-2', 'rough', 'crate', 'wall', 'building'] as const;
export type TileName = (typeof TILE_NAMES)[number];

const textures = new Map<string, Texture>();

export function mechUrl(name: string): string {
  return `mechs/${name}.webp`;
}

export function mechTexture(name: string): Texture {
  return textures.get(name) ?? Texture.EMPTY;
}

let loading: Promise<void> | null = null;

/** Все слои и тайлы — один раз, при первом бое: в космосе они не нужны. */
export function loadMechArt(): Promise<void> {
  loading ??= (async () => {
    const names = [
      ...LAYERS.flatMap((layer) => DIRECTION_NAMES.map((d) => `${layer}-${d}`)),
      ...TILE_NAMES.map((t) => `tile-${t}`),
    ];
    const loaded = await Assets.load(names.map((name) => ({ alias: `mech:${name}`, src: mechUrl(name), data: { autoGenerateMipmaps: true } })));
    for (const name of names) textures.set(name, loaded[`mech:${name}`] as Texture);
  })().catch((e) => {
    // Нет картинок — бой всё равно играется: мехи рисуются пустыми, а поле — цветом клеток.
    console.warn('mech art failed to load', e);
    loading = null;
  });
  return loading;
}

type Frame = { layers: Record<string, { x: number; y: number; scale: number; z: number }>; muzzle: number[] };
const FRAMES = meta.frames as Record<string, Frame>;
const CELL = meta.cellSize;
const [PIVOT_X, PIVOT_Y] = meta.groundPivot;

export class MechRig {
  readonly view = new Container();
  private readonly sprites = new Map<Layer, Sprite>();
  private dir = -1;
  private hidden = new Set<Layer>();

  /**
   * @param size сторона квадрата рига на экране, px: в нём 512 единиц рига
   * @param tint оттенок врага — стенд-ин до своих спрайтов P2
   */
  constructor(size: number, tint = 0xffffff) {
    const k = size / CELL;
    this.view.scale.set(k);
    // Опорная точка рига — где мех стоит на земле; её и ставим на клетку.
    this.view.pivot.set(PIVOT_X, PIVOT_Y);
    this.view.sortableChildren = true;
    for (const layer of LAYERS) {
      const sprite = new Sprite(Texture.EMPTY);
      sprite.tint = tint;
      this.sprites.set(layer, sprite);
      this.view.addChild(sprite);
    }
  }

  /** Направление 0..7, и какие руки разбиты. */
  set(dir: number, broken: ReadonlySet<MechPart>): void {
    const hidden = new Set<Layer>(LAYERS.filter((l) => (l === 'left' || l === 'right') && broken.has(l)));
    const same = hidden.size === this.hidden.size && [...hidden].every((l) => this.hidden.has(l));
    if (dir === this.dir && same) return;
    this.dir = dir;
    this.hidden = hidden;
    const name = DIRECTION_NAMES[dir];
    const frame = FRAMES[name];
    for (const [layer, sprite] of this.sprites) {
      const t = frame.layers[layer];
      if (!t || hidden.has(layer)) {
        sprite.visible = false;
        continue;
      }
      const edge = CELL * t.scale;
      sprite.visible = true;
      sprite.texture = mechTexture(`${layer}-${name}`);
      sprite.width = edge;
      sprite.height = edge;
      sprite.position.set(CELL / 2 - edge / 2 + t.x, CELL / 2 - edge / 2 + t.y);
      sprite.zIndex = t.z;
    }
  }

  /** Дуло в координатах рига относительно опорной точки — туда рисуется трассер. */
  muzzle(dir: number): [number, number] {
    const [x, y] = FRAMES[DIRECTION_NAMES[dir]].muzzle;
    return [(x - PIVOT_X) * this.view.scale.x, (y - PIVOT_Y) * this.view.scale.y];
  }
}
