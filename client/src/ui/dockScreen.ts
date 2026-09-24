import type {
  BuyKind,
  DemandQuoteDto,
  HangarMsg,
  MarketItemDto,
  MarketMsg,
  MissionOffer,
  MissionsMsg,
  PlaceDto,
  RepChangeDto,
  RepMsg,
} from '../net/protocol';
import type { WeaponParams } from '../sim/combat';
import {
  MODULE_SLOTS,
  REQUIRED_SLOTS,
  SLOT_NAMES,
  UTILITY,
  canInstall,
  describeFitProblem,
  fitGet,
  hullSlots,
  hullUtilitySlots,
  moduleLabel,
  tierBadge,
  utilityIndex,
  utilitySlot,
  weaponIndex,
  weaponSlot,
  type FitProblem,
  type Modules,
} from '../sim/fitting';
import { keyHint, keymap } from '../input/keymap';
import { hops, type GalaxyDto } from '../sim/galaxy';
import { gearIcon, hasSprite, moduleSprite, shipSprite, spriteUrl, weaponSprite } from '../render/sprites';
import type { Hulls } from '../sim/hulls';
import type { HullParams } from '../sim/movement';
import { activeHint, activeLine, offerNote, offerTitle, timeLeft, type MissionNames } from '../sim/missions';
import { isStory, lootItem, rarityColor, type LootRules } from '../sim/loot';
import { NO_MARKET, affordable, rumourLine, stockLevel, tradeCost, trend, type MarketRules } from '../sim/market';
import { staffOf, type StaffRole } from '../sim/staff';
import {
  NO_REP,
  allowsLevel,
  levelById,
  levelColor,
  levelOf,
  priceMul,
  repLabel,
  repPrice,
  type ReputationRules,
} from '../sim/reputation';
import { NO_SHOP, formatCredits, price, repairCost, sells, sellPrice, transportCost, type ShopRules } from '../sim/shop';
import type { Weapons } from '../sim/weapons';
import { color, round, type CargoState } from './cargoHud';

/** Что можно сделать с корпусом или пушкой на витрине. */
export type OfferState =
  /** Стоит на корабле. */
  | 'active'
  /** Своё, в ангаре: можно поставить. */
  | 'owned'
  /** Продаётся, кредитов хватает. */
  | 'buy'
  /** Продаётся, но кредитов не хватает. */
  | 'poor'
  /** Продаётся, но только своим: не хватает репутации места (M13). */
  | 'locked'
  /** Не продаётся. Такую строку с M15.6 не рисуют вовсе — ни у корпусов, ни у снаряжения. */
  | 'none';

export function offerState(
  owned: boolean,
  active: boolean,
  cost: number | null,
  credits: number,
  locked = false,
): OfferState {
  if (active) return 'active';
  if (owned) return 'owned';
  if (cost === null) return 'none';
  // Замок важнее кошелька: копить на то, что тебе всё равно не продадут, — ложная цель.
  if (locked) return 'locked';
  return cost <= credits ? 'buy' : 'poor';
}

/** Ниже этого остатка срок письма краснеет (M14). */
const LOW_TIMER_SECONDS = 60;

export type Tab = 'missions' | 'cargo' | 'hulls' | 'ships' | 'fitting';

const TABS: { id: Tab; label: string }[] = [
  { id: 'missions', label: 'Задания' },
  // Покупка и продажа товаров — в одном списке (M12); id остался прежним, на него смотрит startTab.
  { id: 'cargo', label: 'Рынок' },
  { id: 'hulls', label: 'Корабли' },
  // Ярлык короткий: пять вкладок с «Мой ангар» не влезают в 360 px. Полное имя стоит в теле вкладки.
  { id: 'ships', label: 'Ангар' },
  { id: 'fitting', label: 'Модули' },
];

/** Где стоит корабль: станция или поселение на планете — от этого фон сцены (M15). */
export type Place = 'station' | 'planet';

/** Вид места из ключа: «pl:terra» — поселение, всё прочее — станция. */
export function placeKind(key: string | null | undefined): Place {
  return key?.startsWith('pl:') ? 'planet' : 'station';
}

/**
 * Сцена слева на ПК: у каждой вкладки своё место в доке и свой собеседник. Кто он и что говорит —
 * у каждого места своё (sim/staff.ts): диспетчер «Веги» не тот, что в «Касторе».
 */
interface Scene {
  /** Имя картинки фона: public/dock/{place}-{art}.webp. */
  art: string;
  /** Кто говорит в подписи; нет — подписи нет. */
  who: StaffRole | null;
  /** Показывать ли свой корабль поверх фона. */
  ship: boolean;
}

const SCENES: Record<Tab, Scene> = {
  missions: { art: 'office', who: 'missions', ship: false },
  cargo: { art: 'trader', who: 'cargo', ship: false },
  hulls: { art: 'shipyard', who: 'hulls', ship: true },
  ships: { art: 'hangar', who: 'ships', ship: true },
  fitting: { art: 'hangar', who: null, ship: true },
};

/**
 * Свои сцены дока у отдельных мест: какие вкладки нарисованы для набора. Чего в наборе нет,
 * берётся общая сцена места — наборы дорисовываются по одной картинке, а не пачкой.
 * Земное поселение своего набора не имеет: общие сцены планеты (planet-*) и есть земные.
 */
const SCENE_SETS: Record<string, readonly string[]> = {
  // Станции (пачка G): свои диспетчер и торговец у каждой, верфь и ангар общие.
  ring: ['office', 'trader'],
  trade: ['office', 'trader'],
  habitat: ['office', 'trader'],
  'habitat-rim': ['office', 'trader'],
  fortress: ['office', 'trader'],
  mining: ['office', 'trader'],
  outpost: ['office', 'trader'],
  ranger: ['office', 'trader'],
  desert: ['office', 'trader', 'shipyard', 'hangar'],
  ice: ['office', 'trader', 'shipyard', 'hangar'],
  jungle: ['office', 'trader', 'shipyard', 'hangar'],
  lava: ['office', 'trader', 'shipyard', 'hangar'],
  barren: ['office', 'trader', 'shipyard', 'hangar'],
  'orbital-platform': ['office', 'trader', 'shipyard', 'hangar'],
};

/**
 * Имя картинки сцены без пути и расширения («ring-office»): по нему же sim/staff.ts понимает,
 * кто на ней нарисован, чтобы имя в подписи совпадало с лицом.
 */
export function sceneArt(place: Place, tab: Tab, set?: string | null): string {
  const art = SCENES[tab].art;
  return set && SCENE_SETS[set]?.includes(art) ? `${set}-${art}` : `${place}-${art}`;
}

/** Адрес фона сцены: относительный, как и спрайты. */
export function sceneUrl(place: Place, tab: Tab, set?: string | null): string {
  return `dock/${sceneArt(place, tab, set)}.webp`;
}

/** Что можно сделать с пушкой или модулем для выбранного слота. */
export type SlotOffer =
  /** Уже стоит в этом слоте. */
  | { action: 'installed' }
  /** Лежит на складе (у гостя — всё): поставить. */
  | { action: 'install'; problem: FitProblem | null }
  /** Купить и сразу поставить. */
  | { action: 'buy'; cost: number; problem: FitProblem | null; poor: boolean }
  /** Не продаётся и на складе нет. */
  | { action: 'none' };

/**
 * Предложение для слота: стоит ли уже, есть ли на складе, можно ли купить, встанет ли по классу и энергии.
 * @param stored сколько такого на складе; гостю — всё бесконечно
 */
export function slotOffer(
  installed: boolean,
  stored: number,
  cost: number | null,
  credits: number,
  problem: FitProblem | null,
): SlotOffer {
  if (installed) return { action: 'installed' };
  if (stored > 0) return { action: 'install', problem };
  if (cost === null) return { action: 'none' };
  return { action: 'buy', cost, problem, poor: cost > credits };
}

/** Строка рынка: что с этим товаром можно сделать здесь и сейчас. */
export interface MarketRow {
  id: string;
  /** Сколько лежит в трюме. */
  have: number;
  /** Цены станции; null — этим здесь не торгуют (чужой регион или контрабанда). */
  quote: MarketItemDto | null;
  /** Станция продаёт этот товар: покупают только то, что она делает сама. */
  sells: boolean;
  /** Сколько штук выбрано счётчиком. */
  qty: number;
  /** Максимум к покупке: склад, кошелёк и трюм; 0 — купить нельзя. */
  maxBuy: number;
  /** Максимум к продаже: сколько лежит в трюме. */
  maxSell: number;
}

