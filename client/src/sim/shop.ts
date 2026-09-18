// Зеркало server/Sro.Sim/ShopRules.cs: цены станции (GDD §26, §30, §50).

export interface ShopRules {
  /** Кредитов у нового пилота (GDD §54). */
  startCredits: number;
  /** Кредитов за единицу прочности корпуса при ремонте; 0 — бесплатно. */
  repairPrice: number;
  /** Корпус — цена; чего здесь нет, то не продаётся. Сервер может прислать null. */
  hulls?: Record<string, number> | null;
  /** Пушка — цена. */
  weapons?: Record<string, number> | null;
}

/** Магазина нет: до welcome и на серверах без shop.json. */
export const NO_SHOP: ShopRules = { startCredits: 0, repairPrice: 0, hulls: null, weapons: null };

/** Цена корпуса или пушки; null — не продаётся. */
export function price(prices: Record<string, number> | null | undefined, id: string): number | null {
  return prices?.[id] ?? null;
}

/** Ремонт до полной прочности — как на сервере: округлено вверх. */
export function repairCost(shop: ShopRules, missingHp: number): number {
  return missingHp > 0 ? Math.ceil(missingHp * shop.repairPrice) : 0;
}

/** «1 800 кр»: тысячи через пробел, как принято в русском тексте. */
export function formatCredits(credits: number): string {
  return `${Math.round(credits).toLocaleString('ru-RU')} кр`;
}
