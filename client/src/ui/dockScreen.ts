import type { BuyKind, HangarMsg, MissionOffer, MissionsMsg } from '../net/protocol';
import type { WeaponParams } from '../sim/combat';
import { itemSprite, shipSprite, spriteUrl, weaponSprite } from '../render/sprites';
import type { Hulls } from '../sim/hulls';
import { activeHint, activeLine, offerNote, offerTitle, type MissionNames } from '../sim/missions';
import { lootItem, rarityColor, type LootRules } from '../sim/loot';
import { NO_SHOP, formatCredits, fuelCost, price, repairCost, type ShopRules } from '../sim/shop';
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

export type Tab = 'missions' | 'cargo' | 'hulls' | 'weapons';

const TABS: { id: Tab; label: string }[] = [
  { id: 'missions', label: 'Задания' },
  { id: 'cargo', label: 'Груз' },
  { id: 'hulls', label: 'Корабли' },
  { id: 'weapons', label: 'Оружие' },
];

export interface DockHandlers {
  /** Продать груз: item — что именно, без него — весь трюм. */
  onSell(item?: string): void;
  onBuy(kind: BuyKind, id: string): void;
  /** Поставить своё из ангара. */
  onEquip(kind: BuyKind, id: string): void;
  onRepair(): void;
  onRefuel(): void;
  onUndock(): void;
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
  private here: string | null = null;
  private missions: MissionsMsg | null = null;

  constructor(
    private readonly root: HTMLElement,
    private readonly hulls: Hulls,
    private readonly weapons: Weapons,
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
    head.append(el('div', 'dock-title', this.station), el('div', 'dock-credits', formatCredits(credits)));
    head.append(button('Вылет', 'dock-undock', () => this.handlers.onUndock()));
    card.append(head);
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
    else this.renderWeapons(body, hangar, credits);
    card.append(body);

    // Экран перерисовывается целиком после каждой продажи и покупки — прокрутку списка сохраняем.
    const old = this.root.querySelector<HTMLElement>('.dock-body');
    const scroll = old?.dataset.tab === this.tab ? old.scrollTop : 0;
    this.root.replaceChildren(card);
    body.scrollTop = scroll;
  }

  /** Свой корабль: корпус, пушка, прочность и ремонт. */
  private shipLine(hangar: HangarMsg, credits: number): HTMLElement {
    const line = el('div', 'dock-ship');
    const hull = this.hulls.get(hangar.hull);
    const weapon = this.weapons.get(hangar.weapon);
    line.append(el('div', 'dock-ship-name', `${hull.name} · ${weapon.name}`));
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
      if (tutorial.hint) box.append(el('div', 'dock-stats', tutorial.hint));
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
    for (const id of this.hulls.ids()) {
      const hull = this.hulls.get(id);
      const extra = [hull.fuel ? `бак ${hull.fuel}` : '', hull.radar ? `радар ${hull.radar}` : ''].filter(Boolean);
      const stats = [`корпус ${hull.hp} · щит ${hull.shield} · скорость ${hull.maxSpeed} · трюм ${hull.cargo}`, ...extra].join(' · ');
      const state = offerState(hangar.hulls.includes(id), id === hangar.hull, price(this.shop.hulls, id), credits);
      body.append(this.offer('hull', id, hull.name, stats, state));
    }
  }

  private renderWeapons(body: HTMLElement, hangar: HangarMsg, credits: number): void {
    for (const id of this.weapons.ids()) {
      const weapon = this.weapons.get(id);
      const state = offerState(hangar.weapons.includes(id), id === hangar.weapon, price(this.shop.weapons, id), credits);
      body.append(this.offer('weapon', id, weapon.name, weaponLabel(weapon), state));
    }
  }

  private offer(kind: BuyKind, id: string, name: string, stats: string, state: OfferState): HTMLElement {
    const row = el('div', 'dock-row');
    row.dataset.state = state;
    const picture = kind === 'hull' ? shipSprite(id, false) : weaponSprite(id);
    if (picture) row.append(icon(picture));
    row.append(el('div', 'dock-name', name), el('div', 'dock-stats', stats));
    const prices = kind === 'hull' ? this.shop.hulls : this.shop.weapons;
    const cost = price(prices, id) ?? 0;
    switch (state) {
      case 'active':
        row.append(el('div', 'dock-tag', 'На корабле'));
        break;
      case 'owned':
        row.append(button('Поставить', 'dock-buy', () => this.handlers.onEquip(kind, id)));
        break;
      case 'buy':
      case 'poor': {
        const buy = button(`Купить · ${formatCredits(cost)}`, 'dock-buy', () => this.handlers.onBuy(kind, id));
        buy.disabled = state === 'poor';
        row.append(buy);
        break;
      }
      case 'none':
        row.append(el('div', 'dock-tag', 'Не продаётся'));
        break;
    }
    return row;
  }
}

/** Строка характеристик пушки на витрине: урон, темп, точность, дальность. */
export function weaponLabel(weapon: WeaponParams): string {
  return `урон ${weapon.damage} · раз в ${weapon.cooldown} с · точность ${weapon.accuracy}% · дальность ${weapon.maxRange}`;
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
