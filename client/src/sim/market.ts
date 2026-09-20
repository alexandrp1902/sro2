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
  /** Профили всех мест по ключу места («st:sol», «pl:terra»): по ним карта галактики показывает, что где производят. */
  places?: Record<string, MarketStation> | null;
  /** Профиль этого места; нет — здесь не торгуют. */
  station?: MarketStation | null;
  /** Регион этой системы: по нему видно, что тут вне закона. */
  region?: string | null;
  /** Спрос события (M15.5): что здесь просят и во сколько раз дороже. */
  demand?: MarketDemand | null;
}

/** Спрос события на этом месте (M15.5). */
export interface MarketDemand {
  goods: string[];
  mul: number;
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
  // Зеркало Sro.Sim/MarketRules.Mid: событие спроса (M15.5) поднимает и цену, и потолок.
  // Разойтись тут нельзя — кнопка «Купить N · X кр» считается этой же формулой.
  const boost = demandMul(rules, good);
  const value = basePrice * level(rules, good) * Math.pow(n / floor, rules.elasticity ?? ELASTICITY) * boost;
  const min = basePrice * (rules.minFactor ?? MIN_FACTOR);
  const max = basePrice * (rules.maxFactor ?? MAX_FACTOR) * boost;
  return Math.min(Math.max(value, min), max);
}

/** Во сколько раз событие подняло цену этого товара; 1 — событие не про него или его нет. */
export function demandMul(rules: MarketRules, good: string): number {
  const demand = rules.demand;
  if (!demand || !(demand.mul > 1) || !demand.goods.includes(good)) return 1;
  return demand.mul;
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

/**
 * Как товар называют в винительном падеже: «берут руду», а не «берут руда». Совпадающие с именительным
 * формы не перечисляем — их даёт запасной вариант. Заодно пара товаров звучит в торговой речи во
 * множественном: возят не «энергоблок», а энергоблоки.
 */
const GOODS_ACC: Record<string, string> = {
  ore: 'руду',
  energy: 'энергоблоки',
  tech: 'плазменные компоненты',
};

/** Товар в винительном падеже, с маленькой буквы; незнакомый — как прислали. */
function acc(rumour: RumourDto, good: string): string {
  return GOODS_ACC[rumour.good] ?? good.toLowerCase();
}

function capitalize(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/**
 * Что говорит торговец — не сводка, а слух: он не докладывает, а делится тем, что слышал, и сам
 * не ручается. Цену, выгоду и расстояние сервер считает (RumourDto.price, profit, hops) и по ним же
 * выбирает, о чём судачить, но вслух их не произносят: система и товар названы верно, а сколько туда
 * прыжков и почём там берут, пилот посмотрит по карте и на месте.
 *
 * Название системы стоит отдельным словом, без предлога: склонять его пришлось бы по-разному для
 * «Кастора» и для «Vega», а так фраза цела с любым именем.
 *
 * @param good название товара из loot.json
 */
export function rumourLine(rumour: RumourDto, good: string): string {
  const what = acc(rumour, good);
  if (rumour.kind === 'glut') {
    return `${rumour.name}. Там, болтают, ${what} девать некуда — отдают чуть не даром. Сходить бы порожняком.`;
  }
  const why = rumour.scarce ? SCARCITY[rumour.good] : null;
  return why
    ? `${rumour.name}. ${capitalize(why)}, если не врут. ${capitalize(what)} туда возят не зря.`
    : `${rumour.name}. Оттуда, слышно, возвращаются довольные — те, кто вёз ${what}.`;
}