/**
 * Сколько штук можно купить: меньшее из склада станции, места в трюме и того, что по карману.
 * Цена растёт по ходу сделки, поэтому кошелёк считается тем же шагом, что и сама покупка.
 */
/**
 * Строка события спроса над рынком (M15.5); null — здесь его нет.
 * Чистая: её и проверяют тесты — в клиенте они идут без DOM.
 */
export function demandLine(
  demand: DemandQuoteDto | null | undefined,
  name: (good: string) => string,
): string | null {
  if (!demand || demand.goods.length === 0) return null;
  const goods = demand.goods.map(name).join(' и ');
  const mul = demand.mul >= 10 ? Math.round(demand.mul) : Math.round(demand.mul * 10) / 10;
  return `${demand.title}: берут ${goods} по ×${mul} — осталось ${demand.left} из ${demand.quota}`;
}

export function maxBuyable(
  market: MarketRules,
  loot: LootRules,
  quote: MarketItemDto,
  credits: number,
  freeVolume: number,
): number {
  const volume = lootItem(loot, quote.id)?.volume ?? 1;
  const fits = volume > 0 ? Math.floor((freeVolume + 1e-9) / volume) : quote.stock;
  const room = Math.max(0, Math.min(quote.stock, fits));
  if (room <= 0) return 0;
  return affordable(market, quote.id, lootItem(loot, quote.id)?.price ?? 0, quote.stock, room, credits);
}

/**
 * Продаёт ли место этот товар пилоту (M16a). Решает сервер: что у него на складе есть и не заперто
 * заданием, то и продаётся. Старый сервер поля не шлёт — тогда считаем, что продаётся, а дальше
 * всё равно решает запас.
 */
export const sellsHere = (quote: MarketItemDto): boolean => quote.sells !== false;

/** Счётчик не уходит за границы: меньше одного и больше доступного выбрать нельзя. */
export function clampQty(qty: number, max: number): number {
  if (max <= 0) return 0;
  return Math.min(Math.max(1, Math.round(qty)), max);
}

export interface DockHandlers {
  /** Продать груз: item — что именно, без него — весь трюм; count — сколько штук, 0 — вся стопка. */
  onSell(item?: string, count?: number): void;
  /** Купить товар на рынке станции. */
  onBuyGoods(item: string, count: number): void;
  /** Купить корпус, пушку или модуль; slot — пушку или модуль сразу в этот слот. */
  onBuy(kind: BuyKind, id: string, slot?: string): void;
  /** Поставить свой корпус из ангара. */
  onEquip(id: string): void;
  /** Заказать перегон своего корпуса из другого дока сюда (M15.6). */
  onTransport(id: string): void;
  /** Поставить в слот со склада; id = null — снять на склад. */
  onFit(slot: string, id: string | null): void;
  /** Продать со склада пушку или модуль. */
  onSellItem(id: string): void;
  /** Продать со склада все модули разом (M16a): кнопка на рынке. */
  onSellGear(): void;
  onRepair(): void;
  onUndock(): void;
  /** Бургер-меню (M15.5): открыть под кнопкой, прямоугольник которой передан. */
  onMenu(anchor: DOMRect): void;
  /** Взять задание с доски. */
  onAccept(id: string): void;
  /** Бросить своё задание. */
  onAbandon(): void;
  /** Сдать «собрать». */
  onComplete(): void;
  onSkipTutorial(): void;
}

/**
 * С какой вкладки начать заход в док: идёт обучение или есть что сдать — с заданий, иначе с груза.
 * @param cargo что лежит в трюме — хватает ли на «собрать»
 */
export function startTab(missions: MissionsMsg | null, cargo: Record<string, number>): Tab {
  if (missions?.tutorial) return 'missions';
  const offer = missions?.active?.offer;
  if (offer?.kind === 'collect' && (cargo[offer.item ?? ''] ?? 0) >= offer.count) return 'missions';
  return 'cargo';
}

/** Сколько строк журнала репутации помнит экран. */
const REP_LOG_MAX = 10;

/** За что начислили или сняли — по коду с сервера. */
const REP_REASONS: Record<string, string> = {
  missionDone: 'задание выполнено',
  missionAbandon: 'задание брошено',
  pirate: 'пират уничтожен',
  sos: 'помощь торговцу',
  invasion: 'вторжение отбито',
  traderAttack: 'атака торговца',
  traderKill: 'торговец уничтожен',
  rangerAttack: 'атака рейнджера',
  rangerKill: 'рейнджер уничтожен',
  playerKill: 'убийство пилота',
};

/**
 * Плашка отношения: подпись, цвет и значок ступени. Значок зовётся по id ступени, так что
 * незнакомой ступени он просто не найдётся — плашка останется текстовой, как была до M13-арта.
 */
export function repChip(rules: ReputationRules, value: number): { text: string; color: string; icon: string | null } {
  const level = levelOf(rules, value);
  const mark = `rep-${level.id}`;
  return { text: repLabel(level, value), color: levelColor(level), icon: hasSprite(mark) ? mark : null };
}

/**
 * Значок ступени: белый силуэт, который CSS-маска красит в цвет плашки. Адрес абсолютный —
 * относительный в переменной считается от файла стилей, а он лежит в assets/ (как и фон сцены).
 */
export function repIcon(name: string): HTMLElement {
  const mark = el('span', 'rep-chip-icon');
  mark.style.setProperty('--rep-icon', `url("${new URL(spriteUrl(name), document.baseURI).href}")`);
  return mark;
}

/**
 * Строка журнала: «−15 Vega · атака торговца». Имя места приходит снаружи — здесь только ключ.
 * @param name как называется место из change.key; нет — пишем сам id
 */
export function repLogLine(change: RepChangeDto, name?: string | null): string {
  const where = name ?? change.key.slice(change.key.indexOf(':') + 1);
  const sign = change.delta > 0 ? '+' : '';
  const why = REP_REASONS[change.code] ?? change.code;
  return `${sign}${change.delta} ${where} · ${why}`;
}

/**
 * Почему товар недоступен; null — доступен или гейта нет.
 * @param level действующая ступень магазина, как её прислал сервер
 */
export function repGateNote(rules: ReputationRules, level: string | null | undefined, id: string, hull: boolean): string | null {
  if (allowsLevel(rules, level, id, hull)) return null;
  return `Только для уровня «${levelById(rules, rules.gate?.level).name}»`;
}

/**
 * Экран дока станции (GDD §26, §51): поверх мира, пока корабль пристыкован. Здесь продают груз,
 * покупают и меняют корпуса и пушки, чинятся. Рисуется по событиям сервера, а не каждый кадр.
 */
export class DockScreen {
  private tab: Tab = 'cargo';
  private hangar: HangarMsg | null = null;
  private cargo: CargoState | null = null;
  private loot: LootRules | null = null;
  private shop: ShopRules = NO_SHOP;
  /** Витрина из welcome — главного места системы: к ней возвращаемся, выйдя из дока (M15.6). */
  private welcomeShop: ShopRules = NO_SHOP;
  /** Заголовок экрана: «Станция Vega», «Поселение «Терра»». */
  private station = 'Станция';
  /** Голое имя места без слова «станция» — им подписываются задания и репутация. */
  private placeLabel: string | null = null;
  /** Ключ места, где стоит корабль (M15); null — место неизвестно. */
  private placeKey: string | null = null;
  private place: Place = 'station';
  /** Здесь продают и меняют корпуса; false — вкладки верфи нет (M15). */
  private shipyard = true;
  private here: string | null = null;
  private missions: MissionsMsg | null = null;
  /** Надпись обратного отсчёта у письма; null — взятого письма нет (M14). */
  private timer: HTMLElement | null = null;
  /** Слот, для которого открыт список пушек или модулей; null — ни один. */
  private slot: string | null = null;
  /** «Продать модули» нажата один раз и ждёт подтверждения вторым касанием (M16a). */
  private armedSellGear = false;
  /** Свой набор фонов дока у этого места; null — общие сцены по виду места. */
  private scene_: string | null = null;
  /** Что сказал сервер про место, где стоит корабль (M15); null — ещё не сказал. */
  private placeDto: PlaceDto | null = null;
  /** Карта галактики (M15.6): по ней ангар зовёт места по именам и считает прыжки до них. */
  private galaxy: GalaxyDto | null = null;
  /** Места галактики по ключу; null — ещё не собраны из карты. */
  private places: Map<string, { name: string; system: string; systemName: string }> | null = null;
  /** Имя станции этой системы и её набор сцен — запасной вариант, пока места нет. */
  private systemStation: string | null = null;
  private systemScene: string | null = null;
  /** Правила рынка этой станции (M12). */
  private market: MarketRules = NO_MARKET;
  private repRules: ReputationRules = NO_REP;
  /** Сколько корпуса в минуту чинит стоянка в доке (M15.7); 0 — бесплатной починки нет. */
  private mendRate = 0;
  private rep: RepMsg | null = null;
  /** Последние изменения репутации; живут на экране до перезахода — серверной истории нет. */
  private readonly repLog: RepChangeDto[] = [];
  /** Журнал развёрнут: состояние на экране, потому что render() пересобирает карточку целиком. */
  private repOpen = false;
  /** Живые цены станции; null — рынка здесь нет. */
  private quotes: MarketMsg | null = null;
  /**
   * Сколько штук выбрано счётчиком, по товарам. Живёт на экране, а не в разметке: render() заменяет
   * карточку целиком после каждого события сервера, и значение в поле ввода стиралось бы.
   */
  private readonly qty = new Map<string, number>();

