// Бой: шанс попадания и сектор стрельбы (GDD §15–16, §46; боевой документ v0.2, §35, §39–40).
// Зеркало server/Sro.Sim/Combat.cs — только для подсказок в карточке цели: попадания считает сервер.
// Совпадение проверяет shared/test-vectors/combat.json.

import { TICK_RATE, wrapAngle, type HullParams } from './movement';

export interface WeaponParams {
  name: string;
  damage: number;
  /** Точность, %. */
  accuracy: number;
  /** Перезарядка, с. */
  cooldown: number;
  /** До этой дистанции штрафа за дальность нет (§16). */
  optimalRange: number;
  /** Дальше выстрел невозможен. */
  maxRange: number;
  /** Штраф к шансу на maxRange, %; от optimalRange растёт линейно. */
  rangePenalty: number;
  /** Ближе этого растёт штраф за стрельбу в упор; 0 — такого штрафа нет. */
  closeRange: number;
  /** Штраф к шансу в упор, %; от closeRange падает до нуля. */
  closePenalty: number;
  /** Сектор стрельбы от носа в каждую сторону, градусы. */
  arc: number;
  /** Вид трассера: bolt — снаряд, beam — луч, orb — плазменный шар. */
  kind: string;
  /** Цвет трассера, #rrggbb. */
  color: string;
}

export type WeaponConfig = Record<string, WeaponParams>;

/** Правила боя из shared/combat.json (дроны клиенту не нужны). */
export interface CombatRules {
  respawnSeconds: number;
  protectionSeconds: number;
  shieldRegenDelay: number;
  spawnJitter: number;
  /** Сколько единиц мира в одном «секторе» — мере дистанции для игрока. */
  sectorUnit: number;
}

/** Сектор по умолчанию, пока не пришли правила боя. */
export const DEFAULT_SECTOR_UNIT = 700;

/**
 * Дистанция в секторах: 1 сектор — примерно дальность пушки по умолчанию и половина экрана телефона.
 * «1.4» читается лучше, чем «980 метров», и сразу говорит, достаёт ли оружие.
 */
export function formatSectors(distance: number, sectorUnit: number): string {
  const unit = sectorUnit > 0 ? sectorUnit : DEFAULT_SECTOR_UNIT;
  return (distance / unit).toFixed(1);
}

export const MIN_HIT_CHANCE = 5;
export const MAX_HIT_CHANCE = 95;

const DEG_TO_RAD = Math.PI / 180;
/** Цель ровно на границе сектора — в секторе, несмотря на погрешность atan2. */
const ARC_EPSILON = 1e-9;
const SAME_SPOT_SQ = 1e-9;

/** Уклонение цели, % (§39–40): базовое плюс добавка, растущая со скоростью. */
export function evasion(hull: HullParams, speed: number): number {
  return hull.evasion + hull.moveEvasion * clamp(speed / hull.maxSpeed, 0, 1);
}

/**
 * Штраф за дистанцию, % (GDD §16). Два склона: от optimalRange растёт до rangePenalty на maxRange,
 * и — если задан closeRange — от него растёт до closePenalty в упор. Между ними штрафа нет.
 */
export function rangePenalty(weapon: WeaponParams, distance: number): number {
  if (distance > weapon.optimalRange) {
    const span = weapon.maxRange - weapon.optimalRange;
    if (span <= 0) return weapon.rangePenalty;
    return weapon.rangePenalty * Math.min(1, (distance - weapon.optimalRange) / span);
  }
  if (weapon.closeRange > 0 && distance < weapon.closeRange) {
    return weapon.closePenalty * (1 - distance / weapon.closeRange);
  }
  return 0;
}

export function inRange(weapon: WeaponParams, distance: number): boolean {
  return distance <= weapon.maxRange;
}

/** Шанс попадания, % (GDD §46): точность − уклонение − штраф за дистанцию, в пределах 5…95. */
export function hitChance(weapon: WeaponParams, distance: number, target: HullParams, targetSpeed: number): number {
  return hitChanceByEvasion(weapon, distance, evasion(target, targetSpeed));
}

/** Шанс попадания по цели с готовым уклонением, % — для целей без корпуса (метеорит не уклоняется). */
export function hitChanceByEvasion(weapon: WeaponParams, distance: number, targetEvasion: number): number {
  return clamp(weapon.accuracy - targetEvasion - rangePenalty(weapon, distance), MIN_HIT_CHANCE, MAX_HIT_CHANCE);
}

/** Цель в секторе стрельбы (§35). (dx, dy) — от стрелка к цели; корабли в одной точке — в секторе. */
export function inArc(rot: number, dx: number, dy: number, arcDeg: number): boolean {
  if (dx * dx + dy * dy < SAME_SPOT_SQ) return true;
  const bearing = Math.atan2(dx, -dy);
  return Math.abs(wrapAngle(bearing - rot)) <= arcDeg * DEG_TO_RAD + ARC_EPSILON;
}

/** Перезарядка в тиках, как на сервере. */
export function cooldownTicks(weapon: WeaponParams): number {
  return Math.max(1, Math.ceil(weapon.cooldown * TICK_RATE - 1e-9));
}

/** Почему пушка стреляет или нет — для карточки цели и рамки. */
export type AimState = 'ready' | 'arc' | 'range' | 'protected' | 'dead';

export interface Aim {
  state: AimState;
  distance: number;
  /** Шанс попадания, если бы выстрел состоялся, %. */
  chance: number;
}

export interface AimTarget {
  x: number;
  y: number;
  vx: number;
  vy: number;
  dead: boolean;
  protected: boolean;
}

/**
 * Та же проверка, что у сервера перед выстрелом (GDD §45): уничтожена, под защитой, дальность, сектор.
 * targetEvasion — уклонение цели, %: у корабля `evasion(hull, speed)`, у метеорита 0.
 */
export function assess(
  shooter: { x: number; y: number; rot: number },
  weapon: WeaponParams,
  target: AimTarget,
  targetEvasion: number,
): Aim {
  const dx = target.x - shooter.x;
  const dy = target.y - shooter.y;
  const distance = Math.hypot(dx, dy);
  const chance = hitChanceByEvasion(weapon, distance, targetEvasion);
  let state: AimState = 'ready';
  if (target.dead) state = 'dead';
  else if (target.protected) state = 'protected';
  else if (!inRange(weapon, distance)) state = 'range';
  else if (!inArc(shooter.rot, dx, dy, weapon.arc)) state = 'arc';
  return { state, distance, chance };
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}
