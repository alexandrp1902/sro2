import type { GalaxyDto, GateDto } from '../net/protocol';
import { gateIndexTo, gateName, gateNumber, route } from './galaxy';

/**
 * Курс по галактике (M16b). Автопилота в игре нет и не планируется: курс — это подсказка, куда лететь,
 * а лететь к вратам по-прежнему самому. Поэтому он живёт целиком на клиенте, и серверу о нём знать незачем.
 */
export interface CourseView {
  /** Конечная система курса. */
  to: string;
  /** Путь отсюда: [текущая, …, конечная]. Пусто — пути нет. */
  path: string[];
  /** Сколько прыжков осталось. */
  hops: number;
  /** Следующая система пути; null — мы уже на месте. */
  next: string | null;
  /** Номер врат отсюда к next; null — мы на месте или врат в эту систему тут нет. */
  gate: number | null;
  /** Мы в конечной системе. */
  done: boolean;
  /** Пути отсюда нет: система отрезана или исчезла из данных. */
  lost: boolean;
}

/**
 * Что показывать про курс из системы current. null — курса нет.
 * gates — врата текущей системы (из welcome) или их цели: по ним считается номер нужных врат.
 */
export function courseView(
  galaxy: GalaxyDto,
  current: string,
  to: string | null | undefined,
  gates: readonly GateDto[] | readonly string[] | null | undefined,
): CourseView | null {
  if (!to) return null;
  const path = route(galaxy, current, to);
  const next = path[1] ?? null;
  const index = next ? gateIndexTo(gates, next) : -1;
  return {
    to,
    path,
    hops: Math.max(0, path.length - 1),
    next,
    gate: index < 0 ? null : gateNumber(index),
    done: current === to,
    lost: path.length === 0,
  };
}

/** «1 прыжок», «3 прыжка», «5 прыжков», «21 прыжок». */
export function hopsWord(n: number): string {
  const tens = n % 100;
  const ones = n % 10;
  if (ones === 1 && tens !== 11) return `${n} прыжок`;
  if (ones >= 2 && ones <= 4 && (tens < 12 || tens > 14)) return `${n} прыжка`;
  return `${n} прыжков`;
}

/**
 * Строка о курсе. name — имя конечной системы для ленты («Курс на Vega: 3 прыжка…»);
 * без имени — для карточки системы, где имя и так написано сверху.
 */
export function courseLine(view: CourseView, name?: string | null): string {
  const head = name ? `Курс на ${name}` : 'Маршрут';
  if (view.done) return name ? `${name} — вы на месте` : 'Вы на месте: маршрут пройден';
  if (view.lost) return name ? `Маршрута до ${name} отсюда нет` : 'Маршрута отсюда нет';
  const gate = view.gate === null ? '' : `, ближайшие врата — ${gateName(view.gate - 1)}`;
  return `${head}: ${hopsWord(view.hops)}${gate}`;
}

/** Ключ связи для подсветки: концы отсортированы, потому что маршрут ходит в обе стороны. */
export function linkKey(a: string, b: string): string {
  return a < b ? `${a}|${b}` : `${b}|${a}`;
}

/** Связи маршрута — их карта рисует иначе. */
export function routeLinks(path: readonly string[]): Set<string> {
  const keys = new Set<string>();
  for (let i = 1; i < path.length; i++) keys.add(linkKey(path[i - 1], path[i]));
  return keys;
}
