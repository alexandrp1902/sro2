// Оснащение корабля (GDD §11–13, §18–20): оружейные слоты корпуса, модули и энергия генератора.
// Зеркало server/Sro.Sim/Fitting.cs; совпадение проверяет shared/test-vectors/fitting.json.
// Предсказание движения летит на корпусе с модулями — иначе разошлось бы с сервером.

import defaults from '../../../shared/modules.json';
import { DAMAGE_ENERGY, DAMAGE_KINETIC, type WeaponConfig, type WeaponParams } from './combat';
import type { HullParams } from './movement';

export type EquipClass = 'S' | 'M' | 'L';
export type ModuleSlot = 'engine' | 'shield' | 'radar' | 'generator';
/** Вспомогательный модуль (M11): ремонт, охлаждение, грузовой расширитель — в слоты u0…u2. */
export const UTILITY = 'utility';
/** w0…w5 — оружейные слоты, u0…u2 — вспомогательные, остальное — модули. */
export type Slot = `w${number}` | `u${number}` | ModuleSlot;

/** Больше вспомогательных слотов у корпуса не бывает. */
export const MAX_UTILITY_SLOTS = 3;
/** Охлаждение не укорачивает перезарядку больше чем наполовину. */
export const MAX_COOLING = 0.5;
/** Больше этого модули к уклонению не добавляют, % (M15.6). Зеркало Fitting.MaxEvasionBonus. */
export const MAX_EVASION_BONUS = 12;
/** Больше этого один вид урона не блокируется, % (M15.6). Зеркало Fitting.MaxBlock. */
export const MAX_BLOCK = 40;

/** Поля, которых нет в файле, — по умолчанию, как на сервере: множители 1, остальное 0. */
export interface ModuleParams {
  name: string;
  slot: ModuleSlot | typeof UTILITY;
  class: EquipClass;
  /** Сколько энергии генератора забирает. */
  power?: number;
  /** Двигатель: множители скорости и разгона корпуса. */
  speed?: number;
  accel?: number;
  /** Щит: ёмкость и восстановление в секунду. */
  shield?: number;
  shieldRegen?: number;
  /** Радар: дальность. */
  radar?: number;
  /** Генератор: сколько энергии даёт. */
  output?: number;
  /** Вспомогательный: ремонт корпуса вне боя, единиц в секунду. */
  repair?: number;
  /** Вспомогательный: доля, на которую короче перезарядка пушек. */
  cooling?: number;
  /** Вспомогательный: прибавка к трюму. */
  cargo?: number;
  /** Вспомогательный (M15.6): прибавка к уклонению корпуса, %. */
  evasion?: number;
  /** Вспомогательный (M15.6): шанс отбить кинетическое и энергетическое попадание, %. */
  blockKinetic?: number;
  blockEnergy?: number;
  /** Вспомогательный (M15.6): противоракетный комплекс — сбивает ракеты, оружейного слота не занимая. */
  intercept?: InterceptParams | null;
  /** Тир Mk1–Mk3 (M11). */
  tier?: number;
}

/** Противоракетный комплекс и зенитка: радиус, шанс, своя перезарядка и урон по ракете. */
export interface InterceptParams {
  range: number;
  chance: number;
  cooldown?: number;
  damage?: number;
}

export type ModuleConfig = Record<string, ModuleParams>;

/** Что стоит на корабле: пушка в каждом оружейном слоте (null — пусто) и по модулю каждого вида. */
export interface ShipFit {
  weapons: (string | null)[];
  engine?: string | null;
  shield?: string | null;
  radar?: string | null;
  generator?: string | null;
  /** Вспомогательные модули по слотам u0…u2 (M11); нет — старый профиль без них. */
  utility?: (string | null)[] | null;
}

export const MODULE_SLOTS: ModuleSlot[] = ['engine', 'shield', 'radar', 'generator'];
/** Без этих модулей корабль не летает: заменить можно, снять нельзя. */
export const REQUIRED_SLOTS: ModuleSlot[] = ['engine', 'radar', 'generator'];

