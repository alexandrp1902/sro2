import type { GalaxyDto } from '../net/protocol';
import { linkGateNumbers } from '../sim/galaxy';

/**
 * Чистая раскладка карты галактики: где стоят бейджи, какой viewBox, у каких концов связей номера врат.
 * Без DOM — чтобы это можно было проверить тестом, а galaxyMap.ts только рисовал.
 */

export interface MapBox {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** Рамка карты по данным: системы плюс поля под подписи. Пустая галактика — квадрат 0..100, как раньше. */
export function mapViewBox(systems: readonly { x: number; y: number }[], pad = 12): MapBox {
  if (systems.length === 0) return { x: 0, y: 0, w: 100, h: 100 };
  const xs = systems.map((s) => s.x);
  const ys = systems.map((s) => s.y);
  const x = Math.min(...xs) - pad;
  const y = Math.min(...ys) - pad;
  return { x, y, w: Math.max(...xs) + pad - x, h: Math.max(...ys) + pad - y };
}

/** Подпись региона: над самой верхней его системой, по середине региона. */
export function regionLabelAt(systems: readonly { x: number; y: number }[], lift = 10): { x: number; y: number } {
  const x = systems.reduce((sum, s) => sum + s.x, 0) / systems.length;
  return { x, y: Math.min(...systems.map((s) => s.y)) - lift };
}

export type BadgeKind = 'home' | 'objective' | 'invasion' | 'demand';

export interface Badge {
  kind: BadgeKind;
  /** Смещение от центра узла в единицах карты. */
  dx: number;
  dy: number;
}

/** Слоты вокруг узла по порядку занятия: северо-восток, северо-запад, восток, запад (y растёт вниз). */
const SLOTS = [-45, -135, 0, 180];

const BADGE_ORDER: BadgeKind[] = ['home', 'objective', 'invasion', 'demand'];

/** Какие значки стоят у узла и где: каждый следующий берёт следующий свободный слот. */
export function nodeBadges(flags: Partial<Record<BadgeKind, boolean>>, radius = 7.6): Badge[] {
  const result: Badge[] = [];
  for (const kind of BADGE_ORDER) {
    if (!flags[kind]) continue;
    const angle = (SLOTS[result.length % SLOTS.length] * Math.PI) / 180;
    result.push({ kind, dx: round(Math.cos(angle) * radius), dy: round(Math.sin(angle) * radius) });
  }
  return result;
}

export interface GateBadge {
  x: number;
  y: number;
  /** Номер врат в той системе, у которой стоит бейдж. */
  n: number;
  /** Врата на проложенном курсе — бейдж стальной. */
  route: boolean;
}

/**
 * Номера врат только там, где они нужны пилоту: у текущей системы на всех её связях и у начала каждого
 * прыжка курса. Остальные номера перечисляет карточка системы — на карте они были бы шумом.
 */
export function gateBadges(galaxy: GalaxyDto, current: string, path: readonly string[], along = 11): GateBadge[] {
  const byId = new Map(galaxy.systems.map((s) => [s.id, s]));
  const result = new Map<string, GateBadge>();
  const put = (from: string, to: string, route: boolean): void => {
    const a = byId.get(from);
    const b = byId.get(to);
    const numbers = linkGateNumbers(galaxy, from, to);
    if (!a || !b || !numbers) return;
    const key = `${from}>${to}`;
    const known = result.get(key);
    if (known) known.route ||= route;
    else result.set(key, { ...at(a, b, along), n: numbers.a, route });
  };
  for (let i = 1; i < path.length; i++) put(path[i - 1], path[i], true);
  for (const link of galaxy.links) {
    if (link.a === current) put(link.a, link.b, false);
    else if (link.b === current) put(link.b, link.a, false);
  }
  return [...result.values()];
}

/**
 * Ломаная курса по центрам систем, укороченная с обоих концов на trim: линия начинается за кольцом «вы здесь»
 * и кончается стрелкой перед узлом назначения, а не поверх него. Меньше двух точек — рисовать нечего.
 */
export function routePoints(points: readonly { x: number; y: number }[], trim = 7.5): { x: number; y: number }[] {
  if (points.length < 2) return [];
  const result = points.map((p) => ({ x: p.x, y: p.y }));
  const cut = (from: { x: number; y: number }, to: { x: number; y: number }): { x: number; y: number } => {
    const dx = to.x - from.x;
    const dy = to.y - from.y;
    const length = Math.hypot(dx, dy) || 1;
    const step = Math.min(trim, length / 2);
    return { x: round(from.x + (dx / length) * step), y: round(from.y + (dy / length) * step) };
  };
  result[0] = cut(result[0], result[1]);
  const last = result.length - 1;
  result[last] = cut(result[last], result[last - 1]);
  return result;
}

/** Точка на связи в along единицах от её начала, но не дальше трети длины: на короткой связи бейджи столкнулись бы. */
function at(from: { x: number; y: number }, to: { x: number; y: number }, along: number): { x: number; y: number } {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const length = Math.hypot(dx, dy) || 1;
  const step = Math.min(along, length / 3);
  return { x: round(from.x + (dx / length) * step), y: round(from.y + (dy / length) * step) };
}

function round(v: number): number {
  return Math.round(v * 100) / 100;
}
