// Зеркало server/Sro.Sim/MarketRules.cs: цены рынка станции (GDD §22, §27; M12).
// Цена шагает по единицам, поэтому предпросмотр «Купить 10 · 1 240 кр» считается тем же циклом,
// что и сделка на сервере, — иначе кнопка обещала бы одно, а списывалось бы другое.

import type { RumourDto } from '../net/protocol';
import { pick } from './staff';

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

/**
 * Почему туда везут: у каждого товара несколько поводов, и торговец пересказывает один из них.
 * Каждый повод — придаточное без рода говорящего: встаёт после «Говорят,» и после «Пилоты рассказывают,».
 * scarce — беда (товара там мало), plain — затея (товар там просто нужен), glut — откуда излишек.
 */
interface Reasons {
  scarce: readonly string[];
  plain: readonly string[];
  glut: readonly string[];
}

const REASONS: Record<string, Reasons> = {
  medicine: {
    scarce: ['там эпидемия', 'там вспышка лихорадки и лазареты забиты', 'у них карантин, и аптечки на вес золота'],
    plain: ['там открыли новый госпиталь', 'у них набирают колонистов, а всех прививают на входе', 'на их рудниках ввели медосмотры', 'там строят санаторий для шахтёров'],
    glut: ['там фармацевтический цех работает в три смены', 'у них склады забиты гуманитарной помощью, которую так и не раздали'],
  },
  food: {
    scarce: ['там голодают', 'у них погибли теплицы', 'у них сорвался урожай, и пайки урезали'],
    plain: ['туда пригнали целую смену строителей, а кормить их нечем', 'у них праздник урожая, только урожай не свой', 'там растёт колония, ртов всё больше', 'к ним пришёл флот на стоянку, экипажи едят за троих'],
    glut: ['там собрали небывалый урожай', 'у них новые гидропонные фермы, девать продукты некуда'],
  },
  fuelCells: {
    scarce: ['там сидят без энергии', 'у них заглох реактор, живут на аварийном', 'у них перебои с питанием, свет по графику'],
    plain: ['там запустили новый завод, и он жрёт энергию', 'у них на зиму готовят запасы', 'там расширяют причалы, а краны работают на ячейках'],
    glut: ['там запустили новую электростанцию', 'у них ячейки штампуют быстрее, чем расходуют'],
  },
  machinery: {
    scarce: ['у них всё сломалось и чинить нечем', 'у них встали конвейеры', 'там половина техники на запчасти разобрана'],
    plain: ['там строят завод, им нужны машины', 'у них открывают новый рудник', 'там закладывают космодром', 'у них расширяют доки, всё оборудование в дело идёт'],
    glut: ['там закрылся большой подряд и технику распродают', 'у них машиностроительный завод работает без передышки'],
  },
  arms: {
    scarce: ['к ним ходят пираты', 'у них пираты перерезали торговый путь', 'там после налёта арсенал пустой'],
    plain: ['там формируют ополчение', 'у них усиливают гарнизон', 'там проводят учения рейнджеров', 'у них открылся стрелковый клуб, и все вдруг стали охотниками'],
    glut: ['там расформировали гарнизон и сдают арсенал', 'у них оружейная мастерская завалила склад'],
  },
  luxury: {
    scarce: ['их начальство скучает', 'там у губернатора юбилей, а подарков нет', 'у них богатые гости, а угощать нечем'],
    plain: ['там проводят гонки, и публика съезжается при деньгах', 'у них соревнования пилотов, нужна роскошь для призов', 'там у губернатора свадьба', 'у них открыли казино', 'туда прилетает делегация из центра, будут пускать пыль в глаза'],
    glut: ['там разорился один богач и распродаёт добро', 'у них ярмарка кончилась, и купцы остались с товаром'],
  },
  metal: {
    scarce: ['у них встала стройка', 'там после аварии латают обшивку чем попало', 'у них плавильня остановилась'],
    plain: ['там строят новый жилой модуль', 'у них расширяют станцию', 'там закладывают второй док', 'у них ремонтируют причальное кольцо'],
    glut: ['там новый прокатный стан', 'у них разобрали старую станцию на металл'],
  },
  ore: {
    scarce: ['их рудники выдохлись', 'у них обвал на шахте', 'их плавильни простаивают без сырья'],
    plain: ['там запустили новую плавильню', 'у них открыли литейный цех', 'там переплавляют всё подряд под большой заказ'],
    glut: ['там открыли богатую жилу', 'у них новый рудник', 'туда пригнали рудовозы с пояса астероидов'],
  },
  titanium: {
    scarce: ['верфь стоит без титана', 'у них броню латать нечем', 'их верфь сорвала заказ, ищут сырьё'],
    plain: ['там запустили производство корпусов, им нужно больше титана', 'у них верфь получила военный заказ', 'там строят новые стапели', 'у них бронируют станцию после налётов'],
    glut: ['там нашли титановую жилу', 'у них верфь закрыли, а запасы остались'],
  },
  crystals: {
    scarce: ['их реакторы на последнем кристалле', 'у них сели накопители', 'их лаборатория встала без сырья'],
    plain: ['там строят новый реактор', 'у них лаборатория ставит опыты', 'там запускают линию лазерной оптики', 'у них мастерят навигационные маяки'],
    glut: ['там наткнулись на богатую друзу', 'у них копают без остановки, а обрабатывать некому'],
  },
  rareMetal: {
    scarce: ['им нечем чинить технику', 'у них электроника сыпется', 'их мастерские стоят без сырья'],
    plain: ['там открыли приборостроительный цех', 'у них собирают сенсоры для флота', 'там делают щитовые генераторы на заказ'],
    glut: ['там нашли богатую жилу', 'у них рудовоз застрял на складе, и груз продают'],
  },
  energy: {
    scarce: ['у них садятся батареи', 'у них отключают отсеки, чтобы сберечь заряд', 'там генератор на ремонте'],
    plain: ['там запустили производство, и оно жрёт энергию', 'у них строят энергощит вокруг станции', 'там проводят гонки, и зарядные станции не справляются'],
    glut: ['там поставили новые солнечные поля', 'у них заряд копится быстрее, чем расходуется'],
  },
  tech: {
    scarce: ['их плазменные узлы на ладан дышат', 'у них двигатели встали без запчастей', 'их ремонтники сидят без деталей'],
    plain: ['там собирают новые двигатели', 'у них переоснащают патрульное крыло', 'там строят исследовательскую лабораторию', 'у них заказали модернизацию всего флота'],
    glut: ['там завод выпустил лишнюю партию', 'у них устаревшие узлы списывают'],
  },
};

