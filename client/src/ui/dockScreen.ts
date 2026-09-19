import type { BuyKind, HangarMsg, MissionOffer, MissionsMsg } from '../net/protocol';
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
import { activeHint, activeLine, offerNote, offerTitle, type MissionNames } from '../sim/missions';
import { lootItem, rarityColor, type LootRules } from '../sim/loot';
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
  /** Не продаётся. */
  | 'none';

export function offerState(owned: boolean, active: boolean, cost: number | null, credits: number): OfferState {
  if (active) return 'active';
  if (owned) return 'owned';
  if (cost === null) return 'none';
  return cost <= credits ? 'buy' : 'poor';
}

export type Tab = 'missions' | 'cargo' | 'hulls' | 'fitting';

const TABS: { id: Tab; label: string }[] = [
  { id: 'missions', label: 'Задания' },
  { id: 'cargo', label: 'Груз' },
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
  cargo: { art: 'trader', who: 'Торговец', line: 'Показывайте, что привезли. Честная цена — моя цена.', ship: false },
  hulls: { art: 'shipyard', who: 'Мастер верфи', line: 'Корпус выбирают под задачу, а не под мечту.', ship: true },
  fitting: { art: 'hangar', who: '', line: '', ship: true },
};

/** Адрес фона сцены: относительный, как и спрайты. */
export function sceneUrl(place: Place, tab: Tab): string {
  return `dock/${place}-${SCENES[tab].art}.webp`;
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

export interface DockHandlers {
  /** Продать груз: item — что именно, без него — весь трюм. */
  onSell(item?: string): void;
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
  /** Слот, для которого открыт список пушек или модулей; null — ни один. */
  private slot: string | null = null;

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

  setRules(loot: LootRules | undefined, shop: ShopRules | undefined): void {
    this.loot = loot ?? null;
    this.shop = shop ?? NO_SHOP;
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

  /** Имя станции в заголовке — по системе: «Станция Vega». */
  setStation(name: string | null, system: string | null = null): void {
    this.station = name ? `Станция ${name}` : 'Станция';
    this.here = system;
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

  private render(): void {
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
   * Сцена дока — только на широком экране (на телефоне её прячет CSS, и фон там не грузится: картинка
   * задана переменной, которую читает лишь правило для ПК). Пока фона нет, под ним видна заливка сцены.
   */
  private scene(hangar: HangarMsg): HTMLElement {
    const scene = SCENES[this.tab];
    const view = el('div', 'dock-scene');
    view.dataset.scene = scene.art;
    // Абсолютный адрес: относительный url() в CSS-переменной браузер отсчитывает от файла стилей (assets/), а не от страницы.
    view.style.setProperty('--scene-art', `url("${new URL(sceneUrl(this.place, this.tab), document.baseURI).href}")`);
    if (scene.ship) {
      const ship = icon(shipSprite(hangar.hull));
      ship.className = 'dock-scene-ship';
      view.append(ship);
    }
    if (scene.who) {
      const caption = el('div', 'dock-scene-caption');
      caption.append(el('div', 'dock-scene-who', scene.who), el('div', 'dock-scene-line', scene.line));
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
      const cost = repairCost(this.shop, missing);
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

  private renderCargo(body: HTMLElement): void {
    const cargo = this.cargo;
    const rules = this.loot;
    if (!cargo || !rules) return;
    body.append(el('div', 'dock-note', `Трюм ${round(cargo.used)} / ${round(cargo.max)}`));
    if (cargo.reserved > 0) body.append(el('div', 'dock-note', `Из них груз задания — ${cargo.reserved} ед.: не продаётся`));
    const items = Object.entries(cargo.items);
    if (items.length === 0) {
      body.append(el('div', 'dock-empty', 'Трюм пуст. Груз добывают с пиратов, метеоритов и из контейнеров.'));
      return;
    }
    if (!rules.stationUnload) body.append(el('div', 'dock-note', 'Станция сейчас груз не принимает'));
    let total = 0;
    for (const [id, count] of items) {
      const item = lootItem(rules, id);
      const sum = (item?.price ?? 0) * count;
      total += sum;
      const row = el('div', 'dock-row');
      row.append(icon(itemSprite(id)));
      const name = el('div', 'dock-name', `${item?.name ?? id} ×${count}`);
      name.style.color = color(rarityColor(rules, id));
      row.append(name, el('div', 'dock-stats', `${formatCredits(item?.price ?? 0)} за шт.`));
      if (rules.stationUnload) row.append(button(`Продать · ${formatCredits(sum)}`, 'dock-buy', () => this.handlers.onSell(id)));
      body.append(row);
    }
    if (rules.stationUnload && items.length > 1) {
      body.append(button(`Продать всё · ${formatCredits(total)}`, 'dock-buy dock-sell-all', () => this.handlers.onSell()));
    }
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
      const cost = sells(this.shop, id, this.shop.hulls) ? price(this.shop.hulls, id) : null;
      const state = offerState(hangar.hulls.includes(id), id === hangar.hull, cost, credits);
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
      const cost = sells(this.shop, id, this.shop.items) ? price(this.shop.items, id) : null;
      const offer = slotOffer(id === current, stored, cost, credits, problem);
      if (offer.action === 'none') continue;
      const item = el('div', 'dock-row');
      item.dataset.state = offer.action === 'installed' ? 'active' : offer.action === 'install' ? 'owned' : offer.poor || offer.problem ? 'poor' : 'buy';
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
    const cost = price(this.shop.hulls, id) ?? 0;
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
