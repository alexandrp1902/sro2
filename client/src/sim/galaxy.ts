import type { GalaxyDto, GalaxySystemDto, GateDto, PvpRule, SystemDto } from '../net/protocol';

/** Цвет опасности (GDD §33): от спокойного зелёного к красному. */
const DANGER_COLORS = [0x6fe08a, 0xb9e06f, 0xe0c46f, 0xe08a4a, 0xe0524a];

const DANGER_NAMES = ['безопасная', 'низкая опасность', 'средняя опасность', 'высокая опасность', 'экстремальная'];

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

/** Сколько топлива стоит прыжок a → b; null — прямого маршрута нет. */
export function jumpCost(galaxy: GalaxyDto, a: string, b: string): number | null {
  const link = galaxy.links.find((l) => (l.a === a && l.b === b) || (l.a === b && l.b === a));
  return link ? link.cost : null;
}

/** Соседи системы по маршрутам, с ценой прыжка. */
export function neighbours(galaxy: GalaxyDto, from: string): { id: string; cost: number }[] {
  const result: { id: string; cost: number }[] = [];
  for (const link of galaxy.links) {
    if (link.a === from) result.push({ id: link.b, cost: link.cost });
    else if (link.b === from) result.push({ id: link.a, cost: link.cost });
  }
  return result;
}

/** Что пилот узнаёт о прыжке в соседнюю систему по карте: хватит ли топлива туда и обратно. */
export type JumpOutlook =
  /** Это текущая система. */
  | 'here'
  /** Прямого маршрута нет. */
  | 'far'
  /** Не хватает топлива. */
  | 'noFuel'
  /** Туда хватит, а обратно — нет, и заправиться там негде. */
  | 'oneWay'
  /** Можно прыгать. */
  | 'ok';

export function jumpOutlook(galaxy: GalaxyDto, from: string, to: string, fuel: number): JumpOutlook {
  if (from === to) return 'here';
  const cost = jumpCost(galaxy, from, to);
  if (cost === null) return 'far';
  if (fuel < cost) return 'noFuel';
  const target = galaxy.systems.find((s) => s.id === to);
  if (target && !target.station && fuel - cost < cost) return 'oneWay';
  return 'ok';
}

/** Системы по кратчайшему числу прыжков от from: карта показывает, как далеко до каждой. */
export function hops(galaxy: GalaxyDto, from: string): Map<string, number> {
  const result = new Map<string, number>([[from, 0]]);
  const queue = [from];
  while (queue.length > 0) {
    const id = queue.shift()!;
    for (const next of neighbours(galaxy, id)) {
      if (result.has(next.id)) continue;
      result.set(next.id, result.get(id)! + 1);
      queue.push(next.id);
    }
  }
  return result;
}

/** Врата в прицеле: id −2, −3… — отрицательные, как у станции, чтобы не совпасть с id сервера. */
export function gateMarkId(index: number): number {
  return -2 - index;
}

export function gateIndex(markId: number): number {
  return -2 - markId;
}

/** Подпись у врат: «→ Vega · 20». */
export function gateLabel(gate: GateDto): string {
  return `→ ${gate.name} · ${gate.cost}`;
}

export type { GalaxyDto, GalaxySystemDto, GateDto, PvpRule, SystemDto };
