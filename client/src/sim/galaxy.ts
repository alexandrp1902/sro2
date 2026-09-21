import type { GalaxyDto, GalaxySystemDto, GateDto, PvpRule, SystemDto } from '../net/protocol';

/** Цвет опасности (GDD §33): от спокойного зелёного к красному; 6 — Дальний рубеж (M11). */
const DANGER_COLORS = [0x6fe08a, 0xb9e06f, 0xe0c46f, 0xe08a4a, 0xe0524a, 0xb53247];

const DANGER_NAMES = [
  'безопасная',
  'низкая опасность',
  'средняя опасность',
  'высокая опасность',
  'экстремальная',
  'гиблое место',
];

/** Регион системы словами; null — сервер регионов не знает. */
export function regionName(galaxy: GalaxyDto, id: string | null | undefined): string | null {
  return galaxy.regions?.find((r) => r.id === id)?.name ?? null;
}

const PVP_NAMES: Record<PvpRule, string> = {
  off: 'PvP нет',
  border: 'PvP вне станции',
  free: 'PvP везде',
};

export function dangerColor(danger: number): number {
  return DANGER_COLORS[Math.min(DANGER_COLORS.length, Math.max(1, Math.round(danger))) - 1];
}

/** «опасность 3» словами: «средняя опасность». */
export function dangerName(danger: number): string {
  return DANGER_NAMES[Math.min(DANGER_NAMES.length, Math.max(1, Math.round(danger))) - 1];
}

export function pvpName(pvp: PvpRule): string {
  return PVP_NAMES[pvp] ?? pvp;
}

/** Строка о системе для ленты при входе в неё: «Система Tau · средняя опасность · PvP вне станции». */
export function describeSystem(system: Pick<SystemDto, 'name' | 'danger' | 'pvp' | 'station'>): string {
  const parts = [`Система ${system.name}`, dangerName(system.danger), pvpName(system.pvp)];
  if (!system.station) parts.push('станции нет');
  return parts.join(' · ');
}

/** Есть ли прямой маршрут a → b. */
export function linked(galaxy: GalaxyDto, a: string, b: string): boolean {
  return galaxy.links.some((l) => (l.a === a && l.b === b) || (l.a === b && l.b === a));
}

/** Соседи системы по маршрутам. */
export function neighbours(galaxy: GalaxyDto, from: string): string[] {
  const result: string[] = [];
  for (const link of galaxy.links) {
    if (link.a === from) result.push(link.b);
    else if (link.b === from) result.push(link.a);
  }
  return result;
}

/**
 * Что пилот узнаёт о прыжке в соседнюю систему по карте. С M15.6 прыжок бесплатен и ничем не ограничен,
 * поэтому остались только «это здесь» и «прямого маршрута нет».
 */
export type JumpOutlook =
  /** Это текущая система. */
  | 'here'
  /** Прямого маршрута нет. */
  | 'far'
  /** Можно прыгать. */
  | 'ok';

export function jumpOutlook(galaxy: GalaxyDto, from: string, to: string): JumpOutlook {
  if (from === to) return 'here';
  return linked(galaxy, from, to) ? 'ok' : 'far';
}

/** Системы по кратчайшему числу прыжков от from: карта показывает, как далеко до каждой. */
export function hops(galaxy: GalaxyDto, from: string): Map<string, number> {
  const result = new Map<string, number>([[from, 0]]);
  const queue = [from];
  while (queue.length > 0) {
    const id = queue.shift()!;
    for (const next of neighbours(galaxy, id)) {
      if (result.has(next)) continue;
      result.set(next, result.get(id)! + 1);
      queue.push(next);
    }
  }
  return result;
}

/**
 * Кратчайший путь по вратам: [from, …, to], включая оба конца. [from] — уже на месте, пусто — пути нет.
 * При равных путях побеждает сосед, который раньше в списке маршрутов: BFS назначает предшественника
 * при первом посещении, а очередь наполняется в порядке links.
 */
export function route(galaxy: GalaxyDto, from: string, to: string): string[] {
  if (from === to) return [from];
  const from_ = new Map<string, string>([[from, from]]);
  const queue = [from];
  while (queue.length > 0) {
    const id = queue.shift()!;
    for (const next of neighbours(galaxy, id)) {
      if (from_.has(next)) continue;
      from_.set(next, id);
      if (next === to) {
        const path = [to];
        for (let at = to; at !== from; at = from_.get(at)!) path.unshift(from_.get(at)!);
        return path;
      }
      queue.push(next);
    }
  }
  return [];
}

