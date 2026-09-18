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

/** Стрелка у края экрана к кораблю за его пределами и подпись рядом с ней — в экранных координатах. */
export interface EdgeArrow {
  id: number;
  x: number;
  y: number;
  label: { x: number; y: number; width: number; height: number };
}

/** Стрелка мелкая: мыши — такой радиус вокруг неё, пальцу — как для корабля (TOUCH_MIN_RADIUS_PX). */
const ARROW_MOUSE_RADIUS_PX = 16;

/** @returns id корабля, по стрелке или подписи которого у края экрана пришёлся тап, или null */
export function pickArrow(sx: number, sy: number, arrows: Iterable<EdgeArrow>, touch: boolean): number | null {
  const radius = touch ? TOUCH_MIN_RADIUS_PX : ARROW_MOUSE_RADIUS_PX;
  const slack = touch ? TOUCH_SLACK_PX : 0;
  let best: number | null = null;
  let bestDistance = Infinity;
  for (const arrow of arrows) {
    const distance = Math.hypot(sx - arrow.x, sy - arrow.y);
    const l = arrow.label;
    const onLabel = sx >= l.x - slack && sx <= l.x + l.width + slack && sy >= l.y - slack && sy <= l.y + l.height + slack;
    if ((distance <= radius || onLabel) && distance < bestDistance) {
      best = arrow.id;
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

/** Ближайший предмет в радиусе — для клавиши «взять ближайший» на ПК. Автопилота нет (боевой документ §45). */
export function nearestLoot(
  own: { x: number; y: number },
  items: Iterable<TargetCandidate>,
  maxDistance: number,
): number | null {
  let best: number | null = null;
  let bestDistance = maxDistance;
  for (const item of items) {
    const distance = Math.hypot(item.x - own.x, item.y - own.y);
    if (distance <= bestDistance) {
      best = item.id;
      bestDistance = distance;
    }
  }
  return best;
}

const TAU = 2 * Math.PI;

/** Кольцо спирали по умолчанию — один «сектор»: дальность пушки по умолчанию. */
export const DEFAULT_RING = 700;

/**
 * Место объекта на раскручивающейся спирали: номер кольца по дистанции плюс азимут по часовой стрелке.
 * Ноль азимута — прямо по носу экрана (вверх), как в проверке сектора стрельбы.
 */
function spiralKey(own: { x: number; y: number }, item: TargetCandidate, ring: number): number {
  const dx = item.x - own.x;
  const dy = item.y - own.y;
  const band = ring > 0 ? Math.floor(Math.hypot(dx, dy) / ring) : 0;
  const bearing = Math.atan2(dx, -dy);
  return band * TAU + (bearing < 0 ? bearing + TAU : bearing);
}

/**
 * Переключение выделения по спирали: от ближнего кольца к дальнему, внутри кольца — по часовой стрелке.
 * Список закольцован: после последнего снова первый, перед первым — последний.
 * Ничего не выделено — берём ближайший объект, а дальше идём по спирали от него.
 */
export function cycle(
  own: { x: number; y: number },
  ships: Iterable<TargetCandidate>,
  currentId: number,
  step: 1 | -1 = 1,
  ring: number = DEFAULT_RING,
): number | null {
  const list = [...ships];
  if (list.length === 0) return null;
  if (!list.some((ship) => ship.id === currentId)) {
    return list.reduce((best, ship) =>
      Math.hypot(ship.x - own.x, ship.y - own.y) < Math.hypot(best.x - own.x, best.y - own.y) ? ship : best,
    ).id;
  }

  list.sort((a, b) => spiralKey(own, a, ring) - spiralKey(own, b, ring));
  const index = list.findIndex((ship) => ship.id === currentId);
  return list[(index + step + list.length) % list.length].id;
}
