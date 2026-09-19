// Зеркало server/Sro.Sim/ShopRules.cs: цены станции (GDD §26, §30, §50).

export interface ShopRules {
  /** Кредитов у нового пилота (GDD §54). */
  startCredits: number;
  /** Кредитов за единицу прочности корпуса при ремонте; 0 — бесплатно. */
  repairPrice: number;
  /** Корпус — цена; чего здесь нет, то не продаётся. Сервер может прислать null. */
  hulls?: Record<string, number> | null;
  /** Пушка или модуль — цена. */
  items?: Record<string, number> | null;
  /** Кредитов за единицу топлива при заправке (GDD §6); 0 или нет поля — бесплатно. */
  fuelPrice?: number;
  /** Доля цены, за которую станция выкупает пушку или модуль со склада. */
  sellShare?: number;
  /** Что продают именно здесь (M11); нет — продаётся всё, что в прайсе. */
  stock?: string[] | null;
  /** Подпись магазина станции: «Военная станция Nova». */
  title?: string | null;
}

/** Магазина нет: до welcome и на серверах без shop.json. */
export const NO_SHOP: ShopRules = { startCredits: 0, repairPrice: 0, hulls: null, items: null };

/** Сколько станция даёт за пушку или модуль со склада — как на сервере: округлено вниз; не продаётся — даром. */
export function sellPrice(shop: ShopRules, id: string): number {
  const cost = price(shop.items, id);
  return cost === null ? 0 : Math.floor(cost * (shop.sellShare ?? 0.5) + 1e-9);
}

/** Цена корпуса или пушки; null — нет в прайсе. */
export function price(prices: Record<string, number> | null | undefined, id: string): number | null {
  return prices?.[id] ?? null;
}

/**
 * Продают ли это здесь (M11): у каждой станции свой ассортимент. Цена есть на всё, что знает сервер, —
 * по ней принимают со склада, — но купить можно только из stock.
 */
export function sells(shop: ShopRules, id: string, prices: Record<string, number> | null | undefined): boolean {
  return price(prices, id) !== null && (!shop.stock || shop.stock.includes(id));
}

/** Ремонт до полной прочности — как на сервере: округлено вверх. */
export function repairCost(shop: ShopRules, missingHp: number): number {
  return missingHp > 0 ? Math.ceil(missingHp * shop.repairPrice) : 0;
}

/** Заправка до полного бака — как на сервере: округлено вверх. */
export function fuelCost(shop: ShopRules, missingFuel: number): number {
  return missingFuel > 0 ? Math.ceil(missingFuel * (shop.fuelPrice ?? 0) - 1e-9) : 0;
}

/** «1 800 кр»: тысячи через пробел, как принято в русском тексте. */
export function formatCredits(credits: number): string {
  return `${Math.round(credits).toLocaleString('ru-RU')} кр`;
}