  constructor(
    private readonly root: HTMLElement,
    private readonly hulls: Hulls,
    private readonly weapons: Weapons,
    private readonly modules: Modules,
    private readonly handlers: DockHandlers,
    private readonly names: MissionNames,
  ) {}

  get open(): boolean {
    return this.hangar?.docked ?? false;
  }

  setRules(
    loot: LootRules | undefined,
    shop: ShopRules | undefined,
    market?: MarketRules | null,
    reputation?: ReputationRules | null,
    mendRate?: number,
  ): void {
    this.loot = loot ?? null;
    this.welcomeShop = shop ?? NO_SHOP;
    this.shop = this.welcomeShop;
    this.market = market ?? NO_MARKET;
    this.repRules = reputation ?? NO_REP;
    this.mendRate = mendRate ?? 0;
    this.render();
  }

  /** Репутация пилота (M13); повод изменения копится в журнале. */
  setRep(rep: RepMsg | null): void {
    this.rep = rep;
    if (rep?.change) {
      this.repLog.unshift(rep.change);
      if (this.repLog.length > REP_LOG_MAX) this.repLog.length = REP_LOG_MAX;
    }
    this.render();
  }

  /**
   * Витрина места, где стоим (M15.6): ассортимент, цены и подпись здешние, а не главного места системы.
   * null — вернуться к витрине из welcome: так бывает при вылете, пока не пристыковались снова.
   */
  setShop(shop: ShopRules | null): void {
    this.shop = shop ?? this.welcomeShop;
    this.render();
  }

  /** Живые цены станции; null — рынка здесь нет, груз сдаётся по обычной цене. */
  setMarket(market: MarketMsg | null): void {
    this.quotes = market;
    this.render();
  }

  /**
   * Правила рынка этого места (M15.5). В welcome едет профиль главного места системы, поэтому на
   * поселении считать по нему нельзя, а спрос события и вовсе живёт только в market-сообщении.
   * Цену кнопки клиент считает той же формулой, что и сервер, — разойтись тут значит соврать пилоту.
   */
  private get local(): MarketRules {
    const quotes = this.quotes;
    if (!quotes?.station && !quotes?.demand) return this.market;
    return {
      ...this.market,
      station: quotes.station ?? this.market.station,
      demand: quotes.demand ? { goods: quotes.demand.goods, mul: quotes.demand.mul } : null,
    };
  }

  setCargo(state: CargoState): void {
    this.cargo = state;
    this.render();
  }

  /** null — связи нет: экран закрыт до нового ангара от сервера. */
  setHangar(hangar: HangarMsg | null): void {
    // Каждый заход в док начинается с груза — или с заданий, если там ждут.
    if (hangar?.docked && !this.open) {
      this.tab = startTab(this.missions, this.cargo?.items ?? {});
      this.armedSellGear = false;
    }
    this.hangar = hangar;
    this.render();
  }

  /**
   * Карта галактики (M15.6): ангару нужны имена чужих мест, их системы и число прыжков до них.
   * Приходит в welcome и дальше не меняется.
   */
  setGalaxy(galaxy: GalaxyDto | null): void {
    this.galaxy = galaxy;
    this.places = null;
    this.render();
  }

  /**
   * Система, в которой стоит корабль: по ней подписываются задания и берётся запасное имя места,
   * пока сервер не сказал точнее. scene — свой набор фонов дока у станции (M12).
   */
  setStation(name: string | null, system: string | null = null, scene: string | null = null): void {
    this.here = system;
    this.systemStation = name;
    this.systemScene = scene;
    this.applyPlace();
  }

  /**
   * Место, где стоит корабль (M15). Приходит в ангаре: заголовок, фон и набор вкладок — его,
   * а не системы, потому что в одной системе их теперь несколько.
   */
  setPlace(place: PlaceDto | null | undefined): void {
    this.placeDto = place ?? null;
    this.applyPlace();
  }

  /** Свести место и систему в то, что рисует экран. */
  private applyPlace(): void {
    const dto = this.placeDto;
    if (dto) {
      this.placeKey = dto.key;
      this.placeLabel = dto.name;
      this.place = placeKind(dto.key);
      this.station = this.place === 'planet' ? `Поселение «${dto.name}»` : `Станция ${dto.name}`;
      this.scene_ = dto.scene ?? null;
      this.shipyard = dto.shipyard;
    } else {
      // Сервер старше или корабль ещё не в доке: показываем станцию системы, как до M15.
      this.placeKey = this.here ? `st:${this.here}` : null;
      this.placeLabel = this.systemStation;
      this.place = 'station';
      this.station = this.systemStation ? `Станция ${this.systemStation}` : 'Станция';
      this.scene_ = this.systemScene;
      this.shipyard = true;
    }
    // Сели там, где верфи нет, а открыта была она: уводим на оснащение, иначе экран пустой.
    if (this.tab === 'hulls' && !this.shipyard) this.tab = 'fitting';
    this.render();
  }

  /** Обучение, взятое задание и доска этой станции. */
  setMissions(missions: MissionsMsg | null): void {
    // Новичок входит сразу в док, а задания приходят следом за ангаром — тогда и открываем их вкладку.
    if (this.open && missions?.tutorial && !this.missions?.tutorial) this.tab = 'missions';
    this.missions = missions;
    this.render();
  }

  /** Корпуса или пушки поменялись в балансе на лету. */
  refresh(): void {
    this.render();
  }

  /**
   * Срок письма идёт и в доке (M14). Перерисовывать ради него весь экран нельзя — он живёт событиями,
   * а не кадрами, поэтому обновляется одна надпись. Зовётся из кадрового цикла.
   */
  tick(now: number): void {
    if (!this.timer) return;
    const until = this.missions?.active?.until ?? 0;
    const left = timeLeft(until, now);
    this.timer.textContent = left ? `Срок: ${left}` : 'Срок вышел';
    this.timer.dataset.low = String(left !== null && until - now / 1000 < LOW_TIMER_SECONDS);
  }

