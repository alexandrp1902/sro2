// Сила огня двигателей: одно правило на весь флот.
//
// Форму факела с M20 даёт render/exhaust.ts — по точкам сопел из shipNozzles.ts, и рисованные кадры
// пламени больше не нужны никому. Здесь осталось то, что к картинке не привязано: ступени тяги.
import { localVelocity, type HullParams, type ShipState } from '../sim/movement';

/** Ступени огня: 0, 25, 50, 75, 100 % — между ними пламя не живёт. */
const STEPS = 4;

/**
 * Медленнее этой доли полного хода корабль считается стоящим. Тяга без полёта бывает часто: пират
 * висит на дистанции боя и жмёт газ в развороте — сопло при этом гореть не должно.
 */
const IDLE_SHARE = 0.1;

/**
 * Сила огня ступенями: 0 — не горит, 1 — полный. Ступень берётся от тяги, но только у того,
 * кто действительно летит вперёд.
 */
export function engineGlow(state: ShipState, throttle: number, hull: HullParams): number {
  if (throttle <= 0) return 0;
  const { forward } = localVelocity(state);
  if (forward < hull.maxSpeed * IDLE_SHARE) return 0;
  return Math.round(Math.min(1, throttle) * STEPS) / STEPS;
}