/** Повод для товара, которого нет в списке: слух без подробностей всё равно слух. */
const ANY_REASONS: Reasons = {
  scarce: ['у них этого не хватает', 'у них с этим совсем туго'],
  plain: ['там затеяли что-то большое', 'у них новый заказчик с толстым кошельком', 'там идёт стройка'],
  glut: ['там этого навалом', 'у них склады ломятся'],
};

/** Как торговец начинает фразу; дальше — повод. */
const OPENERS = ['Говорят', 'Болтают', 'Слух идёт', 'Пилоты рассказывают', 'В баре судачат', 'По эфиру передавали', 'Мне шепнули'];

/** Чем кончается совет: {what} — товар в винительном падеже, {What} — он же с большой буквы. */
const ROUTE_CLOSERS = [
  '{What} туда возят не зря.',
  'Кто везёт туда {what}, внакладе не остаётся.',
  'Так что {what} там берут охотно.',
  '{What} у них с руками оторвут.',
  'Будь трюм побольше, туда бы только {what} и возить.',
  'Оттуда возвращаются довольные — те, кто вёз {what}.',
];

const SCARCE_CLOSERS = [
  'Если не врут, {what} туда возят не зря.',
  'Если не врут, за {what} там заплатят не торгуясь.',
  '{What} там сейчас нужнее воздуха — если не врут.',
];

const GLUT_CLOSERS = [
  '{What} отдают чуть не даром. Сходить бы порожняком.',
  'Девать {what} некуда — отдают чуть не даром.',
  'Лететь туда стоит порожняком: {what} отдают чуть не даром.',
];

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
 * Повод и оборот речи выбираются по зерну: у разных торговцев про одно и то же — разные истории,
 * а у одного — одна и та же, сколько вкладку ни открывай.
 *
 * Название системы стоит отдельным словом, без предлога: склонять его пришлось бы по-разному для
 * «Кастора» и для «Vega», а так фраза цела с любым именем.
 *
 * @param good название товара из loot.json
 * @param seed кто рассказывает — обычно ключ места
 */
export function rumourLine(rumour: RumourDto, good: string, seed = ''): string {
  const what = acc(rumour, good);
  const key = `${seed}|${rumour.kind}|${rumour.system}|${rumour.good}`;
  const kind = rumour.kind === 'glut' ? 'glut' : rumour.scarce ? 'scarce' : 'plain';
  const reason = pick((REASONS[rumour.good] ?? ANY_REASONS)[kind], `${key}|why`);
  const opener = pick(OPENERS, `${key}|who`);
  const closers = kind === 'glut' ? GLUT_CLOSERS : kind === 'scarce' ? SCARCE_CLOSERS : ROUTE_CLOSERS;
  const closer = pick(closers, `${key}|end`).replace('{What}', capitalize(what)).replace('{what}', what);
  return `${rumour.name}. ${opener}, ${reason}. ${closer}`;
}