export const SLOT_NAMES: Record<ModuleSlot | typeof UTILITY, string> = {
  utility: 'Вспомогательный',
  engine: 'Двигатель',
  shield: 'Щит',
  radar: 'Радар',
  generator: 'Генератор',
};

/** Почему предмет не встаёт в слот; те же коды приходят с сервера в notice (noPower, badClass, badSlot). */
export type FitProblem = 'slot' | 'class' | 'power' | 'required';

/** Модули кораблей. Встроенные значения из shared/modules.json заменяются присланными сервером. */
export class Modules {
  config: ModuleConfig = defaults as unknown as ModuleConfig;
  /** Сервер без modules.json: модулей нет, щит, радар и бак даёт корпус. */
  enabled = true;

  set(config: ModuleConfig | null | undefined): void {
    this.enabled = !!config && Object.keys(config).length > 0;
    if (config && this.enabled) this.config = config;
  }

  get(id: string | null | undefined): ModuleParams | null {
    return id ? (this.config[id] ?? null) : null;
  }

  ids(): string[] {
    return Object.keys(this.config);
  }

  /** Каталог для расчётов; null — модулей нет. */
  get catalog(): ModuleConfig | null {
    return this.enabled ? this.config : null;
  }
}

export function classRank(c: string | null | undefined): number {
  return c === 'S' ? 1 : c === 'M' ? 2 : c === 'L' ? 3 : 0;
}

/** Предмет класса item встаёт в место класса slot. Класс не указан (старый баланс) — S у предмета, L у места. */
export function classFits(item: string | undefined, slot: string | undefined): boolean {
  return classRank(item ?? 'S') <= classRank(slot ?? 'L');
}

export function weaponSlot(index: number): Slot {
  return `w${index}`;
}

export function utilitySlot(index: number): Slot {
  return `u${index}`;
}

/** Номер оружейного слота; null — это модуль. */
export function weaponIndex(slot: string): number | null {
  const m = /^w(\d)$/.exec(slot);
  return m && Number(m[1]) < 6 ? Number(m[1]) : null;
}

/** Номер вспомогательного слота; null — это не он. */
export function utilityIndex(slot: string): number | null {
  const m = /^u(\d)$/.exec(slot);
  return m && Number(m[1]) < MAX_UTILITY_SLOTS ? Number(m[1]) : null;
}

/** Оружейные слоты корпуса и их классы; у старого баланса — один слот класса корпуса. */
export function hullSlots(hull: HullParams): string[] {
  return hull.weaponSlots ?? [hull.class ?? 'L'];
}

export function fitGet(fit: ShipFit, slot: string): string | null {
  const i = weaponIndex(slot);
  if (i !== null) return fit.weapons[i] ?? null;
  const u = utilityIndex(slot);
  if (u !== null) return fit.utility?.[u] ?? null;
  return fit[slot as ModuleSlot] ?? null;
}

export function fitWith(fit: ShipFit, slot: string, id: string | null): ShipFit {
  const i = weaponIndex(slot);
  if (i !== null) {
    const weapons = [...fit.weapons];
    while (weapons.length <= i) weapons.push(null);
    weapons[i] = id;
    return { ...fit, weapons };
  }
  const u = utilityIndex(slot);
  if (u !== null) {
    const utility = [...(fit.utility ?? [])];
    while (utility.length <= u) utility.push(null);
    utility[u] = id;
    return { ...fit, utility };
  }
  return { ...fit, [slot]: id };
}

/** Стоящие вспомогательные модули. */
export function utilities(fit: ShipFit, modules: ModuleConfig | null): ModuleParams[] {
  if (!modules) return [];
  return (fit.utility ?? [])
    .map((id) => (id ? modules[id] : null))
    .filter((m): m is ModuleParams => !!m && m.slot === UTILITY);
}

/** Ремонт корпуса в секунду от ремонтных блоков (M11). */
export function fitRepair(fit: ShipFit, modules: ModuleConfig | null): number {
  return utilities(fit, modules).reduce((sum, m) => sum + (m.repair ?? 0), 0);
}

/** Множитель перезарядки от охлаждения: 1 — без него. */
export function fitCooldown(fit: ShipFit, modules: ModuleConfig | null): number {
  return 1 - Math.min(MAX_COOLING, utilities(fit, modules).reduce((sum, m) => sum + (m.cooling ?? 0), 0));
}

