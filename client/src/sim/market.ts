// Зеркало server/Sro.Sim/MarketRules.cs: цены рынка станции (GDD §22, §27; M12).
// Цена шагает по единицам, поэтому предпросмотр «Купить 10 · 1 240 кр» считается тем же циклом,
// что и сделка на сервере, — иначе кнопка обещала бы одно, а списывалось бы другое.

import type { RumourDto } from '../net/protocol';

/** Профиль станции: что она делает и что скупает. */
export interface MarketStation {
  produces?: string[] | null;
  consumes?: string[] | null;
}

/** Товар на рынке: рыночное о нём; название, объём и базовая цена — в loot.json. */
export interface MarketGood {
  /** Норма запаса на обычной станции, штук. Чем меньше, тем резче ходит цена. */
  baseline?: number;
  /** Регионы, где товар вне закона. */
  illegal?: string[] | null;
}

/** Правила рынка этой станции; приходят в welcome и config. */
export interface MarketRules {
  spread?: number;
  elasticity?: number;
  minFactor?: number;
  maxFactor?: number;
  stockFloor?: number;
  stockCap?: number;
  produceMul?: number;
  consumeMul?: number;
  illegalMul?: number;
  produceStock?: number;
  consumeStock?: number;
  baseline?: number;
  goods?: Record<string, MarketGood> | null;
  /** Профили всех станций: по ним карта галактики показывает, что где производят. */
  stations?: Record<string, MarketStation> | null;
  /** Профиль этой станции; нет — здесь не торгуют. */
  station?: MarketStation | null;
  /** Регион этой системы: по нему видно, что тут вне закона. */
  region?: string | null;
}

/** Рынка нет: до welcome и на серверах без market.json. */
export const NO_MARKET: MarketRules = {};

const SPREAD = 0.18;
const ELASTICITY = 0.6;
const MIN_FACTOR = 0.45;
const MAX_FACTOR = 2.2;
const STOCK_FLOOR = 0.08;
const PRODUCE_MUL = 0.7;
const CONSUME_MUL = 1.45;
const ILLEGAL_MUL = 1.6;
const PRODUCE_STOCK = 2.5;
const CONSUME_STOCK = 0.5;
const BASELINE = 100;

/** Что станция делает с этим товаром. */
export type MarketRole = 'produces' | 'consumes' | 'neutral';

/** Торгуют ли здесь хоть чем-нибудь. */
export function hasMarket(rules: MarketRules): boolean {
  return !!rules.station && Object.keys(rules.goods ?? {}).length > 0;
}

export function role(rules: MarketRules, good: string): MarketRole {
  const station = rules.station;
  if (!station) return 'neutral';
  if (station.produces?.includes(good)) return 'produces';
  if (station.consumes?.includes(good)) return 'consumes';
  return 'neutral';
}

/** Товар вне закона в этом регионе: здесь его не купить и не продать. */
export function isIllegal(rules: MarketRules, good: string): boolean {
  const region = rules.region;
  return !!region && !!rules.goods?.[good]?.illegal?.includes(region);
}

/** Берут ли здесь этот товар: скупает станция всё, чем торгует. */
export function trades(rules: MarketRules, good: string): boolean {
  return hasMarket(rules) && !!rules.goods?.[good] && !isIllegal(rules, good);
}

/** Продаёт ли станция этот товар: продаёт она только то, что делает сама. */
export function sells(rules: MarketRules, good: string): boolean {
  return trades(rules, good) && role(rules, good) === 'produces';
}

/** Равновесный запас товара здесь, штук. */
export function norm(rules: MarketRules, good: string): number {
  const baseline = rules.goods?.[good]?.baseline || (rules.baseline ?? BASELINE);
  const r = role(rules, good);
  const factor =
    r === 'produces' ? (rules.produceStock ?? PRODUCE_STOCK) : r === 'consumes' ? (rules.consumeStock ?? CONSUME_STOCK) : 1;
  return baseline * factor;
}

/** Уровень цены: производитель отдаёт дешевле нормы, потребитель платит дороже. */
function level(rules: MarketRules, good: string): number {
  const r = role(rules, good);
  const base = r === 'produces' ? (rules.produceMul ?? PRODUCE_MUL) : r === 'consumes' ? (rules.consumeMul ?? CONSUME_MUL) : 1;
  return isIllegal(rules, good) ? base * (rules.illegalMul ?? ILLEGAL_MUL) : base;
}

/** Справедливая цена штуки при таком запасе; зажата, чтобы пустой склад не просил бесконечность. */
export function mid(rules: MarketRules, good: string, basePrice: number, stock: number): number {
  const n = norm(rules, good);
  if (!(n > 0) || !(basePrice > 0)) return basePrice;
  const floor = Math.max(stock, (rules.stockFloor ?? STOCK_FLOOR) * n);
  const value = basePrice * level(rules, good) * Math.pow(n / floor, rules.elasticity ?? ELASTICITY);
  const min = basePrice * (rules.minFactor ?? MIN_FACTOR);
  const max = basePrice * (rules.maxFactor ?? MAX_FACTOR);
  return Math.min(Math.max(value, min), max);
}

