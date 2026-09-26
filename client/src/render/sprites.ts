import { Assets, Texture } from 'pixi.js';
import { lootSprite, type LootRules } from '../sim/loot';
import meta from './spriteMeta.json';

/**
 * Спрайты из листов art/space (нарезает tools/sprites.py в public/sprites). Грузятся один раз при старте,
 * до этого — и в тестах — вместо текстуры пустая: виды можно создавать и без картинок.
 */
export type SpriteName = keyof typeof meta;

export interface SpriteSize {
  w: number;
  h: number;
  /** У кораблей с нарисованным пламенем: где кончается сам корпус, px сверху. По нему центр и масштаб. */
  body?: number;
}

const textures = new Map<string, Texture>();

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
  // Кадры «…-flame» больше не грузим: с M20 факел рисуется по точкам сопел (render/exhaust.ts).
  const names = Object.keys(meta);
  // Мипмапы: корабль в 256 px на мелком зуме рисуется в 30 — без них края рябят.
  const loaded = await Assets.load(names.map((name) => ({ alias: name, src: spriteUrl(name), data: { autoGenerateMipmaps: true } })));
  for (const name of names) textures.set(name, loaded[name] as Texture);
}

/** Чей корабль, если не пилота: у пиратов, торговцев, рейнджеров и дронов свои картинки. */
export type ShipRole = 'pirate' | 'trader' | 'ranger' | 'drone' | 'convoy' | 'wing' | 'rebel' | 'corp';

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
  // Повстанцы «Тихой войны» (M20a) — шахтёры на рабочих буксирах, а не налётчики: это должно
  // читаться силуэтом, иначе кампания про мятеж выглядит как очередное логово пиратов.
  rebel: 'ships-tug',
  // Корабли корпорации (M20b) рисуются по своему корпусу — см. shipSprite. Запись здесь нужна только
  // затем, чтобы список видов оставался полным: своей общей картинки у них нет и быть не должно.
  corp: 'ships-ranger-heavy',
};

/** Спрайт корабля по корпусу и виду: у NPC свои корабли, у пилотов — по корпусу; размер — всегда по корпусу. */
export function shipSprite(hull: string, role: ShipRole | null = null): SpriteName {
  // Корпорация летает на обычных корпусах — «Игле», «Страже», «Галеоне» (M20b): охрана, курьер и транспорт
  // должны отличаться друг от друга, а не быть тремя именами одного силуэта.
  if (role === 'corp') return shipSprite(hull);
  if (role) return ROLE_SPRITES[role];
  const name = `ships-${hull}`;
  return name in meta ? (name as SpriteName) : 'ships-light';
}

/** Предметы, чья картинка на листе названа иначе: tech в loot.json — «Плазменный компонент». */
const ITEM_SPRITES: Record<string, string> = {
  tech: 'plasma',
  // Сюжетные предметы (M20a, M20b) — подмена: своего арта у них нет, взяты ближайшие по смыслу товары.
  // Записано в art/next-art-status.md; когда картинки появятся, эти строки уходят.
  powerCore: 'energy',
  armorSections: 'metal',
  shipLog: 'electronics',
  // Детали прототипа (M20b): каркас, приводы и оружейный модуль — детали меха из пачки E. У остальных
  // та же подмена по смыслу: чертёж как электроника, реактор как плазма, нейроинтерфейс как кристалл.
  blueprint: 'electronics',
  mechFrame: 'mech-frame',
  driveBlock: 'mech-drive',
  reactor: 'plasma',
  neuroLink: 'crystals',
  weaponModule: 'mech-weapon',
};

/**
 * Значки видов заданий — у каждого свой: пять из пачки C, четыре дорисованы кодом (tools/mission_icons.py).
 * Охота на камни — метеорит в прицеле, «собрать» — кирка над самородком: это разная работа.
 */
const MISSION_SPRITES: Record<string, string> = {
  kill: 'mission-kill',
  collect: 'mission-collect',
  deliver: 'mission-deliver',
  escort: 'mission-escort',
  patrol: 'mission-patrol',
  courier: 'mission-courier',
  hunt: 'mission-meteor',
  defend: 'mission-defend',
};

/** Значок вида задания; null — его не рисовали. «ground» — наземный бой мехов у ретранслятора (M21). */
export function missionSprite(kind: string): SpriteName | null {
  const name = kind === 'ground' ? 'mission-ground' : MISSION_SPRITES[kind];
  return name && name in meta ? (name as SpriteName) : null;
}

/** Иконка предмета груза; неизвестный — металл. */
export function itemSprite(item: string): SpriteName {
  const name = `resources-${ITEM_SPRITES[item] ?? item}`;
  return name in meta ? (name as SpriteName) : 'resources-metal';
}

/** Иконка того, что лежит в трюме: у снаряжения (M11) — своя пушка или модуль, у груза — ресурс. */
export function gearIcon(rules: LootRules, item: string): SpriteName {
  const gear = lootSprite(rules, item);
  return gear && hasSprite(gear) ? (gear as SpriteName) : itemSprite(item);
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

/**
 * Картинки модулей: у некоторых своя (M11), у остальных — по слоту.
 * У вспомогательных картинки по слоту нарочно нет: она была бы иконкой грузового контейнера, и её надел бы
 * каждый новый utility-модуль — а защита (M15.6) не контейнер. Свои иконки у них — в MODULE_ITEM_SPRITES:
 * с пачкой F они есть у всех четырёх защитных модулей. У нового utility-модуля иконки снова не будет,
 * пока её не нарисуют, — так и задумано; заявки лежат в art/next-art-requests.md.
 */
const MODULE_SPRITES: Record<string, string> = {
  engine: 'modules-engine',
  shield: 'modules-shield',
  radar: 'modules-scanner',
  generator: 'modules-reactor',
};

const MODULE_ITEM_SPRITES: Record<string, string> = {
  afterburnerM: 'modules-afterburner',
  afterburnerL: 'modules-afterburner',
  radarL: 'modules-military-radar',
  generatorS: 'resources-energy',
  repair: 'modules-repair',
  cooling: 'modules-cooling',
  cargoPod: 'modules-cargo',
  // Защита M15.6 (пачка F).
  thrusters: 'modules-thrusters',
  dustCloud: 'modules-dust-cloud',
  reactiveArmor: 'modules-reactive-armor',
  antiMissile: 'modules-anti-missile',
  // Флот M19 (пачка K).
  grapple: 'modules-grapple',
  deepScanner: 'modules-deep-scanner',
  cloak: 'modules-cloak',
  armorPlate: 'modules-armor-plate',
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
