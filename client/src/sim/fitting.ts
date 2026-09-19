// Оснащение корабля (GDD §11–13, §18–20): оружейные слоты корпуса, модули и энергия генератора.
// Зеркало server/Sro.Sim/Fitting.cs; совпадение проверяет shared/test-vectors/fitting.json.
// Предсказание движения летит на корпусе с модулями — иначе разошлось бы с сервером.

import defaults from '../../../shared/modules.json';
import type { WeaponConfig, WeaponParams } from './combat';
import type { HullParams } from './movement';

export type EquipClass = 'S' | 'M' | 'L';
export type ModuleSlot = 'engine' | 'shield' | 'radar' | 'tank' | 'generator';
/** w0…w5 — оружейные слоты, остальное — модули. */
export type Slot = `w${number}` | ModuleSlot;

/** Поля, которых нет в файле, — по умолчанию, как на сервере: множители 1, остальное 0. */
export interface ModuleParams {
  name: string;
  slot: ModuleSlot;
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
  /** Бак: ёмкость. */
  fuel?: number;
  /** Генератор: сколько энергии даёт. */
  output?: number;
}

export type ModuleConfig = Record<string, ModuleParams>;

/** Что стоит на корабле: пушка в каждом оружейном слоте (null — пусто) и по модулю каждого вида. */
export interface ShipFit {
  weapons: (string | null)[];
  engine?: string | null;
  shield?: string | null;
  radar?: string | null;
  tank?: string | null;
  generator?: string | null;
}

export const MODULE_SLOTS: ModuleSlot[] = ['engine', 'shield', 'radar', 'tank', 'generator'];
/** Без этих модулей корабль не летает: заменить можно, снять нельзя. */
export const REQUIRED_SLOTS: ModuleSlot[] = ['engine', 'radar', 'generator'];

export const SLOT_NAMES: Record<ModuleSlot, string> = {
  engine: 'Двигатель',
  shield: 'Щит',
  radar: 'Радар',
  tank: 'Бак',
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

/** Номер оружейного слота; null — это модуль. */
export function weaponIndex(slot: string): number | null {
  const m = /^w(\d)$/.exec(slot);
  return m ? Number(m[1]) : null;
}

/** Оружейные слоты корпуса и их классы; у старого баланса — один слот класса корпуса. */
export function hullSlots(hull: HullParams): string[] {
  return hull.weaponSlots ?? [hull.class ?? 'L'];
}

export function fitGet(fit: ShipFit, slot: string): string | null {
  const i = weaponIndex(slot);
  if (i !== null) return fit.weapons[i] ?? null;
  return fit[slot as ModuleSlot] ?? null;
}

export function fitWith(fit: ShipFit, slot: string, id: string | null): ShipFit {
  const i = weaponIndex(slot);
  if (i === null) return { ...fit, [slot]: id };
  const weapons = [...fit.weapons];
  while (weapons.length <= i) weapons.push(null);
  weapons[i] = id;
  return { ...fit, weapons };
}

function module(modules: ModuleConfig, id: string | null | undefined, slot: ModuleSlot): ModuleParams | null {
  const m = id ? modules[id] : undefined;
  return m && m.slot === slot ? m : null;
}

/**
 * Корпус с учётом модулей: двигатель умножает скорость, разгон и торможение; щит, радар и бак — из модулей.
 * Без каталога модулей (старый сервер) или без оснащения — корпус как есть.
 */
export function effectiveHull(hull: HullParams, fit: ShipFit | null, modules: ModuleConfig | null): HullParams {
  if (!modules || !fit) return hull;
  const engine = module(modules, fit.engine, 'engine');
  const shield = module(modules, fit.shield, 'shield');
  const radar = module(modules, fit.radar, 'radar');
  const tank = module(modules, fit.tank, 'tank');
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
    fuel: tank?.fuel ?? 0,
  };
}

/** Сколько энергии забирает всё, что стоит (GDD §18). */
export function fitPower(fit: ShipFit, weapons: WeaponConfig, modules: ModuleConfig | null): number {
  let total = 0;
  for (const id of fit.weapons) if (id && weapons[id]) total += weapons[id].power ?? 0;
  if (!modules) return total;
  for (const slot of MODULE_SLOTS) total += module(modules, fit[slot], slot)?.power ?? 0;
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
    case 'tank':
      return `топливо ${m.fuel ?? 0}${power}`;
    case 'generator':
      return `даёт энергии ${m.output ?? 0}`;
  }
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
