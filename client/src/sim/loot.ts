// Зеркало server/Sro.Sim/LootRules.cs — та часть, которая нужна клиенту: вид предмета,
// радиус захвата и правила сдачи груза. Таблицы дропа остаются на сервере, клиент их не считает.

/** Редкость (GDD §23) — от неё цвет предмета на экране. */
export type Rarity = 'common' | 'uncommon' | 'rare' | 'epic' | 'legendary';

export interface LootItem {
  name: string;
  rarity: Rarity;
  /** Сколько места занимает одна штука в трюме. */
  volume: number;
  /** Кредитов за штуку при сдаче на станции. */
  price: number;
}

export interface LootRules {
  /** Ближе этого предмет сам идёт в трюм (GDD §21). */
  pickupRange: number;
  lifetimeSeconds: number;
  /** Последние секунды жизни предмет мигает. */
  fadeSeconds: number;
  /** Сдача груза на станции включена. */
  stationUnload: boolean;
  /** Радиус станции: ближе этого можно пристыковаться (M6), в доке продают груз. */
  stationRange: number;
  /** Каталог предметов; сервер может прислать null, если лута нет. */
  items?: Record<string, LootItem> | null;
}

/** Лута нет: до welcome и на серверах без loot.json. */
export const NO_LOOT: LootRules = {
  pickupRange: 130,
  lifetimeSeconds: 120,
  fadeSeconds: 10,
  stationUnload: false,
  stationRange: 200,
  items: null,
};

export const RARITY_COLORS: Record<Rarity, number> = {
  common: 0x9aa4b4,
  uncommon: 0x6fe08a,
  rare: 0x6fa8ff,
  epic: 0xc77dff,
  legendary: 0xffb347,
};

/** Предмет по идентификатору; нет в каталоге — null (баланс мог поменяться). */
export function lootItem(rules: LootRules, id: string): LootItem | null {
  return rules.items?.[id] ?? null;
}

/** Название стопки: «Металл ×5». Неизвестный предмет показываем как есть, чтобы не терять его. */
export function lootLabel(rules: LootRules, id: string, count: number): string {
  const name = lootItem(rules, id)?.name ?? id;
  return count > 1 ? `${name} ×${count}` : name;
}

export function rarityColor(rules: LootRules, id: string): number {
  return RARITY_COLORS[lootItem(rules, id)?.rarity ?? 'common'];
}
