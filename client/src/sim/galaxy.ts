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
 * Куда прыгать первым, чтобы кратчайшим путём попасть из from в to: соседняя система на этом пути.
 * null — уже там или пути нет. При равных путях — сосед, который раньше в списке маршрутов.
 */
export function nextHop(galaxy: GalaxyDto, from: string, to: string): string | null {
  if (from === to) return null;
  const distance = hops(galaxy, to);
  if (!distance.has(from)) return null;
  const want = distance.get(from)! - 1;
  return neighbours(galaxy, from).find((id) => distance.get(id) === want) ?? null;
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

/** Подпись у врат: «→ Vega». */
export function gateLabel(gate: GateDto): string {
  return `→ ${gate.name}`;
}

export type { GalaxyDto, GalaxySystemDto, GateDto, PvpRule, SystemDto };
