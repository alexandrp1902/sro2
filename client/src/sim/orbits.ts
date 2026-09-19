import type { OrbitDto, SystemDto } from '../net/protocol';
import { DT } from './movement';

/**
 * Орбиты вокруг звезды в центре системы. Зеркало Sro.Sim/Orbits.cs: сервер и клиент считают положение станции
 * и планет одной формулой от одного орбитального времени, по сети идут только параметры.
 */

export interface Point {
  x: number;
  y: number;
}

/** Станция в центре — система без galaxy.json. */
export const CENTER: OrbitDto = { radius: 0, periodMinutes: 60, phase: 0 };

/** Орбитальное время в тик tick системы, секунды. */
export function orbitSeconds(system: SystemDto | null, tick: number): number {
  return (system?.orbitEpoch ?? 0) + tick * DT;
}

/** Угол на орбите в радианах: 0 — вправо от звезды, растёт по часовой стрелке на экране. */
export function orbitAngle(orbit: OrbitDto, seconds: number): number {
  const turns = orbit.periodMinutes === 0 ? 0 : seconds / (orbit.periodMinutes * 60);
  // Только дробная часть оборотов: секунды — unix-время, точность угла от него зависеть не должна.
  return (orbit.phase * Math.PI) / 180 + (turns - Math.floor(turns)) * 2 * Math.PI;
}

export function orbitAt(orbit: OrbitDto, seconds: number): Point {
  if (orbit.radius <= 0) return { x: 0, y: 0 };
  const angle = orbitAngle(orbit, seconds);
  return { x: orbit.radius * Math.cos(angle), y: orbit.radius * Math.sin(angle) };
}

/** Точка в осях станции → мир: +y — прочь от звезды, +x — вдоль орбиты. У станции в центре оси мировые. */
export function toWorld(orbit: OrbitDto, seconds: number, local: Point): Point {
  if (orbit.radius <= 0) return { x: local.x, y: local.y };
  const s = orbitAt(orbit, seconds);
  const angle = orbitAngle(orbit, seconds);
  const sin = Math.sin(angle);
  const cos = Math.cos(angle);
  return { x: s.x + local.x * sin + local.y * cos, y: s.y - local.x * cos + local.y * sin };
}

/** Поворот осей станции: картинка станции и всё, что к ней пришвартовано, повёрнуты на этот угол. */
export function frameRotation(orbit: OrbitDto, seconds: number): number {
  return orbit.radius <= 0 ? 0 : orbitAngle(orbit, seconds) - Math.PI / 2;
}