  private render(): void {
    this.timer = null; // экран перерисовывается целиком: прежняя надпись отсчёта уже не в документе
    const hangar = this.hangar;
    if (!hangar?.docked) {
      this.root.hidden = true;
      return;
    }
    this.root.hidden = false;
    const credits = this.cargo?.credits ?? 0;

    const card = el('div', 'dock-card sro-pane sro-pane--window');
    // Шапка в три зоны (M15.5): «Вылет» — главное, ради чего сюда заходят, и стоит по центру.
    const head = el('div', 'dock-head sro-head');
    const left = el('div', 'dock-head-left sro-head__left');
    const title = el('div', 'dock-title sro-head__title', this.station);
    // Подпись магазина станции (M11): «Военная станция Nova» — по ней видно, чем здесь торгуют.
    if (this.shop.title) title.append(el('span', 'dock-shop-title', this.shop.title));
    left.append(title, el('div', 'dock-credits sro-credits', formatCredits(credits)));
    const right = el('div', 'dock-head-right sro-head__right');
    const burger = button('☰', 'menu-open dock-menu sro-btn sro-btn--icon', () => this.handlers.onMenu(burger.getBoundingClientRect()));
    burger.title = 'Меню';
    burger.setAttribute('aria-label', 'Меню');
    right.append(burger);
    head.append(left, button('Вылет', 'dock-undock sro-btn sro-btn--primary', () => this.handlers.onUndock()), right);
    card.append(head);
    card.append(this.scene(hangar));
    card.append(this.shipLine(hangar, credits));
    const rep = this.repLine();
    if (rep) card.append(rep);

    const tabs = el('div', 'dock-tabs sro-tabs');
    for (const { id, label } of TABS) {
      // Верфь есть не в каждом поселении (M15): нет — нет и вкладки, менять корабль тут негде.
      if (id === 'hulls' && !this.shipyard) continue;
      const tab = button(label, 'dock-tab sro-tab', () => {
        this.tab = id;
        this.armedSellGear = false; // ушли с рынка — «Точно?» не должно ждать возвращения
        this.render();
      });
      tab.setAttribute('aria-pressed', String(id === this.tab));
      tabs.append(tab);
    }
    card.append(tabs);

    const body = el('div', 'dock-body');
    body.dataset.tab = this.tab;
    if (this.tab === 'missions') this.renderMissions(body, hangar);
    else if (this.tab === 'cargo') this.renderCargo(body);
    else if (this.tab === 'hulls') this.renderHulls(body, hangar, credits);
    else if (this.tab === 'ships') this.renderShips(body, hangar, credits);
    else this.renderFitting(body, hangar, credits);
    card.append(body);

    // Экран перерисовывается целиком после каждой продажи и покупки — прокрутку списка сохраняем.
    const old = this.root.querySelector<HTMLElement>('.dock-body');
    const scroll = old?.dataset.tab === this.tab ? old.scrollTop : 0;
    this.root.replaceChildren(card);
    body.scrollTop = scroll;
  }

  /**
   * Отношение станции и системы к пилоту (M13). Вся строка — кнопка: тап разворачивает журнал
   * последних изменений. Отдельной вкладки журнал не получает: на телефоне пятая вкладка не влезает,
   * а читают его редко.
   */
  private repLine(): HTMLElement | null {
    const here = this.rep?.here;
    if (!here || !this.repRules.levels?.length) return null;

    const line = el('div', 'dock-rep');
    const row = button('', 'dock-rep-row', () => {
      this.repOpen = !this.repOpen;
      this.render();
    });
    row.setAttribute('aria-expanded', String(this.repOpen));
    for (const [label, value] of [
      ['Станция', here.value],
      ['Система', here.system],
    ] as const) {
      const chip = repChip(this.repRules, value);
      const box = el('span', 'rep-chip');
      box.style.color = chip.color;
      box.append(el('span', 'rep-chip-what sro-muted', label));
      if (chip.icon) box.append(repIcon(chip.icon));
      box.append(el('span', 'rep-chip-level', chip.text));
      row.append(box);
    }
    if (this.repLog.length > 0) row.append(el('span', 'rep-more sro-muted', this.repOpen ? '▴' : '▾'));
    line.append(row);

    if (this.repOpen && this.repLog.length > 0) {
      const log = el('div', 'dock-rep-log');
      for (const change of this.repLog) {
        const entry = el('div', 'dock-rep-entry', repLogLine(change, this.placeName(change.key)));
        entry.dataset.sign = change.delta > 0 ? 'up' : 'down';
        log.append(entry);
      }
      line.append(log);
    }
    return line;
  }

  /**
   * Множитель цен от отношения. Ступень считает сервер (лучшее из станции и региона) и присылает готовой —
   * дока только умножает, чтобы показать ровно то, что спишется.
   */
  private repMul(): number {
    return this.rep?.here ? priceMul(levelById(this.repRules, this.rep.here.level)) : 1;
  }

  /** Цена с учётом отношения — тем же округлением, что на сервере. */
  private repCost(base: number): number {
    return repPrice(base, this.repMul());
  }

  /** Хватает ли репутации, чтобы это вообще продали. */
  private repAllows(id: string, hull: boolean): boolean {
    const here = this.rep?.here;
    return here ? allowsLevel(this.repRules, here.level, id, hull) : true;
  }

  /**
   * Места галактики по ключу: имя места, его система и имя системы (M15.6). Считается один раз
   * на карту — она приходит в welcome и потом не меняется.
   */
  private placeIndex(): Map<string, { name: string; system: string; systemName: string }> {
    if (this.places) return this.places;
    const index = new Map<string, { name: string; system: string; systemName: string }>();
    for (const system of this.galaxy?.systems ?? []) {
      for (const place of system.places ?? []) {
        index.set(place.key, { name: place.name, system: system.id, systemName: system.name });
      }
    }
    this.places = index;
    return index;
  }

  /**
   * Человеческое имя места или системы по ключу («sys:vega», «st:vega», «pl:terra»).
   * Сначала — то, где стоим (там имя точно верное), потом карта галактики, потом своя система.
   */
  private placeName(key: string): string | null {
    if (key === this.placeKey) return this.placeLabel;
    const known = this.placeIndex().get(key);
    if (known) return known.name;
    const [kind, id] = [key.slice(0, key.indexOf(':')), key.slice(key.indexOf(':') + 1)];
    return kind !== 'pl' && id === this.here ? this.systemStation : null;
  }

  /**
   * Сцена дока — только на широком экране (на телефоне её прячет CSS, и фон там не грузится: картинка
   * задана переменной, которую читает лишь правило для ПК). Пока фона нет, под ним видна заливка сцены.
   */
  private scene(hangar: HangarMsg): HTMLElement {
    const scene = SCENES[this.tab];
    const view = el('div', 'dock-scene');
    view.dataset.scene = scene.art;
    // Абсолютный адрес: относительный url() в CSS-переменной браузер отсчитывает от файла стилей (assets/), а не от страницы.
    view.style.setProperty('--scene-art', `url("${new URL(sceneUrl(this.place, this.tab, this.scene_), document.baseURI).href}")`);
    if (scene.ship) {
      const ship = icon(shipSprite(hangar.hull));
      ship.className = 'dock-scene-ship';
      view.append(ship);
    }
    if (scene.who) {
      const person = staffOf(this.staffSeed(), scene.who, sceneArt(this.place, this.tab, this.scene_));
      const caption = el('div', 'dock-scene-caption');
      // Торговец вместо приветствия рассказывает, что слышал: подсказка ценнее вежливости.
      const line = this.tab === 'cargo' ? (this.rumour() ?? person.line) : person.line;
      caption.append(el('div', 'dock-scene-who sro-label', `${person.name} · ${person.role}`), el('div', 'dock-scene-line', line));
      view.append(caption);
    }
    return view;
  }

  /** На верфи корабль в сцене — тот, над чьей строкой мышь; ушла — снова свой. */
  private previewHull(id: string | null): void {
    const ship = this.root.querySelector<HTMLImageElement>('.dock-scene-ship');
    const hull = id ?? this.hangar?.hull;
    if (ship && hull) ship.src = spriteUrl(shipSprite(hull));
  }

  /**
   * Свой корабль: корпус, пушка, прочность, кредиты и ремонт. Кредиты стоят здесь вторым разом (M15.7):
   * в шапке они слева, а на ПК списки «Рынка» и «Оснащения» — в правой колонке, и сумма оказывалась
   * через весь экран от кнопок покупки. Эта строка — последняя перед рядом вкладок.
   */
  private shipLine(hangar: HangarMsg, credits: number): HTMLElement {
    const line = el('div', 'dock-ship');
    const hull = this.hulls.get(hangar.hull);
    const guns = hangar.fit.weapons.filter((id): id is string => !!id).map((id) => this.weapons.get(id).name);
    line.append(el('div', 'dock-ship-name sro-strong', [hull.name, ...guns].join(' · ')));
    line.append(el('div', 'dock-ship-hp sro-num sro-muted', `Корпус ${hangar.hp} / ${hangar.maxHp}`));
    line.append(el('div', 'dock-ship-credits sro-credits', formatCredits(credits)));
    const missing = hangar.maxHp - hangar.hp;
    if (missing > 0) {
      const cost = this.repCost(repairCost(this.shop, missing, hangar.maxHp, price(this.shop.hulls, hangar.hull) ?? 0));
      const repair = button(cost > 0 ? `Ремонт · ${formatCredits(cost)}` : 'Ремонт бесплатно', 'dock-buy sro-btn sro-btn--sm', () =>
        this.handlers.onRepair(),
      );
      repair.disabled = cost > credits;
      line.append(repair);
      // Платить нечем — не тупик: корпус чинится сам, пока стоишь здесь (M15.7).
      if (repair.disabled && this.mendRate > 0) {
        line.append(el('div', 'dock-ship-mend sro-muted', `Чинится сам: ${this.mendRate} ед. в минуту, пока вы в доке`));
      }
    }
    return line;
  }