/** Прибавка к уклонению от маневровых дюз, % (M15.6); не выше MAX_EVASION_BONUS. */
export function fitEvasion(fit: ShipFit, modules: ModuleConfig | null): number {
  return Math.min(MAX_EVASION_BONUS, utilities(fit, modules).reduce((sum, m) => sum + (m.evasion ?? 0), 0));
}

/**
 * Шанс отбить попадание этого вида урона, % (M15.6); не выше MAX_BLOCK.
 * У ракеты — 0: её не блокируют, её сбивают (fitGuard).
 */
export function fitBlock(fit: ShipFit, modules: ModuleConfig | null, type: string): number {
  const field = type === DAMAGE_KINETIC ? 'blockKinetic' : type === DAMAGE_ENERGY ? 'blockEnergy' : null;
  if (!field) return 0;
  return Math.min(MAX_BLOCK, utilities(fit, modules).reduce((sum, m) => sum + (m[field] ?? 0), 0));
}

/** Лучший противоракетный комплекс на корабле; null — его нет. Работает только один. */
export function fitGuard(fit: ShipFit, modules: ModuleConfig | null): InterceptParams | null {
  let best: InterceptParams | null = null;
  for (const m of utilities(fit, modules)) {
    if (m.intercept && (!best || m.intercept.chance > best.chance)) best = m.intercept;
  }
  return best;
}

/** Сколько вспомогательных слотов у корпуса. */
export function hullUtilitySlots(hull: HullParams): number {
  return Math.min(MAX_UTILITY_SLOTS, hull.utilitySlots ?? 0);
}

function module(modules: ModuleConfig, id: string | null | undefined, slot: ModuleSlot | typeof UTILITY): ModuleParams | null {
  const m = id ? modules[id] : undefined;
  return m && m.slot === slot ? m : null;
}

/**
 * Корпус с учётом модулей: двигатель умножает скорость, разгон и торможение; щит и радар — из модулей,
 * маневровые дюзы прибавляют уклонение (M15.6).
 * Без каталога модулей (старый сервер) или без оснащения — корпус как есть.
 */
export function effectiveHull(hull: HullParams, fit: ShipFit | null, modules: ModuleConfig | null): HullParams {
  if (!modules || !fit) return hull;
  const engine = module(modules, fit.engine, 'engine');
  const shield = module(modules, fit.shield, 'shield');
  const radar = module(modules, fit.radar, 'radar');
  const speed = engine?.speed ?? 1;
  const accel = engine?.accel ?? 1;
  return {
    ...hull,
    maxSpeed: hull.maxSpeed * speed,
    acceleration: hull.acceleration * accel,
    brakeAcceleration: hull.brakeAcceleration * accel,
    shield: shield?.shield ?? 0,
    shieldRegen: shield?.shieldRegen ?? 0,
    radar: radar?.radar ?? hull.radar,
    evasion: hull.evasion + fitEvasion(fit, modules),
    cargo: hull.cargo + utilities(fit, modules).reduce((sum, m) => sum + (m.cargo ?? 0), 0),
  };
}

/** Сколько энергии забирает всё, что стоит (GDD §18). */
export function fitPower(fit: ShipFit, weapons: WeaponConfig, modules: ModuleConfig | null): number {
  let total = 0;
  for (const id of fit.weapons) if (id && weapons[id]) total += weapons[id].power ?? 0;
  if (!modules) return total;
  for (const slot of MODULE_SLOTS) total += module(modules, fit[slot], slot)?.power ?? 0;
  for (const m of utilities(fit, modules)) total += m.power ?? 0;
  return total;
}

/** Сколько энергии даёт генератор; без каталога модулей энергию не считают. */
export function fitOutput(fit: ShipFit, modules: ModuleConfig | null): number {
  return modules ? (module(modules, fit.generator, 'generator')?.output ?? 0) : Infinity;
}

/**
 * Встанет ли id в slot (null — снять) — та же проверка, что на сервере: вид слота, класс, энергия
 * с учётом того, что старый предмет из слота уходит. null — встанет.
 */
