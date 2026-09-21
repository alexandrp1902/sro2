// Зеркало server/Sro.Sim/LootRules.cs — та часть, которая нужна клиенту: вид предмета,
// радиус захвата и правила сдачи груза. Таблицы дропа остаются на сервере, клиент их не считает.

import { classRank } from './fitting';

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
  /** Снаряжение, которое тоже лежит в космосе (M11): его имя и картинку клиент берёт из каталогов. */
  gear?: Record<string, GearItem> | null;
}

/** Пушка или модуль, выпавший с пирата (M11): едет домой в трюме, на склад уходит в доке. */
export interface GearItem {
  name: string;
  /** Тир Mk1–Mk3: от него цвет. */
  tier: number;
  /** Картинка из каталога спрайтов; null — нарисуем общий контейнер. */
  sprite: string | null;
  /** Сколько места занимает в трюме: класс S, M, L — 2, 4, 6. */
  volume: number;
}

/** Место под пушку или модуль класса S, M, L: 2, 4, 6. Зеркало LootRules.GearVolume. */
export function gearVolume(equipClass: string | null | undefined): number {
  return classRank(equipClass) * 2;
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
  const item = rules.items?.[id];
  if (item) return item;
  const gear = rules.gear?.[id];
  // Снаряжение место в трюме занимает, но грузом не торгуют: его цену знает магазин, а не станция.
  return gear
    ? {
        name: gear.name,
        rarity: gear.tier >= 3 ? 'epic' : gear.tier === 2 ? 'rare' : 'uncommon',
        volume: gear.volume,
        price: 0,
      }
    : null;
}

/** Картинка предмета в космосе: у снаряжения (M11) — иконка пушки или модуля. */
export function lootSprite(rules: LootRules, id: string): string | null {
  return rules.gear?.[id]?.sprite ?? null;
}

/** Предмет — снаряжение, а не груз: подобранное уходит на склад, но в доке, а не сразу. */
export function isGear(rules: LootRules, id: string): boolean {
  return !rules.items?.[id] && !!rules.gear?.[id];
}

/** Название стопки: «Металл ×5». Неизвестный предмет показываем как есть, чтобы не терять его. */
export function lootLabel(rules: LootRules, id: string, count: number): string {
  const name = lootItem(rules, id)?.name ?? id;
  return count > 1 ? `${name} ×${count}` : name;
}

export function rarityColor(rules: LootRules, id: string): number {
  return RARITY_COLORS[lootItem(rules, id)?.rarity ?? 'common'];
}
