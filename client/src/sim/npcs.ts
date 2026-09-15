// Пираты из shared/npcs.json — только то, что нужно клиенту для карты: логова и укрытие у станции.
// Поведение пиратов целиком на сервере (server/Sro.Server/Game/PirateBrain.cs).

export interface NpcType {
  name: string;
}

export interface NpcSpawn {
  type: string;
  level: number;
  x: number;
  y: number;
  count?: number;
}

export interface NpcRules {
  /** Укрытие вокруг станции: пираты сюда не залетают. */
  stationSafeRadius: number;
  patrolRadius: number;
  types?: Record<string, NpcType>;
  spawns?: NpcSpawn[];
}

/** Логово на карте: точка, радиус патруля и подпись по составу. */
export interface Lair {
  x: number;
  y: number;
  radius: number;
  label: string;
}

/** Записи с одной точкой — одно логово: «Пираты Ур.2 ×2», «Тяжёлый пират Ур.3 + Пират Ур.2». */
export function lairs(npcs: NpcRules): Lair[] {
  const byPoint = new Map<string, { x: number; y: number; parts: string[] }>();
  for (const spawn of npcs.spawns ?? []) {
    const key = `${spawn.x}|${spawn.y}`;
    let lair = byPoint.get(key);
    if (!lair) {
      lair = { x: spawn.x, y: spawn.y, parts: [] };
      byPoint.set(key, lair);
    }
    const name = `${npcs.types?.[spawn.type]?.name ?? spawn.type} Ур.${spawn.level}`;
    const count = spawn.count ?? 1;
    lair.parts.push(count > 1 ? `${name} ×${count}` : name);
  }
  return [...byPoint.values()].map((lair) => ({ x: lair.x, y: lair.y, radius: npcs.patrolRadius, label: lair.parts.join(' + ') }));
}
