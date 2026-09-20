import type {
  BuyKind,
  HangarMsg,
  MarketItemDto,
  MarketMsg,
  MissionOffer,
  MissionsMsg,
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
import { itemSprite, moduleSprite, shipSprite, spriteUrl, weaponSprite } from '../render/sprites';
import type { Hulls } from '../sim/hulls';
import { activeHint, activeLine, offerNote, offerTitle, timeLeft, type MissionNames } from '../sim/missions';
import { lootItem, rarityColor, type LootRules } from '../sim/loot';
import { NO_MARKET, affordable, rumourLine, stockLevel, tradeCost, trend, type MarketRules } from '../sim/market';
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
import { NO_SHOP, formatCredits, fuelCost, price, repairCost, sells, sellPrice, type ShopRules } from '../sim/shop';
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
  /** Не продаётся. */
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

export type Tab = 'missions' | 'cargo' | 'hulls' | 'fitting';

const TABS: { id: Tab; label: string }[] = [
  { id: 'missions', label: 'Задания' },
  // Покупка и продажа товаров — в одном списке (M12); id остался прежним, на него смотрит startTab.
  { id: 'cargo', label: 'Рынок' },
  { id: 'hulls', label: 'Корабли' },
  { id: 'fitting', label: 'Оснащение' },
];

/** Где стоит корабль: станция или планета (город) — от этого фон сцены. Пока док только на станциях. */
export type Place = 'station' | 'planet';

/** Сцена слева на ПК: у каждой вкладки своё место в доке и свой собеседник. */
interface Scene {
  /** Имя картинки фона: public/dock/{place}-{art}.webp. */
  art: string;
  /** Кто говорит в подписи; пусто — подписи нет. */
  who: string;
  line: string;
  /** Показывать ли свой корабль поверх фона. */
  ship: boolean;
}

const SCENES: Record<Tab, Scene> = {
  missions: { art: 'office', who: 'Диспетчер', line: 'Работа есть всегда. Вопрос — сколько вы готовы рискнуть.', ship: false },
  cargo: { art: 'trader', who: 'Торговец', line: 'Товар берут там, где его нет. Остальное — арифметика.', ship: false },
  hulls: { art: 'shipyard', who: 'Мастер верфи', line: 'Корпус выбирают под задачу, а не под мечту.', ship: true },
  fitting: { art: 'hangar', who: '', line: '', ship: true },
};

/**
 * Свои сцены дока у отдельных станций: какие вкладки нарисованы для набора. Чего в наборе нет,
 * берётся общая сцена места — наборы дорисовываются по одной картинке, а не пачкой.
 */
const SCENE_SETS: Record<string, readonly string[]> = {
  ranger: ['office'],
};

/** Адрес фона сцены: относительный, как и спрайты. */
export function sceneUrl(place: Place, tab: Tab, set?: string | null): string {
  const art = SCENES[tab].art;
  if (set && SCENE_SETS[set]?.includes(art)) return `dock/${set}-${art}.webp`;
  return `dock/${place}-${art}.webp`;
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
  /** Поставить в слот со склада; id = null — снять на склад. */
  onFit(slot: string, id: string | null): void;
  /** Продать со склада пушку или модуль. */
  onSellItem(id: string): void;
  onRepair(): void;
  onRefuel(): void;
  onUndock(): void;
  /** Окно «Управление» (ПК). */
  onControls(): void;
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

/** Плашка отношения: подпись и цвет ступени. */
export function repChip(rules: ReputationRules, value: number): { text: string; color: string } {
  const level = levelOf(rules, value);
  return { text: repLabel(level, value), color: levelColor(level) };
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
  private station = 'Станция';
  private place: Place = 'station';
  private here: string | null = null;
  private missions: MissionsMsg | null = null;
  /** Надпись обратного отсчёта у письма; null — взятого письма нет (M14). */
  private timer: HTMLElement | null = null;
  /** Слот, для которого открыт список пушек или модулей; null — ни один. */
  private slot: string | null = null;
  /** Свой набор фонов дока у этой станции; null — общие сцены места. */
  private scene_: string | null = null;
  /** Правила рынка этой станции (M12). */
  private market: MarketRules = NO_MARKET;
  private repRules: ReputationRules = NO_REP;
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
  ): void {
    this.loot = loot ?? null;
    this.shop = shop ?? NO_SHOP;
    this.market = market ?? NO_MARKET;
    this.repRules = reputation ?? NO_REP;
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

  /** Живые цены станции; null — рынка здесь нет, груз сдаётся по обычной цене. */
  setMarket(market: MarketMsg | null): void {
    this.quotes = market;
    this.render();
  }

  setCargo(state: CargoState): void {
    this.cargo = state;
    this.render();
  }

  /** null — связи нет: экран закрыт до нового ангара от сервера. */
  setHangar(hangar: HangarMsg | null): void {
    // Каждый заход в док начинается с груза — или с заданий, если там ждут.
    if (hangar?.docked && !this.open) this.tab = startTab(this.missions, this.cargo?.items ?? {});
    this.hangar = hangar;
    this.render();
  }

  /** Имя станции в заголовке — по системе: «Станция Vega»; scene — свой набор фонов дока (M12). */
  setStation(name: string | null, system: string | null = null, scene: string | null = null): void {
    this.station = name ? `Станция ${name}` : 'Станция';
    this.here = system;
    this.scene_ = scene;
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

    const card = el('div', 'dock-card');
    const head = el('div', 'dock-head');
    const title = el('div', 'dock-title', this.station);
    // Подпись магазина станции (M11): «Военная станция Nova» — по ней видно, чем здесь торгуют.
    if (this.shop.title) title.append(el('span', 'dock-shop-title', this.shop.title));
    head.append(title, el('div', 'dock-credits', formatCredits(credits)));
    const gear = button('⚙', 'controls-open dock-controls', () => this.handlers.onControls());
    gear.title = 'Управление';
    gear.setAttribute('aria-label', 'Управление');
    head.append(gear);
    head.append(button('Вылет', 'dock-undock', () => this.handlers.onUndock()));
    card.append(head);
    card.append(this.scene(hangar));
    card.append(this.shipLine(hangar, credits));
    const rep = this.repLine();
    if (rep) card.append(rep);

    const tabs = el('div', 'dock-tabs');
    for (const { id, label } of TABS) {
      const tab = button(label, 'dock-tab', () => {
        this.tab = id;
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
      box.append(el('span', 'rep-chip-what', label), el('span', 'rep-chip-level', chip.text));
      row.append(box);
    }
    if (this.repLog.length > 0) row.append(el('span', 'rep-more', this.repOpen ? '▴' : '▾'));
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

  /** Человеческое имя места из ключа «sys:vega»; знаем только здешнюю систему — остальные по id. */
  private placeName(key: string): string | null {
    const id = key.slice(key.indexOf(':') + 1);
    return id === this.here ? this.station.replace('Станция ', '') : null;
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
      const caption = el('div', 'dock-scene-caption');
      // Торговец вместо приветствия рассказывает, что слышал: подсказка ценнее вежливости.
      const line = this.tab === 'cargo' ? (this.rumour() ?? scene.line) : scene.line;
      caption.append(el('div', 'dock-scene-who', scene.who), el('div', 'dock-scene-line', line));
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

  /** Свой корабль: корпус, пушка, прочность и ремонт. */
  private shipLine(hangar: HangarMsg, credits: number): HTMLElement {
    const line = el('div', 'dock-ship');
    const hull = this.hulls.get(hangar.hull);
    const guns = hangar.fit.weapons.filter((id): id is string => !!id).map((id) => this.weapons.get(id).name);
    line.append(el('div', 'dock-ship-name', [hull.name, ...guns].join(' · ')));
    line.append(el('div', 'dock-ship-hp', `Корпус ${hangar.hp} / ${hangar.maxHp}`));
    const missing = hangar.maxHp - hangar.hp;
    if (missing > 0) {
      const cost = this.repCost(repairCost(this.shop, missing, hangar.maxHp, price(this.shop.hulls, hangar.hull) ?? 0));
      const repair = button(cost > 0 ? `Ремонт · ${formatCredits(cost)}` : 'Ремонт бесплатно', 'dock-buy', () =>
        this.handlers.onRepair(),
      );
      repair.disabled = cost > credits;
      line.append(repair);
    }
    // Топливо (GDD §6) — только на гиперпрыжки: заправка здесь же, до полного бака.
    const maxFuel = hangar.maxFuel ?? 0;
    if (maxFuel > 0) {
      const fuel = hangar.fuel ?? 0;
      line.append(el('div', 'dock-ship-hp', `Топливо ${fuel} / ${maxFuel}`));
      if (fuel < maxFuel) {
        const cost = fuelCost(this.shop, maxFuel - fuel);
        const refuel = button(cost > 0 ? `Заправить · ${formatCredits(cost)}` : 'Заправить бесплатно', 'dock-buy', () =>
          this.handlers.onRefuel(),
        );
        refuel.disabled = cost > credits;
        line.append(refuel);
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

    const ids: string[] = [...Object.keys(cargo.items)];
    for (const id of quotes.keys()) if (!cargo.items[id]) ids.push(id);

    return ids.map((id) => {
      const quote = quotes.get(id) ?? null;
      const have = cargo.items[id] ?? 0;
      const maxBuy = quote && this.canSellHere(quote.id) ? maxBuyable(this.market, rules, quote, credits, free) : 0;
      const maxSell = quote ? have : 0;
      return {
        id,
        have,
        quote,
        sells: maxBuy > 0 || (!!quote && this.canSellHere(quote.id)),
        qty: clampQty(this.qty.get(id) ?? 1, Math.max(maxBuy, maxSell)),
        maxBuy,
        maxSell,
      };
    });
  }

  /** Одна история здешнего торговца, готовой строкой; null — рассказывать нечего. */
  private rumour(): string | null {
    const rules = this.loot;
    const first = this.quotes?.rumours?.[0];
    if (!rules || !first) return null;
    return rumourLine(first, lootItem(rules, first.good)?.name ?? first.good);
  }

  /** Станция продаёт этот товар: покупают у неё только то, что она делает сама. */
  private canSellHere(id: string): boolean {
    // Без правил рынка (сервер без market.json) станция ничего не продаёт — как до M12.
    return this.market.station ? (this.market.station.produces ?? []).includes(id) : false;
  }

  /** Рынок станции (M12): в одном списке и покупка, и продажа. */
  private renderCargo(body: HTMLElement): void {
    const cargo = this.cargo;
    const rules = this.loot;
    if (!cargo || !rules) return;
    body.append(el('div', 'dock-note', `Трюм ${round(cargo.used)} / ${round(cargo.max)}`));
    if (cargo.reserved > 0) body.append(el('div', 'dock-note', `Из них груз задания — ${cargo.reserved} ед.: не продаётся`));
    if (!rules.stationUnload) {
      body.append(el('div', 'dock-note', 'Станция сейчас груз не принимает'));
      return;
    }

    const rows = this.marketRows(cargo.credits);
    if (rows.length === 0) {
      body.append(el('div', 'dock-empty', 'Трюм пуст, и торговать здесь нечем. Груз добывают с пиратов, метеоритов и из контейнеров.'));
      return;
    }
    // Та же реплика торговца, что стоит под его картинкой, — для телефона, где сцены нет совсем.
    // На широком экране её прячет CSS тем же брейкпоинтом, которым показывает сцену: дважды не повторяем.
    const rumour = this.rumour();
    if (rumour) body.append(el('div', 'dock-rumour', rumour));

    // «Продать всё» — первым делом: с полным трюмом в док заходят чаще, чем за покупками.
    // Считает по здешним ценам и не трогает то, чего тут не берут.
    const sellable = rows.filter((r) => r.maxSell > 0 && r.quote);
    if (sellable.length > 1) {
      let total = 0;
      for (const r of sellable) {
        total += tradeCost(this.market, r.id, lootItem(rules, r.id)?.price ?? 0, r.quote!.stock, r.have, false);
      }
      body.append(button(`Продать всё · ${formatCredits(total)}`, 'dock-buy dock-sell-all', () => this.handlers.onSell()));
    }
    for (const row of rows) body.append(this.marketRow(row, rules));
  }

  private marketRow(row: MarketRow, rules: LootRules): HTMLElement {
    const item = lootItem(rules, row.id);
    const basePrice = item?.price ?? 0;
    const view = el('div', 'dock-row dock-market-row');
    view.append(icon(itemSprite(row.id)));

    const name = el('div', 'dock-name', item?.name ?? row.id);
    name.style.color = color(rarityColor(rules, row.id));
    if (row.have > 0) name.append(el('span', 'dock-market-have', ` в трюме ${row.have}`));
    view.append(name);

    if (!row.quote) {
      // Товар есть, но станция им не торгует: чужой регион или контрабанда.
      view.append(el('div', 'dock-tag', 'Здесь этим не торгуют'));
      return view;
    }

    view.append(this.priceLine(row.quote, basePrice));
    const max = Math.max(row.maxBuy, row.maxSell);
    if (max > 0) view.append(this.stepper(row, max));

    const actions = el('div', 'dock-market-actions');
    if (row.maxBuy > 0) {
      const count = Math.min(row.qty, row.maxBuy);
      const cost = tradeCost(this.market, row.id, basePrice, row.quote.stock, count, true);
      actions.append(button(`Купить ${count} · ${formatCredits(cost)}`, 'dock-buy', () => this.handlers.onBuyGoods(row.id, count)));
    } else if (row.sells) {
      // Продают, но прямо сейчас нельзя: пусто на складе, нет места или не хватает кредитов.
      actions.append(el('div', 'dock-tag', row.quote.stock <= 0 ? 'Склад пуст' : 'Не по карману'));
    }
    if (row.maxSell > 0) {
      const count = Math.min(row.qty, row.maxSell);
      const gain = tradeCost(this.market, row.id, basePrice, row.quote.stock, count, false);
      actions.append(button(`Продать ${count} · ${formatCredits(gain)}`, 'dock-buy', () => this.handlers.onSell(row.id, count)));
    }
    if (actions.childElementCount > 0) view.append(actions);
    return view;
  }

  /** Цена штуки, стрелка «дороже/дешевле обычного» и намёк на склад станции. */
  private priceLine(quote: MarketItemDto, basePrice: number): HTMLElement {
    const line = el('div', 'dock-stats dock-market-price');
    const where = trend(quote.buy, quote.sell, basePrice);
    const arrow = where === 'up' ? '▲' : where === 'down' ? '▼' : '';
    const price = el('span', 'dock-market-rate', `${formatCredits(quote.sell)} / ${formatCredits(quote.buy)}`);
    price.title = 'Станция покупает / продаёт за штуку';
    line.append(price);
    if (arrow) {
      const mark = el('span', `dock-market-trend dock-market-${where}`, ` ${arrow}`);
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
    const box = el('div', 'dock-qty');
    const set = (value: number): void => {
      this.qty.set(row.id, clampQty(value, max));
      this.render();
    };
    box.append(button('−', 'dock-qty-step', () => set(row.qty - 1)));
    box.append(el('div', 'dock-qty-value', String(row.qty)));
    box.append(button('+', 'dock-qty-step', () => set(row.qty + 1)));
    if (max > 10) box.append(button('+10', 'dock-qty-step', () => set(row.qty + 10)));
    box.append(button('Макс', 'dock-qty-step', () => set(max)));
    return box;
  }

  /** Обучение (GDD §54), своё задание и доска станции (§36). */
  private renderMissions(body: HTMLElement, hangar: HangarMsg): void {
    const missions = this.missions;
    if (!missions) return;
    const tutorial = missions.tutorial;
    if (tutorial) {
      const box = el('div', 'dock-mission dock-tutorial');
      box.append(el('div', 'dock-mission-head', `Обучение · шаг ${tutorial.step + 1} из ${tutorial.total}`));
      box.append(el('div', 'dock-name', tutorial.title));
      if (tutorial.hint) box.append(el('div', 'dock-stats', keyHint(tutorial.hint, keymap)));
      const actions = el('div', 'dock-mission-actions');
      if (tutorial.id === 'undock') actions.append(button('Вылет', 'dock-buy', () => this.handlers.onUndock()));
      actions.append(button('Пропустить обучение', 'dock-link', () => this.handlers.onSkipTutorial()));
      box.append(actions);
      body.append(box);
    }

    const active = missions.active;
    if (active) {
      const box = el('div', 'dock-mission');
      box.append(el('div', 'dock-mission-head', `Задание · награда ${formatCredits(active.offer.reward)}`));
      box.append(el('div', 'dock-name', activeLine(active, this.names)));
      box.append(el('div', 'dock-stats', activeHint(active, this.here, hangar.docked, this.names)));
      if (active.until) {
        // Срок идёт, пока пилот торгуется на станции: цифра живая, её двигает tick().
        this.timer = el('div', 'dock-timer');
        box.append(this.timer);
        this.tick(Date.now());
      }
      const actions = el('div', 'dock-mission-actions');
      if (active.offer.kind === 'collect') {
        const give = button('Сдать', 'dock-buy', () => this.handlers.onComplete());
        give.disabled = active.progress < active.offer.count;
        actions.append(give);
      }
      actions.append(button('Отказаться', 'dock-link', () => this.handlers.onAbandon()));
      box.append(actions);
      body.append(box);
    }

    if (missions.offers.length === 0) {
      if (!active) body.append(el('div', 'dock-empty', 'Заданий на этой станции нет.'));
      return;
    }
    body.append(el('div', 'dock-note', active ? 'Доска станции: сначала сдайте или бросьте своё задание' : 'Доска станции'));
    for (const offer of missions.offers) body.append(this.missionRow(offer, active !== null));
  }

  private missionRow(offer: MissionOffer, busy: boolean): HTMLElement {
    const row = el('div', 'dock-row');
    row.dataset.state = busy ? 'poor' : 'buy';
    row.append(el('div', 'dock-name', offerTitle(offer, this.names)), el('div', 'dock-stats', offerNote(offer)));
    const take = button(`Взять · ${formatCredits(offer.reward)}`, 'dock-buy', () => this.handlers.onAccept(offer.id));
    take.disabled = busy;
    row.append(take);
    return row;
  }

  private renderHulls(body: HTMLElement, hangar: HangarMsg, credits: number): void {
    if (this.modules.enabled) body.append(el('div', 'dock-note', 'Щит, радар, бак и двигатель — модули: они переходят на новый корпус'));
    for (const id of this.hulls.ids()) {
      const hull = this.hulls.get(id);
      const slots = hullSlots(hull).join(' ');
      const utility = hullUtilitySlots(hull);
      const own = this.modules.enabled
        ? [`класс ${hull.class ?? 'L'}`, `пушки ${slots}`, utility ? `вспомогательных ${utility}` : '']
        : [`щит ${hull.shield}`, hull.fuel ? `бак ${hull.fuel}` : '', hull.radar ? `радар ${hull.radar}` : ''];
      const stats = [`корпус ${hull.hp} · скорость ${hull.maxSpeed} · трюм ${hull.cargo}`, ...own.filter(Boolean)].join(' · ');
      // Здесь продают не всё (M11): чего нет в ассортименте станции, то и не купить.
      const listed = sells(this.shop, id, this.shop.hulls) ? price(this.shop.hulls, id) : null;
      const cost = listed === null ? null : this.repCost(listed);
      const state = offerState(hangar.hulls.includes(id), id === hangar.hull, cost, credits, !this.repAllows(id, true));
      const name = hull.role ? `${hull.name} — ${hull.role}` : hull.name;
      body.append(this.offer(id, name, stats, state));
    }
  }

  /**
   * Оснащение (GDD §13, §18–20): оружейные слоты корпуса и модули, энергия генератора, склад. Тап по слоту
   * открывает под ним всё, что туда встаёт: со склада — поставить, из магазина — купить и сразу поставить.
   */
  private renderFitting(body: HTMLElement, hangar: HangarMsg, credits: number): void {
    const hull = this.hulls.get(hangar.hull);
    const powerMax = hangar.powerMax ?? 0;
    if (powerMax > 0) {
      const power = hangar.power ?? 0;
      const bar = el('div', 'dock-power');
      const fill = el('div', 'dock-power-fill');
      fill.style.width = `${Math.min(100, (100 * power) / powerMax)}%`;
      bar.append(fill, el('div', 'dock-power-text', `Энергия ${power} / ${powerMax}`));
      body.append(bar);
    }

    body.append(el('div', 'dock-note', `Оружие · слотов ${hullSlots(hull).length}`));
    hullSlots(hull).forEach((slotClass, i) => this.slotRow(body, hangar, credits, weaponSlot(i), `Слот ${i + 1} · ${slotClass}`));
    if (this.modules.enabled) {
      body.append(el('div', 'dock-note', `Модули · класс корпуса ${hull.class ?? 'L'}`));
      for (const slot of MODULE_SLOTS) this.slotRow(body, hangar, credits, slot, SLOT_NAMES[slot]);
      const utility = hullUtilitySlots(hull);
      if (utility > 0) {
        body.append(el('div', 'dock-note', `Вспомогательные · слотов ${utility}`));
        for (let i = 0; i < utility; i++) {
          this.slotRow(body, hangar, credits, utilitySlot(i), `${SLOT_NAMES[UTILITY]} ${i + 1}`);
        }
      }
    }

    const stored = Object.entries(hangar.storage ?? {}).filter(([, count]) => count > 0);
    if (hangar.guest) return;
    body.append(el('div', 'dock-note', 'Склад станции'));
    if (stored.length === 0) {
      body.append(el('div', 'dock-empty', 'Пусто. Снятое с корабля и купленное про запас лежит здесь.'));
      return;
    }
    for (const [id, count] of stored) {
      const row = el('div', 'dock-row');
      const picture = this.picture(id);
      if (picture) row.append(icon(picture));
      const stock = el('div', 'dock-name', `${this.itemName(id)} ×${count}`);
      const mark = tierBadge(id);
      if (mark) stock.append(el('span', 'dock-tier', mark));
      row.append(stock, el('div', 'dock-stats', this.itemLabel(id)));
      const cost = sellPrice(this.shop, id);
      row.append(button(cost > 0 ? `Продать · ${formatCredits(cost)}` : 'Выбросить', 'dock-buy', () => this.handlers.onSellItem(id)));
      body.append(row);
    }
  }

  /** Строка слота: что стоит; открытый слот — ещё и список того, что туда встаёт. */
  private slotRow(body: HTMLElement, hangar: HangarMsg, credits: number, slot: string, label: string): void {
    const hull = this.hulls.get(hangar.hull);
    const current = fitGet(hangar.fit, slot);
    const open = this.slot === slot;
    const row = el('div', 'dock-row dock-slot');
    row.dataset.state = open ? 'active' : current ? 'owned' : 'none';
    const picture = current ? this.picture(current) : null;
    if (picture) row.append(icon(picture));
    row.append(
      el('div', 'dock-name', `${label}: ${current ? this.itemName(current) : 'пусто'}`),
      el('div', 'dock-stats', current ? this.itemLabel(current) : 'Свободный слот'),
    );
    row.append(
      button(open ? 'Закрыть' : current ? 'Сменить' : 'Выбрать', 'dock-buy', () => {
        this.slot = open ? null : slot;
        this.render();
      }),
    );
    body.append(row);
    if (!open) return;

    const list = el('div', 'dock-slot-list');
    if (current && !(REQUIRED_SLOTS as string[]).includes(slot)) {
      list.append(button('Снять на склад', 'dock-link', () => this.handlers.onFit(slot, null)));
    }
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
      const item = el('div', 'dock-row');
      item.dataset.state =
        offer.action === 'installed' ? 'active'
        : offer.action === 'install' ? 'owned'
        : offer.action === 'none' ? 'locked'
        : offer.poor || offer.problem ? 'poor'
        : 'buy';
      const picture = this.picture(id);
      if (picture) item.append(icon(picture));
      const name = offer.action === 'install' && Number.isFinite(stored) ? `${this.itemName(id)} · на складе ${stored}` : this.itemName(id);
      const title = el('div', 'dock-name', name);
      const badge = tierBadge(id);
      if (badge) title.append(el('span', 'dock-tier', badge));
      item.append(title, el('div', 'dock-stats', this.itemLabel(id)));
      switch (offer.action) {
        case 'installed':
          item.append(el('div', 'dock-tag', 'Стоит'));
          break;
        case 'install': {
          const put = button(offer.problem ? describeFitProblem(offer.problem) : 'Поставить', 'dock-buy', () => this.handlers.onFit(slot, id));
          put.disabled = offer.problem !== null;
          item.append(put);
          break;
        }
        case 'buy': {
          const text = offer.problem ? describeFitProblem(offer.problem) : `Купить · ${formatCredits(offer.cost)}`;
          const buy = button(text, 'dock-buy', () => this.handlers.onBuy('item', id, slot));
          buy.disabled = offer.poor || offer.problem !== null;
          item.append(buy);
          break;
        }
        case 'none':
          // Витрину не прячем: пусть видно, что здесь есть и чего это стоит добиться.
          item.append(el('div', 'dock-tag', repGateNote(this.repRules, this.rep?.here?.level, id, false) ?? 'Только для своих'));
          break;
      }
      list.append(item);
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
    const row = el('div', 'dock-row');
    row.dataset.state = state;
    row.addEventListener('mouseenter', () => this.previewHull(id));
    row.addEventListener('mouseleave', () => this.previewHull(null));
    row.append(icon(shipSprite(id)));
    row.append(el('div', 'dock-name', name), el('div', 'dock-stats', stats));
    const cost = this.repCost(price(this.shop.hulls, id) ?? 0);
    switch (state) {
      case 'active':
        row.append(el('div', 'dock-tag', 'На корабле'));
        break;
      case 'owned':
        row.append(button('Поставить', 'dock-buy', () => this.handlers.onEquip(id)));
        break;
      case 'buy':
      case 'poor': {
        const buy = button(`Купить · ${formatCredits(cost)}`, 'dock-buy', () => this.handlers.onBuy('hull', id));
        buy.disabled = state === 'poor';
        row.append(buy);
        break;
      }
      case 'locked':
        row.append(el('div', 'dock-tag', repGateNote(this.repRules, this.rep?.here?.level, id, true) ?? 'Только для своих'));
        break;
      case 'none':
        row.append(el('div', 'dock-tag', 'Здесь нет'));
        break;
    }
    return row;
  }
}

/** Строка характеристик пушки на витрине: класс, урон, темп, точность (у ракетницы — самонаведение), дальность, энергия. */
export function weaponLabel(weapon: WeaponParams): string {
  const aim = weapon.missile ? 'самонаведение' : `точность ${weapon.accuracy}%`;
  const parts = [`урон ${weapon.damage}`, `раз в ${weapon.cooldown} с`, aim, `дальность ${weapon.maxRange}`];
  if (weapon.class) parts.unshift(weapon.class);
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