  /**
   * Что показать на рынке: сначала то, что лежит в трюме (это продают), потом остальной ассортимент станции.
   * Один список вместо двух вкладок — цена товара в доке ровно одна, и видно её в одном месте.
   */
  private marketRows(credits: number): MarketRow[] {
    const cargo = this.cargo;
    const rules = this.loot;
    if (!cargo || !rules) return [];
    const quotes = new Map((this.quotes?.items ?? []).map((q) => [q.id, q]));
    const free = Math.max(0, cargo.max - cargo.used);

    // Сюжетный груз (M20a) на рынок не попадает: там ему нечего делать, а строка «здесь не торгуют»
    // рядом с уликой из шестой миссии читалась бы как «не повезло с местом».
    const ids: string[] = [...Object.keys(cargo.items)].filter((id) => !isStory(rules, id));
    for (const id of quotes.keys()) if (!cargo.items[id]) ids.push(id);

    return ids.map((id) => {
      const quote = quotes.get(id) ?? null;
      const have = cargo.items[id] ?? 0;
      const maxBuy = quote && sellsHere(quote) ? maxBuyable(this.local, rules, quote, credits, free) : 0;
      const maxSell = quote ? have : 0;
      return {
        id,
        have,
        quote,
        sells: !!quote && sellsHere(quote),
        qty: clampQty(this.qty.get(id) ?? 1, Math.max(maxBuy, maxSell)),
        maxBuy,
        maxSell,
      };
    });
  }

  /**
   * Что на складе места и сколько за это дадут (M16a). Склад — это и есть «модули в трюме»: при стыковке
   * снаряжение из трюма переезжает туда само. Стоящее на корабле сюда не попадает и продаться не может.
   * То, за что здесь не дают ни кредита, в счёт не идёт: кнопка его не тронет.
   */
  private storedGear(): { count: number; total: number } {
    let count = 0;
    let total = 0;
    for (const [id, have] of Object.entries(this.hangar?.storage ?? {})) {
      const price = sellPrice(this.shop, id);
      if (have <= 0 || price <= 0) continue;
      count += have;
      total += price * have;
    }
    return { count, total };
  }

  /** Одна история здешнего торговца, готовой строкой; null — рассказывать нечего. */
  private rumour(): string | null {
    const rules = this.loot;
    const first = this.quotes?.rumours?.[0];
    if (!rules || !first) return null;
    return rumourLine(first, lootItem(rules, first.good)?.name ?? first.good, this.staffSeed());
  }

  /** Чьи люди встречают в доке: ключ места, а пока его нет — имя станции. */
  private staffSeed(): string {
    return this.placeKey ?? this.station;
  }

  /** Рынок станции (M12): в одном списке и покупка, и продажа. */
  private renderCargo(body: HTMLElement): void {
    const cargo = this.cargo;
    const rules = this.loot;
    if (!cargo || !rules) return;
    // Событие спроса (M15.5) — первой строкой: за ним сюда и летели.
    const demand = demandLine(this.quotes?.demand, (good) => lootItem(rules, good)?.name ?? good);
    if (demand) body.append(el('div', 'dock-demand sro-pane sro-pane--warn', demand));
    body.append(el('div', 'dock-note sro-muted', `Трюм ${round(cargo.used)} / ${round(cargo.max)}`));
    if (cargo.reserved > 0) body.append(el('div', 'dock-note sro-muted', `Из них груз задания — ${cargo.reserved} ед.: не продаётся`));
    if (!rules.stationUnload) {
      body.append(el('div', 'dock-note sro-muted', 'Станция сейчас груз не принимает'));
      return;
    }

    const rows = this.marketRows(cargo.credits);
    if (rows.length === 0) {
      body.append(el('div', 'dock-empty sro-muted', 'Трюм пуст, и торговать здесь нечем. Груз добывают с пиратов, метеоритов и из контейнеров.'));
      return;
    }
    // Та же реплика торговца, что стоит под его картинкой, — для телефона, где сцены нет совсем.
    // На широком экране её прячет CSS тем же брейкпоинтом, которым показывает сцену: дважды не повторяем.
    const rumour = this.rumour();
    if (rumour) body.append(el('div', 'dock-rumour', `${staffOf(this.staffSeed(), 'cargo', sceneArt(this.place, 'cargo', this.scene_)).name}: ${rumour}`));

    // Быстрая продажа — первым делом: с полным трюмом в док заходят чаще, чем за покупками.
    // Две кнопки в ряд (M16a): ресурсы и модули продаются отдельно, чтобы за модулями не ходить
    // во вкладку «Модули» и не искать их там по одной строке.
    const quick = el('div', 'dock-quick-sell');
    const sellable = rows.filter((r) => r.maxSell > 0 && r.quote);
    if (sellable.length > 0) {
      let total = 0;
      for (const r of sellable) {
        total += tradeCost(this.local, r.id, lootItem(rules, r.id)?.price ?? 0, r.quote!.stock, r.have, false);
      }
      quick.append(button(
        `Продать ресурсы · ${formatCredits(total)}`,
        'dock-buy dock-sell-all sro-btn sro-btn--sm',
        () => {
          this.armedSellGear = false;
          this.handlers.onSell();
        },
      ));
    }
    const gear = this.storedGear();
    if (gear.count > 0) {
      // Модули продаются в два касания: снятый щит стоит дороже всего трюма, и промах пальцем
      // по кнопке рядом с «Продать ресурсы» обошёлся бы слишком дорого.
      const label = this.armedSellGear
        ? `Точно? · ${gear.count} шт · ${formatCredits(gear.total)}`
        : `Продать модули · ${gear.count} шт · ${formatCredits(gear.total)}`;
      quick.append(button(label, `dock-buy dock-sell-gear sro-btn sro-btn--sm${this.armedSellGear ? ' sro-btn--danger' : ''}`, () => {
        if (this.armedSellGear) {
          this.armedSellGear = false;
          this.handlers.onSellGear();
          return;
        }
        this.armedSellGear = true;
        this.render();
      }));
    } else {
      this.armedSellGear = false;
    }
    if (quick.childElementCount > 0) body.append(quick);
    for (const row of rows) body.append(this.marketRow(row, rules));
  }

  private marketRow(row: MarketRow, rules: LootRules): HTMLElement {
    const item = lootItem(rules, row.id);
    const basePrice = item?.price ?? 0;
    const view = el('div', 'dock-row dock-market-row sro-row');
    view.append(icon(gearIcon(rules, row.id)));

    const name = el('div', 'dock-name sro-row__name', item?.name ?? row.id);
    name.style.color = color(rarityColor(rules, row.id));
    if (row.have > 0) name.append(el('span', 'dock-market-have sro-row__meta', ` в трюме ${row.have}`));
    view.append(name);

    if (!row.quote) {
      // Товар есть, но станция им не торгует: чужой регион или контрабанда.
      view.append(el('div', 'dock-tag sro-row__meta', 'Здесь этим не торгуют'));
      return view;
    }

    view.append(this.priceLine(row.quote, basePrice));
    const max = Math.max(row.maxBuy, row.maxSell);
    if (max > 0) view.append(this.stepper(row, max));

    const actions = el('div', 'dock-market-actions');
    if (row.maxBuy > 0) {
      const count = Math.min(row.qty, row.maxBuy);
      const cost = tradeCost(this.local, row.id, basePrice, row.quote.stock, count, true);
      actions.append(button(`Купить ${count} · ${formatCredits(cost)}`, 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onBuyGoods(row.id, count)));
    } else if (row.sells) {
      // Продают, но прямо сейчас нельзя: пусто на складе, нет места или не хватает кредитов.
      actions.append(el('div', 'dock-tag sro-row__meta', row.quote.stock <= 0 ? 'Склад пуст' : 'Не по карману'));
    } else {
      // Единственная причина отказа с M16a: это и есть груз здешнего задания «собрать».
      actions.append(el('div', 'dock-tag sro-row__meta', 'Груз задания: его надо привезти'));
    }
    if (row.maxSell > 0) {
      const count = Math.min(row.qty, row.maxSell);
      const gain = tradeCost(this.local, row.id, basePrice, row.quote.stock, count, false);
      actions.append(button(`Продать ${count} · ${formatCredits(gain)}`, 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onSell(row.id, count)));
    }
    if (actions.childElementCount > 0) view.append(actions);
    return view;
  }

