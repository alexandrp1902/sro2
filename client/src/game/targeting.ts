import { inArc, inRange, type WeaponParams } from '../sim/combat';

// Выбор цели (GDD §9): тап или клик по кораблю, атака без цели, Tab.

/** Корабль, который можно выбрать целью (уничтоженные сюда не попадают). */
export interface TargetCandidate {
  id: number;
  x: number;
  y: number;
  size: number;
}

/** Что показывает камера: центр, масштаб и размер экрана. */
export interface ScreenView {
  x: number;
  y: number;
  zoom: number;
  width: number;
  height: number;
}

/** Палец толще курсора: радиус касания не меньше этого, px. */
const TOUCH_MIN_RADIUS_PX = 36;
const TOUCH_SLACK_PX = 8;
const MOUSE_SLACK_PX = 6;

/** @returns id корабля, ближайшего к точке экрана в пределах радиуса касания, или null */
export function pickAt(
  sx: number,
  sy: number,
  ships: Iterable<TargetCandidate>,
  view: ScreenView,
  touch: boolean,
): number | null {
  let best: number | null = null;
  let bestDistance = Infinity;
  for (const ship of ships) {
    const px = view.width / 2 + (ship.x - view.x) * view.zoom;
    const py = view.height / 2 + (ship.y - view.y) * view.zoom;
    const r = ship.size * view.zoom;
    const radius = touch ? Math.max(r + TOUCH_SLACK_PX, TOUCH_MIN_RADIUS_PX) : r + MOUSE_SLACK_PX;
    const distance = Math.hypot(sx - px, sy - py);
    if (distance <= radius && distance < bestDistance) {
      best = ship.id;
      bestDistance = distance;
    }
  }
  return best;
}

/** Атака без цели: ближайший корабль в секторе и дальности, иначе просто ближайший в дальности. */
export function nearest(
  own: { x: number; y: number; rot: number },
  ships: Iterable<TargetCandidate>,
  weapon: WeaponParams,
): number | null {
  let inArcId: number | null = null;
  let inArcDistance = Infinity;
  let inRangeId: number | null = null;
  let inRangeDistance = Infinity;
  for (const ship of ships) {
    const dx = ship.x - own.x;
    const dy = ship.y - own.y;
    const distance = Math.hypot(dx, dy);
    if (!inRange(weapon, distance)) continue;
    if (distance < inRangeDistance) {
      inRangeId = ship.id;
      inRangeDistance = distance;
    }
    if (distance < inArcDistance && inArc(own.rot, dx, dy, weapon.arc)) {
      inArcId = ship.id;
      inArcDistance = distance;
    }
  }
  return inArcId ?? inRangeId;
}

/** Tab: следующая цель по удалённости; после самой дальней — снова ближайшая. */
export function cycle(own: { x: number; y: number }, ships: Iterable<TargetCandidate>, currentId: number): number | null {
  const sorted = [...ships].sort((a, b) => Math.hypot(a.x - own.x, a.y - own.y) - Math.hypot(b.x - own.x, b.y - own.y));
  if (sorted.length === 0) return null;
  const index = sorted.findIndex((ship) => ship.id === currentId);
  return sorted[(index + 1) % sorted.length].id;
}
