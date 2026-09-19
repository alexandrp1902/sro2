import { Assets, Texture } from 'pixi.js';
import meta from './spriteMeta.json';

/**
 * Спрайты из листов art/space (нарезает tools/sprites.py в public/sprites). Грузятся один раз при старте,
 * до этого — и в тестах — вместо текстуры пустая: виды можно создавать и без картинок.
 */
export type SpriteName = keyof typeof meta;

export interface SpriteSize {
  w: number;
  h: number;
  /** У кораблей: где кончается корпус и начинается пламя, px сверху. */
  body?: number;
}

const textures = new Map<string, Texture>();

/** Корабли режутся на корпус и пламя: пламя — отдельная текстура «…-flame». */
const FLAMES = (Object.keys(meta) as SpriteName[]).filter((name) => 'body' in meta[name]).map((name) => `${name}-flame`);

/** Адрес картинки — и для DOM (иконки в доке): путь относительный, как и base сборки. */
export function spriteUrl(name: string): string {
  return `sprites/${name}.webp`;
}

/** Есть ли такая картинка на листах: имена звёзд и планет приходят с сервера. */
export function hasSprite(name: string): name is SpriteName {
  return name in meta;
}

export function spriteSize(name: SpriteName): SpriteSize {
  return meta[name];
}

export function texture(name: string): Texture {
  return textures.get(name) ?? Texture.EMPTY;
}

export async function loadSprites(): Promise<void> {
  const names = [...Object.keys(meta), ...FLAMES];
  // Мипмапы: корабль в 256 px на мелком зуме рисуется в 30 — без них края рябят.
  const loaded = await Assets.load(names.map((name) => ({ alias: name, src: spriteUrl(name), data: { autoGenerateMipmaps: true } })));
  for (const name of names) textures.set(name, loaded[name] as Texture);
}

/** Чей корабль, если не пилота: у пиратов, торговцев, рейнджеров и дронов свои картинки. */
export type ShipRole = 'pirate' | 'trader' | 'ranger' | 'drone';

/** Картинки NPC: грузовик у торговца, фрегат у рейнджера, разведчик у дрона. Пламени у них нет. */
const ROLE_SPRITES: Record<ShipRole, SpriteName> = {
  pirate: 'ships-pirate',
  trader: 'ships-freighter',
  ranger: 'ships-frigate',
  drone: 'ships-scout',
};

/** Спрайт корабля по корпусу и виду: у NPC свои корабли, у пилотов — по корпусу; размер — всегда по корпусу. */
export function shipSprite(hull: string, role: ShipRole | null = null): SpriteName {
  if (role) return ROLE_SPRITES[role];
  if (hull === 'medium') return 'ships-medium';
  if (hull === 'heavy') return 'ships-heavy';
  return 'ships-light';
}

/** Предметы, чья картинка на листе названа иначе: tech в loot.json — «Плазменный компонент». */
const ITEM_SPRITES: Record<string, string> = { tech: 'plasma' };

/** Иконка предмета груза; неизвестный — металл. */
export function itemSprite(item: string): SpriteName {
  const name = `resources-${ITEM_SPRITES[item] ?? item}`;
  return name in meta ? (name as SpriteName) : 'resources-metal';
}

/** Пушки, чья картинка названа иначе или общая с родственной. */
const WEAPON_SPRITES: Record<string, string> = { missiles: 'rockets', heavyLaser: 'laser', cannon: 'pulse' };

/** Иконка пушки на витрине; неизвестная — null. */
export function weaponSprite(weapon: string): SpriteName | null {
  const name = `weapons-${WEAPON_SPRITES[weapon] ?? weapon}`;
  return name in meta ? (name as SpriteName) : null;
}

/** Картинки модулей по слоту: бак — грузовой модуль, радар — сканер, генератор — энергоблок. */
const MODULE_SPRITES: Record<string, string> = {
  engine: 'modules-engine',
  shield: 'modules-shield',
  radar: 'modules-scanner',
  tank: 'modules-cargo',
  generator: 'resources-energy',
};

/** Иконка модуля на витрине по его слоту; неизвестный — null. */
export function moduleSprite(slot: string): SpriteName | null {
  const name = MODULE_SPRITES[slot];
  return name && name in meta ? (name as SpriteName) : null;
}