  /** Цена штуки, стрелка «дороже/дешевле обычного» и намёк на склад станции. */
  private priceLine(quote: MarketItemDto, basePrice: number): HTMLElement {
    const line = el('div', 'dock-stats dock-market-price sro-row__meta');
    const where = trend(quote.buy, quote.sell, basePrice);
    const arrow = where === 'up' ? '▲' : where === 'down' ? '▼' : '';
    const price = el('span', 'dock-market-rate', `${formatCredits(quote.sell)} / ${formatCredits(quote.buy)}`);
    price.title = 'Станция покупает / продаёт за штуку';
    line.append(price);
    if (arrow) {
      const mark = el('span', `dock-market-trend dock-market-${where} sro-trend-${where}`, ` ${arrow}`);
      mark.title = where === 'up' ? 'Дороже обычного' : 'Дешевле обычного';
      line.append(mark);
    }
    const level = stockLevel(quote.stock, quote.norm);
    const stock = el('span', 'dock-market-stock', ` склад ${quote.stock}`);
    stock.dataset.level = level;
    line.append(stock);
    return line;
  }

  /** Счётчик количества: значение живёт на экране, разметка строится из него заново на каждой перерисовке. */
  private stepper(row: MarketRow, max: number): HTMLElement {
    const box = el('div', 'dock-qty sro-stepper');
    const set = (value: number): void => {
      this.qty.set(row.id, clampQty(value, max));
      this.render();
    };
    box.append(button('−', 'dock-qty-step sro-btn', () => set(row.qty - 1)));
    box.append(el('div', 'dock-qty-value sro-stepper__value', String(row.qty)));
    box.append(button('+', 'dock-qty-step sro-btn', () => set(row.qty + 1)));
    if (max > 10) box.append(button('+10', 'dock-qty-step sro-btn', () => set(row.qty + 10)));
    box.append(button('Макс', 'dock-qty-step sro-btn', () => set(max)));
    return box;
  }

  /** Обучение (GDD §54), своё задание и доска станции (§36). */
  private renderMissions(body: HTMLElement, hangar: HangarMsg): void {
    const missions = this.missions;
    if (!missions) return;
    const tutorial = missions.tutorial;
    if (tutorial) {
      const box = el('div', 'dock-mission dock-tutorial');
      box.append(el('div', 'dock-mission-head sro-label sro-warn', `Обучение · шаг ${tutorial.step + 1} из ${tutorial.total}`));
      box.append(el('div', 'dock-name sro-row__name', tutorial.title));
      if (tutorial.hint) box.append(el('div', 'dock-stats sro-row__meta', keyHint(tutorial.hint, keymap)));
      const actions = el('div', 'dock-mission-actions');
      if (tutorial.kind === 'undock') actions.append(button('Вылет', 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onUndock()));
      actions.append(button('Пропустить обучение', 'dock-link sro-btn sro-btn--ghost sro-btn--sm', () => this.handlers.onSkipTutorial()));
      box.append(actions);
      body.append(box);
    }

    const active = missions.active;
    if (active) {
      const box = el('div', 'dock-mission');
      box.append(el('div', 'dock-mission-head sro-label sro-warn', `Задание · награда ${formatCredits(active.offer.reward)}`));
      box.append(el('div', 'dock-name sro-row__name', activeLine(active, this.names)));
      box.append(el('div', 'dock-stats sro-row__meta', activeHint(active, this.here, hangar.docked, this.names)));
      if (active.until) {
        // Срок идёт, пока пилот торгуется на станции: цифра живая, её двигает tick().
        this.timer = el('div', 'dock-timer sro-num sro-warn');
        box.append(this.timer);
        this.tick(Date.now());
      }
      const actions = el('div', 'dock-mission-actions');
      if (active.offer.kind === 'collect') {
        const give = button('Сдать', 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onComplete());
        give.disabled = active.progress < active.offer.count;
        actions.append(give);
      }
      actions.append(button('Отказаться', 'dock-link sro-btn sro-btn--ghost sro-btn--sm', () => this.handlers.onAbandon()));
      box.append(actions);
      body.append(box);
    }

    // Сюжет — над обычной работой и своей строкой (M20a): это не «ещё одно задание с доски»,
    // а продолжение истории, и найтись оно должно первым.
    const story = missions.story;
    if (story?.offer) {
      body.append(el('div', 'dock-note dock-story-note sro-warn', `${story.name} · миссия ${story.offer.story?.number ?? story.done + 1} из ${story.total}`));
      body.append(this.missionRow(story.offer, active !== null));
    }

    // Доска и сюжет приезжают в одном списке offers: сюжетную строку из него убираем, иначе она
    // встала бы дважды — своим разделом и рядовой работой станции.
    const board = missions.offers.filter((offer) => !offer.story);
    if (board.length === 0) {
      if (!active && !story?.offer) body.append(el('div', 'dock-empty sro-muted', 'Заданий на этой станции нет.'));
      return;
    }
    body.append(el('div', 'dock-note sro-muted', active ? 'Доска станции: сначала сдайте или бросьте своё задание' : 'Доска станции'));
    for (const offer of board) body.append(this.missionRow(offer, active !== null));
  }

  private missionRow(offer: MissionOffer, busy: boolean): HTMLElement {
    const row = el('div', 'dock-row sro-row');
    row.dataset.state = busy ? 'poor' : 'buy';
    if (offer.story) row.classList.add('dock-row--story');
    const name = el('div', 'dock-name sro-row__name', offerTitle(offer, this.names));
    // Кто даёт работу — видно до того, как её возьмут: с этого человека и начинается сцена.
    const note = offer.story
      ? `${offer.story.giver} · ${offer.story.role} — ${offerNote(offer, this.names)}`
      : offerNote(offer, this.names);
    row.append(name, el('div', 'dock-stats sro-row__meta', note));
    const take = button(`Взять · ${formatCredits(offer.reward)}`, 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onAccept(offer.id));
    take.disabled = busy;
    row.append(take);
    return row;
  }

  private renderHulls(body: HTMLElement, hangar: HangarMsg, credits: number): void {
    if (this.modules.enabled) body.append(el('div', 'dock-note sro-muted', 'Щит, радар и двигатель — модули: они переходят на новый корпус'));
    let shown = 0;
    for (const id of this.hulls.ids()) {
      const hull = this.hulls.get(id);
      const slots = hullSlots(hull).join(' ');
      const utility = hullUtilitySlots(hull);
      const own = this.modules.enabled
        ? [`класс ${hull.class ?? 'L'}`, `пушки ${slots}`, utility ? `вспомогательных ${utility}` : '']
        : [`щит ${hull.shield}`, hull.radar ? `радар ${hull.radar}` : ''];
      const stats = [`корпус ${hull.hp} · скорость ${hull.maxSpeed} · трюм ${hull.cargo}`, ...own.filter(Boolean)].join(' · ');
      // Здесь продают не всё (M11): чего нет в ассортименте станции, то и не купить.
      const listed = sells(this.shop, id, this.shop.hulls) ? price(this.shop.hulls, id) : null;
      const cost = listed === null ? null : this.repCost(listed);
      const state = offerState(hangar.hulls.includes(id), id === hangar.hull, cost, credits, !this.repAllows(id, true));
      // Чего здесь не продают, того здесь и нет (M15.6): строка «Здесь нет» была длинным списком
      // недоступного, в котором тонуло доступное. Свои корпуса и закрытые репутацией остаются:
      // первые уже куплены, вторые — цель, а не шум.
      if (state === 'none') continue;
      shown++;
      const name = hull.role ? `${hull.name} — ${hull.role}` : hull.name;
      body.append(this.offer(id, name, stats, state));
    }
    if (shown === 0) body.append(el('div', 'dock-empty sro-muted', 'Здесь корпуса не продают — только чинят и меняют на свои.'));
  }

  /**
   * «Мой ангар» (M15.6): все свои корпуса и где какой стоит. Список виден всюду — найти свои корабли
   * это сведения; сесть в них и сдвинуть их можно только там, где есть верфь: это уже услуга.
   */
  private renderShips(body: HTMLElement, hangar: HangarMsg, credits: number): void {
    body.append(el('div', 'dock-note sro-muted', 'Мой ангар'));
    const at = hangar.ships ?? {};
    const here = this.placeKey;
    const jumps = this.galaxy && this.here ? hops(this.galaxy, this.here) : null;
    const owned = hangar.guest ? [hangar.hull] : hangar.hulls;
    for (const id of owned) {
      const hull = this.hulls.get(id);
      const row = el('div', 'dock-row sro-row');
      row.addEventListener('mouseenter', () => this.previewHull(id));
      row.addEventListener('mouseleave', () => this.previewHull(null));
      row.append(icon(shipSprite(id)));
      row.append(el('div', 'dock-name sro-row__name', hull.role ? `${hull.name} — ${hull.role}` : hull.name));
      const where = at[id] ?? null;
      row.append(el('div', 'dock-stats sro-row__meta', where === null ? 'под вами' : this.whereLine(where)));
      row.append(this.shipAction(id, where, here, jumps, credits));
      body.append(row);
    }
  }

