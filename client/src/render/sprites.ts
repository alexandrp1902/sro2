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
export type ShipRole = 'pirate' | 'trader' | 'ranger' | 'drone' | 'convoy' | 'wing';

/**
 * Картинки NPC (M11): свои силуэты, чтобы их не путали с кораблями пилотов — те летают на scout, frigate
 * и прочих корпусах. Пламени у NPC нет.
 */
const ROLE_SPRITES: Record<ShipRole, SpriteName> = {
  pirate: 'ships-pirate-raider',
  trader: 'ships-trader-hauler',
  ranger: 'ships-ranger',
  drone: 'ships-drone',
  // Корабли заданий M14: конвой и звено отличаются от рядового трафика уже силуэтом.
  convoy: 'ships-trader-convoy',
  wing: 'ships-ranger-heavy',
};

/** Спрайт корабля по корпусу и виду: у NPC свои корабли, у пилотов — по корпусу; размер — всегда по корпусу. */
export function shipSprite(hull: string, role: ShipRole | null = null): SpriteName {
  if (role) return ROLE_SPRITES[role];
  const name = `ships-${hull}`;
  return name in meta ? (name as SpriteName) : 'ships-light';
}

/**
 * Пламя двигателей для корпусов, которым его не нарисовали (M11): берём чужое по размеру корабля.
 * Временная замена — когда придут свои flame-спрайты, строка уходит (см. art/next-art-status.md).
 */
const FLAME_STANDINS: Record<string, SpriteName> = {
  'ships-scout': 'ships-light',
  'ships-interceptor': 'ships-light',
  'ships-industrial': 'ships-medium',
  'ships-frigate': 'ships-medium',
  'ships-freighter': 'ships-heavy',
  'ships-cruiser': 'ships-heavy',
};

/** Имя текстуры пламени для корпуса; null — пламени у этого корабля нет (NPC). */
export function flameSprite(sprite: SpriteName): string | null {
  if ('body' in meta[sprite]) return `${sprite}-flame`;
  const standin = FLAME_STANDINS[sprite];
  return standin ? `${standin}-flame` : null;
}

/** Предметы, чья картинка на листе названа иначе: tech в loot.json — «Плазменный компонент». */
const ITEM_SPRITES: Record<string, string> = { tech: 'plasma' };

/** Иконка предмета груза; неизвестный — металл. */
export function itemSprite(item: string): SpriteName {
  const name = `resources-${ITEM_SPRITES[item] ?? item}`;
  return name in meta ? (name as SpriteName) : 'resources-metal';
}

/** Пушки, чья картинка названа иначе. */
const WEAPON_SPRITES: Record<string, string> = {
  missiles: 'rockets',
  heavyLaser: 'heavy-laser',
  pointDefense: 'point-defense',
};

/** Иконка пушки на витрине; тир на картинку не влияет — Mk2 рисуется значком. */
export function weaponSprite(weapon: string): SpriteName | null {
  const id = baseId(weapon);
  const name = `weapons-${WEAPON_SPRITES[id] ?? id}`;
  return name in meta ? (name as SpriteName) : null;
}

/** Картинки модулей: у некоторых своя (M11), у остальных — по слоту. */
const MODULE_SPRITES: Record<string, string> = {
  engine: 'modules-engine',
  shield: 'modules-shield',
  radar: 'modules-scanner',
  tank: 'modules-fuel-tank',
  generator: 'modules-reactor',
  utility: 'modules-cargo',
};

const MODULE_ITEM_SPRITES: Record<string, string> = {
  afterburnerM: 'modules-afterburner',
  afterburnerL: 'modules-afterburner',
  radarL: 'modules-military-radar',
  generatorS: 'resources-energy',
  repair: 'modules-repair',
  cooling: 'modules-cooling',
  cargoPod: 'modules-cargo',
};

/** Иконка модуля на витрине: своя по id, иначе по слоту; неизвестный — null. */
export function moduleSprite(slot: string, id?: string): SpriteName | null {
  const name = (id ? MODULE_ITEM_SPRITES[baseId(id)] : undefined) ?? MODULE_SPRITES[slot];
  return name && name in meta ? (name as SpriteName) : null;
}

/** Базовый id без тира: «ion_mk2» → «ion» (M11). Иконка у всех тиров одна. */
function baseId(id: string): string {
  const m = /^(.+)_mk[2-3]$/.exec(id);
  return m ? m[1] : id;
}