export function canInstall(
  hull: HullParams,
  fit: ShipFit,
  slot: string,
  id: string | null,
  weapons: WeaponConfig,
  modules: ModuleConfig | null,
): FitProblem | null {
  const index = weaponIndex(slot);
  if (index !== null) {
    const slots = hullSlots(hull);
    if (index >= slots.length) return 'slot';
    if (id !== null) {
      const weapon = weapons[id];
      if (!weapon) return 'slot';
      if (!classFits(weapon.class, slots[index])) return 'class';
    }
  } else if (utilityIndex(slot) !== null) {
    if (!modules || utilityIndex(slot)! >= hullUtilitySlots(hull)) return 'slot';
    if (id !== null) {
      const m = modules[id];
      if (!m || m.slot !== UTILITY) return 'slot';
      if (!classFits(m.class, hull.class)) return 'class';
    }
  } else {
    if (!modules) return 'slot';
    if (id === null) {
      if ((REQUIRED_SLOTS as string[]).includes(slot)) return 'required';
    } else {
      const m = modules[id];
      if (!m || m.slot !== slot) return 'slot';
      if (!classFits(m.class, hull.class)) return 'class';
    }
  }
  const next = fitWith(fit, slot, id);
  return fitPower(next, weapons, modules) <= fitOutput(next, modules) + 1e-9 ? null : 'power';
}

/** Все стоящие пушки по слотам (пустые и неизвестные пропущены). */
export function fitWeapons(fit: ShipFit | null, weapons: { has(id: string): boolean; get(id: string): WeaponParams }): WeaponParams[] {
  if (!fit) return [];
  return fit.weapons.filter((id): id is string => !!id && weapons.has(id)).map((id) => weapons.get(id));
}

/** Строка характеристик модуля на витрине. */
export function moduleLabel(m: ModuleParams): string {
  const power = (m.power ?? 0) > 0 ? ` · энергия ${m.power}` : '';
  switch (m.slot) {
    case 'engine':
      return `скорость ×${m.speed ?? 1} · разгон ×${m.accel ?? 1}${power}`;
    case 'shield':
      return `щит ${m.shield ?? 0} · +${m.shieldRegen ?? 0}/с${power}`;
    case 'radar':
      return `дальность ${m.radar ?? 0}${power}`;
    case 'generator':
      return `даёт энергии ${m.output ?? 0}`;
    default: {
      const parts: string[] = [];
      if (m.repair) parts.push(`ремонт ${m.repair}/с вне боя`);
      if (m.cooling) parts.push(`перезарядка −${Math.round(m.cooling * 100)} %`);
      if (m.cargo) parts.push(`трюм +${m.cargo}`);
      if (m.evasion) parts.push(`уклонение +${m.evasion} %`);
      if (m.blockKinetic) parts.push(`блок кинетики ${m.blockKinetic} %`);
      if (m.blockEnergy) parts.push(`блок энергии ${m.blockEnergy} %`);
      if (m.intercept) parts.push(`сбивает ракеты ${m.intercept.chance} % в радиусе ${m.intercept.range}`);
      return parts.join(' · ') + power;
    }
  }
}

/** Значок тира на иконке: «Mk2»; Mk1 — без значка. */
export function tierBadge(id: string, tier?: number): string | null {
  const mark = tier ?? tierOf(id);
  return mark > 1 ? `Mk${mark}` : null;
}

/** Тир по id предмета: «ion_mk2» → 2. */
export function tierOf(id: string): number {
  const m = /^(.+)_mk([2-3])$/.exec(id);
  return m ? Number(m[2]) : 1;
}

/** Базовый id без тира: «ion_mk2» → «ion». */
export function baseId(id: string): string {
  const m = /^(.+)_mk([2-3])$/.exec(id);
  return m ? m[1] : id;
}

/** «Энергии не хватит» — текст к коду проблемы. */
export function describeFitProblem(problem: FitProblem): string {
  switch (problem) {
    case 'power':
      return 'не хватит энергии';
    case 'class':
      return 'класс не подходит';
    case 'required':
      return 'снять нельзя';
    default:
      return 'не подходит';
  }
}