  /** «Станция Vega · система Vega» / «Поселение «Новый Порт» · система Sol». */
  private whereLine(key: string): string {
    const known = this.placeIndex().get(key);
    const name = known?.name ?? this.placeName(key) ?? key.slice(key.indexOf(':') + 1);
    const label = placeKind(key) === 'planet' ? `Поселение «${name}»` : `Станция ${name}`;
    return known ? `${label} · система ${known.systemName}` : label;
  }

  /**
   * Что можно сделать с этим корпусом: он под вами, он здесь, он в другом доке — или туда нет пути.
   * @param where ключ места, где он стоит; null — он под пилотом
   */
  private shipAction(
    id: string,
    where: string | null,
    here: string | null,
    jumps: ReadonlyMap<string, number> | null,
    credits: number,
  ): HTMLElement {
    if (where === null) return el('div', 'dock-tag sro-row__meta', 'На корабле');
    // Двигать корабли — работа верфи: где её нет, ангар можно только посмотреть.
    if (!this.shipyard) return el('div', 'dock-tag sro-row__meta', 'Нужна верфь');
    if (where === here) return button('Сесть', 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onEquip(id));
    const system = this.placeIndex().get(where)?.system;
    const hops_ = system !== undefined ? jumps?.get(system) : undefined;
    if (hops_ === undefined) return el('div', 'dock-tag sro-row__meta', 'Отсюда туда нет пути');
    const cost = transportCost(this.shop, price(this.shop.hulls, id) ?? 0, hops_);
    if (cost === null) return el('div', 'dock-tag sro-row__meta', 'Перевозки здесь не заказать');
    const order = button(`Перевезти · ${formatCredits(cost)}`, 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onTransport(id));
    order.disabled = cost > credits;
    return order;
  }

  /**
   * Оснащение (GDD §13, §18–20): все места под пушки и модули — кружками в один ряд вверху, с переносом.
   * В кружке стоит иконка того, что установлено; пустой слот — пунктир. Выбранный слот подсвечен, как
   * активная вкладка: под кружками его содержимое словами, а ниже — только то, что в этот слот встаёт.
   * Энергия генератора над слотами, склад станции — под магазином.
   */
  private renderFitting(body: HTMLElement, hangar: HangarMsg, credits: number): void {
    const hull = this.hulls.get(hangar.hull);
    const powerMax = hangar.powerMax ?? 0;
    if (powerMax > 0) {
      const power = hangar.power ?? 0;
      const box = el('div', 'dock-power');
      const row = el('div', 'sro-bar-row');
      row.append(el('span', 'sro-label', 'Энергия'), el('span', 'sro-num', `${power} / ${powerMax}`));
      const bar = el('div', 'sro-bar');
      const fill = el('div', 'sro-bar__fill');
      fill.style.width = `${Math.min(100, (100 * power) / powerMax)}%`;
      bar.append(fill);
      box.append(row, bar);
      body.append(box);
    }

    const slots = hullSlotViews(hull, this.modules.enabled);
    // Выбор держится за прежний слот, пока он есть у этого корпуса; после пересадки — первый по порядку.
    const chosen = slots.find((view) => view.id === this.slot) ?? slots[0];
    if (chosen) {
      this.slot = chosen.id;
      const grid = el('div', 'dock-slots');
      for (const view of slots) grid.append(this.slotCell(view, hangar, view.id === chosen.id));
      body.append(grid);
      body.append(this.fittedBox(chosen, hangar));
      body.append(el('div', 'dock-note sro-muted', `Для слота «${chosen.caption}»`));
      this.offerList(body, hangar, credits, chosen.id);
    }

    const stored = Object.entries(hangar.storage ?? {}).filter(([, count]) => count > 0);
    if (hangar.guest) return;
    body.append(el('div', 'dock-note sro-muted', 'Склад станции'));
    if (stored.length === 0) {
      body.append(el('div', 'dock-empty sro-muted', 'Пусто. Снятое с корабля и купленное про запас лежит здесь.'));
      return;
    }
    for (const [id, count] of stored) {
      const row = el('div', 'dock-row sro-row');
      const picture = this.picture(id);
      if (picture) row.append(icon(picture));
      const stock = el('div', 'dock-name sro-row__name', `${this.itemName(id)} ×${count}`);
      const mark = tierBadge(id);
      if (mark) stock.append(el('span', 'dock-tier', mark));
      row.append(stock, el('div', 'dock-stats sro-row__meta', this.itemLabel(id)));
      const cost = sellPrice(this.shop, id);
      row.append(button(cost > 0 ? `Продать · ${formatCredits(cost)}` : 'Выбросить', 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onSellItem(id)));
      body.append(row);
    }
  }

  /**
   * Кружок слота на сетке: иконка того, что стоит, и подпись «Оружие 1 (S)». Выбранный подсвечен
   * как активная вкладка. Подсказка при наведении — название и характеристики установленного (на ПК;
   * на телефоне то же самое написано под сеткой).
   */
  private slotCell(view: SlotView, hangar: HangarMsg, chosen: boolean): HTMLElement {
    const current = fitGet(hangar.fit, view.id);
    const cell = document.createElement('button');
    cell.type = 'button';
    cell.className = 'dock-slot';
    cell.setAttribute('aria-pressed', String(chosen));
    cell.dataset.fitted = String(current !== null);
    cell.title = current ? `${view.caption} · ${this.itemName(current)} — ${this.itemLabel(current)}` : `${view.caption} · свободный слот`;
    const dot = el('span', 'dock-slot-dot');
    const picture = current ? this.picture(current) : null;
    if (picture) {
      const img = document.createElement('img');
      img.className = 'dock-slot-icon';
      img.src = spriteUrl(picture);
      img.alt = '';
      img.draggable = false;
      dot.append(img);
    } else dot.textContent = current ? '●' : '+'; // иконки на этот модуль ещё нет (art/next-art-requests.md)
    cell.append(dot, el('span', 'dock-slot-cap', view.caption));
    cell.addEventListener('click', () => {
      cell.blur(); // иначе Space «нажимал» бы кнопку в фокусе
      if (this.slot === view.id) return;
      this.slot = view.id;
      this.render();
    });
    return cell;
  }

  /** Что стоит в выбранном слоте — словами, между сеткой и магазином. Отсюда же снимают на склад. */
  private fittedBox(view: SlotView, hangar: HangarMsg): HTMLElement {
    const current = fitGet(hangar.fit, view.id);
    const box = el('div', 'dock-fitted');
    const name = el('div', 'dock-fitted-name sro-row__name');
    if (current) {
      name.append(el('span', 'dock-fitted-label sro-muted', 'Установлен:'));
      const picture = this.picture(current);
      if (picture) {
        const img = document.createElement('img');
        img.className = 'dock-fitted-icon';
        img.src = spriteUrl(picture);
        img.alt = '';
        img.draggable = false;
        name.append(img);
      }
      name.append(this.itemName(current));
      const badge = tierBadge(current);
      if (badge) name.append(el('span', 'dock-tier', badge));
    } else name.append(`${view.caption}: свободный слот`);
    box.append(name);
    box.append(
      el(
        'div',
        'dock-fitted-stats sro-row__meta',
        current ? this.itemLabel(current) : 'Поставьте сюда что-нибудь из списка ниже',
      ),
    );
    // Двигатель, радар и генератор снять нельзя: без них корабль не летает (fitting.ts).
    if (current && !(REQUIRED_SLOTS as string[]).includes(view.id)) {
      box.append(button('Снять на склад', 'dock-link sro-btn sro-btn--ghost sro-btn--sm', () => this.handlers.onFit(view.id, null)));
    }
    return box;
  }