/**
 * Куда прыгать первым, чтобы кратчайшим путём попасть из from в to: соседняя система на этом пути.
 * null — уже там или пути нет.
 */
export function nextHop(galaxy: GalaxyDto, from: string, to: string): string | null {
  return route(galaxy, from, to)[1] ?? null;
}

/** Ближайшая система со станцией (сама from, если в ней есть); null — станций нет. */
export function nearestStation(galaxy: GalaxyDto, from: string): string | null {
  let best: string | null = null;
  let bestHops = Infinity;
  for (const [id, n] of hops(galaxy, from)) {
    if (n < bestHops && galaxy.systems.find((s) => s.id === id)?.station) {
      best = id;
      bestHops = n;
    }
  }
  return best;
}

/** Врата в прицеле: id −2, −3… — отрицательные, как у станции, чтобы не совпасть с id сервера. */
export function gateMarkId(index: number): number {
  return -2 - index;
}

export function gateIndex(markId: number): number {
  return -2 - markId;
}

/**
 * Номер врат (M16b): позиция в списке врат системы плюс один. Своего имени у врат нет и не заводится —
 * порядок в galaxy.json устойчив, вторые врата в ту же систему запрещены проверкой данных, и на этом же
 * порядке уже стоит gateMarkId.
 */
export function gateNumber(index: number): number {
  return index + 1;
}

/** «Врата 3». */
export function gateName(index: number): string {
  return `Врата ${gateNumber(index)}`;
}

/**
 * Подписи врат на миникарте (M16c): буква системы, куда они ведут, — «V» у врат на Vega. Номер там читался
 * хуже: он говорит только про порядок в списке, а буква сразу отвечает «куда».
 *
 * Если в одной системе двое врат начинаются одинаково, подписи удлиняются все разом, пока не различатся:
 * лучше «So» и «Si», чем одинаковые «S». В нынешней galaxy.json до этого не доходит, но карта растёт.
 */
export function gateLetters(gates: readonly { name: string }[]): string[] {
  const names = gates.map((gate) => gate.name.trim() || '?');
  const longest = Math.max(1, ...names.map((name) => name.length));
  for (let length = 1; length < longest; length++) {
    const tries = names.map((name) => prefix(name, length));
    if (new Set(tries).size === tries.length) return tries;
  }
  return names.map((name) => prefix(name, longest));
}

/** Первая буква заглавная, остальные как в названии: «Sigma» → «Si», «Альфа Центавра» → «Аль». */
function prefix(name: string, length: number): string {
  const cut = name.slice(0, length);
  return cut.charAt(0).toUpperCase() + cut.slice(1);
}

/** Подпись у врат в мире: «Врата 3 · → Vega». */
export function gateLabel(gate: GateDto, index: number): string {
  return `${gateName(index)} · → ${gate.name}`;
}

/** Номер врат, ведущих в систему to; −1 — таких тут нет. */
export function gateIndexTo(gates: readonly GateDto[] | readonly string[] | null | undefined, to: string): number {
  return (gates ?? []).findIndex((g) => (typeof g === 'string' ? g : g.to) === to);
}

/**
 * Номера врат на обоих концах маршрута a ↔ b для карты галактики: из A в B это одни врата, из B в A —
 * другие, и номера у них разные. null — сервер списка врат не прислал (старый сервер или витрина).
 */
export function linkGateNumbers(galaxy: GalaxyDto, a: string, b: string): { a: number; b: number } | null {
  const gatesOf = (id: string): readonly string[] | null => galaxy.systems.find((s) => s.id === id)?.gates ?? null;
  const from = gatesOf(a);
  const to = gatesOf(b);
  if (!from || !to) return null;
  const ia = gateIndexTo(from, b);
  const ib = gateIndexTo(to, a);
  return ia < 0 || ib < 0 ? null : { a: gateNumber(ia), b: gateNumber(ib) };
}

export type { GalaxyDto, GalaxySystemDto, GateDto, PvpRule, SystemDto };