/** Сколько пилот получает за штуку. */
export function sellPrice(rules: MarketRules, good: string, basePrice: number, stock: number): number {
  const spread = rules.spread ?? SPREAD;
  const price = Math.floor(mid(rules, good, basePrice, stock) * (1 - spread / 2) + 1e-9);
  return Math.max(price, basePrice > 0 ? 1 : 0);
}

/** Сколько пилот платит за штуку; всегда строго дороже, чем станция выкупает. */
export function buyPrice(rules: MarketRules, good: string, basePrice: number, stock: number): number {
  const spread = rules.spread ?? SPREAD;
  const price = Math.ceil(mid(rules, good, basePrice, stock) * (1 + spread / 2) - 1e-9);
  return Math.max(price, sellPrice(rules, good, basePrice, stock) + 1);
}

/** Вся сделка: цена шагает по единицам, поэтому крупная пачка идёт по другой цене, чем первая штука. */
export function tradeCost(
  rules: MarketRules,
  good: string,
  basePrice: number,
  stock: number,
  count: number,
  buying: boolean,
): number {
  let credits = 0;
  let left = stock;
  for (let i = 0; i < count; i++) {
    credits += buying ? buyPrice(rules, good, basePrice, left) : sellPrice(rules, good, basePrice, left);
    left = Math.max(0, left + (buying ? -1 : 1));
  }
  return credits;
}

/** На сколько штук из max хватит кредитов — тем же шагом, что и сделка. */
export function affordable(
  rules: MarketRules,
  good: string,
  basePrice: number,
  stock: number,
  max: number,
  credits: number,
): number {
  let spent = 0;
  let left = stock;
  for (let i = 0; i < max; i++) {
    const unit = buyPrice(rules, good, basePrice, left);
    if (spent + unit > credits) return i;
    spent += unit;
    left = Math.max(0, left - 1);
  }
  return Math.max(0, max);
}

/** Насколько цена расходится с обычной: ▲ дороже, ▼ дешевле, пусто — как везде. */
export type PriceTrend = 'up' | 'down' | 'even';

/** Мёртвая зона: колебание меньше этой доли базовой цены не стоит стрелки. */
const TREND_BAND = 0.1;

export function trend(buy: number, sell: number, basePrice: number): PriceTrend {
  if (!(basePrice > 0)) return 'even';
  const here = (buy + sell) / 2;
  if (here > basePrice * (1 + TREND_BAND)) return 'up';
  if (here < basePrice * (1 - TREND_BAND)) return 'down';
  return 'even';
}

/** Много ли на складе: подсказка «мало / норма / много» рядом с ценой. */
export type StockLevel = 'low' | 'normal' | 'high';

export function stockLevel(stock: number, normStock: number): StockLevel {
  if (!(normStock > 0)) return 'normal';
  if (stock < normStock * 0.5) return 'low';
  if (stock > normStock * 1.5) return 'high';
  return 'normal';
}

/**
 * Слухи торговца (M12): он же и подсказка, что взять и куда везти. Сервер присылает факты,
 * текст собираем здесь — как у заданий (sim/missions.ts).
 */

/** Чем объясняют нехватку: у каждого товара своя беда, и от этого слух звучит по-человечески. */
const SCARCITY: Record<string, string> = {
  medicine: 'там эпидемия',
  food: 'там голодают',
  fuelCells: 'там сидят без энергии',
  machinery: 'у них всё сломалось и чинить нечем',
  arms: 'к ним ходят пираты',
  luxury: 'их начальство скучает',
  metal: 'у них встала стройка',
  ore: 'их рудники выдохлись',
  titanium: 'верфь стоит без титана',
  crystals: 'их реакторы на последнем кристалле',
  rareMetal: 'им нечем чинить технику',
  energy: 'у них садятся батареи',
  tech: 'их плазменные узлы на ладан дышат',
};

const JUMPS = ['здесь же', 'в одном прыжке', 'в двух прыжках', 'в трёх прыжках', 'в четырёх прыжках', 'в пяти прыжках'];

function jumps(hops: number): string {
  return JUMPS[hops] ?? `в ${hops} прыжках`;
}

/**
 * Что говорит торговец. Слух — это подсказка: «возьмите здесь X и везите в Y».
 * @param good название товара из loot.json
 */
export function rumourLine(rumour: RumourDto, good: string): string {
  const where = `${rumour.name} (${jumps(rumour.hops)})`;
  if (rumour.kind === 'glut') {
    return `В ${where} завал: ${good.toLowerCase()} отдают по ${rumour.price} кр. Сходить бы туда порожняком.`;
  }
  const why = rumour.scarce ? SCARCITY[rumour.good] : null;
  const profit = rumour.profit ? `, это ${rumour.profit} кр с штуки` : '';
  return why
    ? `Говорят, ${why}: в ${where} за ${good.toLowerCase()} дают ${rumour.price} кр${profit}. Берите здесь и везите.`
    : `В ${where} за ${good.toLowerCase()} дают ${rumour.price} кр${profit} — берите здесь и везите туда.`;
}