  /** Витрина выбранного слота: со склада — поставить, из магазина — купить и сразу поставить. */
  private offerList(body: HTMLElement, hangar: HangarMsg, credits: number, slot: string): void {
    const hull = this.hulls.get(hangar.hull);
    const current = fitGet(hangar.fit, slot);
    const list = el('div', 'dock-slot-list');
    const weaponsCatalog = this.weapons.config;
    const modules = this.modules.catalog;
    const kind = utilityIndex(slot) !== null ? UTILITY : slot;
    const ids = weaponIndex(slot) !== null ? this.weapons.ids() : this.modules.ids().filter((id) => this.modules.get(id)?.slot === kind);
    for (const id of ids) {
      const stored = hangar.guest ? Infinity : (hangar.storage?.[id] ?? 0);
      const problem = id === current ? null : canInstall(hull, hangar.fit, slot, id, weaponsCatalog, modules);
      // Чего не поставить по классу — и не показываем: список не должен тонуть в недоступном.
      if (problem === 'class' || problem === 'slot') continue;
      // Купить можно только то, что продают здесь (M11); своё со склада ставится везде.
      const listed = sells(this.shop, id, this.shop.items) ? price(this.shop.items, id) : null;
      // Mk3 продают только друзьям станции (M13): витрину оставляем, но с замком вместо кнопки.
      const locked = listed !== null && !this.repAllows(id, false);
      const cost = listed === null || locked ? null : this.repCost(listed);
      const offer = slotOffer(id === current, stored, cost, credits, problem);
      if (offer.action === 'none' && !locked) continue;
      const item = el('div', 'dock-row sro-row');
      item.dataset.state =
        offer.action === 'installed' ? 'active'
        : offer.action === 'install' ? 'owned'
        : offer.action === 'none' ? 'locked'
        : offer.poor || offer.problem ? 'poor'
        : 'buy';
      const picture = this.picture(id);
      if (picture) item.append(icon(picture));
      const name = offer.action === 'install' && Number.isFinite(stored) ? `${this.itemName(id)} · на складе ${stored}` : this.itemName(id);
      const title = el('div', 'dock-name sro-row__name', name);
      const badge = tierBadge(id);
      if (badge) title.append(el('span', 'dock-tier', badge));
      item.append(title, el('div', 'dock-stats sro-row__meta', this.itemLabel(id)));
      switch (offer.action) {
        case 'installed':
          item.append(el('div', 'dock-tag sro-row__meta', 'Стоит'));
          break;
        case 'install': {
          const put = button(offer.problem ? describeFitProblem(offer.problem) : 'Поставить', 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onFit(slot, id));
          put.disabled = offer.problem !== null;
          item.append(put);
          break;
        }
        case 'buy': {
          const text = offer.problem ? describeFitProblem(offer.problem) : `Купить · ${formatCredits(offer.cost)}`;
          const buy = button(text, 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onBuy('item', id, slot));
          buy.disabled = offer.poor || offer.problem !== null;
          item.append(buy);
          break;
        }
        case 'none':
          // Витрину не прячем: пусть видно, что здесь есть и чего это стоит добиться.
          item.append(el('div', 'dock-tag sro-row__meta', repGateNote(this.repRules, this.rep?.here?.level, id, false) ?? 'Только для своих'));
          break;
      }
      list.append(item);
    }
    if (list.childElementCount === 0) {
      list.append(el('div', 'dock-empty sro-muted', 'Сюда здесь ничего не продают, и на складе для этого слота пусто.'));
    }
    body.append(list);
  }

  private itemName(id: string): string {
    if (this.weapons.has(id)) return this.weapons.get(id).name;
    return this.modules.get(id)?.name ?? id;
  }

  private itemLabel(id: string): string {
    if (this.weapons.has(id)) return weaponLabel(this.weapons.get(id));
    const m = this.modules.get(id);
    return m ? `${m.class} · ${moduleLabel(m)}` : '';
  }

  private picture(id: string): string | null {
    if (this.weapons.has(id)) return weaponSprite(id);
    const m = this.modules.get(id);
    return m ? moduleSprite(m.slot, id) : null;
  }

  private offer(id: string, name: string, stats: string, state: OfferState): HTMLElement {
    const row = el('div', 'dock-row sro-row');
    row.dataset.state = state;
    row.addEventListener('mouseenter', () => this.previewHull(id));
    row.addEventListener('mouseleave', () => this.previewHull(null));
    row.append(icon(shipSprite(id)));
    row.append(el('div', 'dock-name sro-row__name', name), el('div', 'dock-stats sro-row__meta', stats));
    const cost = this.repCost(price(this.shop.hulls, id) ?? 0);
    switch (state) {
      case 'active':
        row.append(el('div', 'dock-tag sro-row__meta', 'На корабле'));
        break;
      case 'owned':
        row.append(button('Поставить', 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onEquip(id)));
        break;
      case 'buy':
      case 'poor': {
        const buy = button(`Купить · ${formatCredits(cost)}`, 'dock-buy sro-btn sro-btn--sm', () => this.handlers.onBuy('hull', id));
        buy.disabled = state === 'poor';
        row.append(buy);
        break;
      }
      case 'locked':
        row.append(el('div', 'dock-tag sro-row__meta', repGateNote(this.repRules, this.rep?.here?.level, id, true) ?? 'Только для своих'));
        break;
    }
    return row;
  }
}

/** Место под пушку или модуль на сетке оснащения: его id и подпись под кружком. */
export interface SlotView {
  id: string;
  /** «Оружие 1 (S)», «Щит (M)», «Вспом. 2 (S)» — что это за место и какого оно класса. */
  caption: string;
}

/**
 * Все места корпуса по порядку: сначала пушки, потом основные модули, потом вспомогательные.
 * Класс в скобках — то, что в это место влезает: у пушек свой на каждый слот, у модулей — класс корпуса,
 * вспомогательные всегда S (shared/modules.json).
 *
 * @param modules Модули включены сервером; пока нет — на сетке только оружие.
 */
export function hullSlotViews(hull: HullParams, modules: boolean): SlotView[] {
  const views: SlotView[] = hullSlots(hull).map((cls, i) => ({ id: weaponSlot(i), caption: `Оружие ${i + 1} (${cls})` }));
  if (!modules) return views;
  const cls = hull.class ?? 'L';
  for (const slot of MODULE_SLOTS) views.push({ id: slot, caption: `${SLOT_NAMES[slot]} (${cls})` });
  for (let i = 0; i < hullUtilitySlots(hull); i++) views.push({ id: utilitySlot(i), caption: `Вспом. ${i + 1} (S)` });
  return views;
}

/** Строка характеристик пушки на витрине: класс, урон, темп, точность (у ракетницы — самонаведение), дальность, энергия. */
export function weaponLabel(weapon: WeaponParams): string {
  const aim = weapon.missile ? 'самонаведение' : `точность ${weapon.accuracy}%`;
  // Дробовик и залп (M19): в файле damage — за дробину и за ракету, и написать одно это число значило бы
  // соврать вчетверо. Пишем «5 × 38», чтобы витрина показывала то, что на самом деле уходит за нажатие.
  const shots = (weapon.pellets ?? 1) * (weapon.salvo ?? 1);
  const damage = shots > 1 ? `урон ${shots} × ${weapon.damage}` : `урон ${weapon.damage}`;
  const parts = [damage, `раз в ${weapon.cooldown} с`, aim, `дальность ${weapon.maxRange}`];
  if (weapon.class) parts.unshift(weapon.class);
  if (weapon.pellets && weapon.pellets > 1) parts.push('веером');
  if (weapon.salvo && weapon.salvo > 1) parts.push('залпом');
  // Гаусс и ионка (M19): множители меняют роль пушки сильнее, чем её урон, и в строке им место.
  if (weapon.shieldFactor !== undefined && weapon.shieldFactor !== 1) parts.push(`по щиту ×${weapon.shieldFactor}`);
  if (weapon.hullFactor !== undefined && weapon.hullFactor !== 1) parts.push(`по корпусу ×${weapon.hullFactor}`);
  if (weapon.power) parts.push(`энергия ${weapon.power}`);
  return parts.join(' · ');
}

function el(tag: string, className: string, text?: string): HTMLElement {
  const node = document.createElement(tag);
  node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

/** Картинка предмета, корабля или пушки слева в строке витрины. */
function icon(sprite: string): HTMLElement {
  const img = document.createElement('img');
  img.className = 'dock-icon';
  img.src = spriteUrl(sprite);
  img.alt = '';
  img.draggable = false;
  return img;
}

function button(text: string, className: string, onClick: () => void): HTMLButtonElement {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = className;
  b.textContent = text;
  b.addEventListener('click', () => {
    b.blur(); // иначе Space «нажимал» бы кнопку в фокусе
    onClick();
  });
  return b;
}
