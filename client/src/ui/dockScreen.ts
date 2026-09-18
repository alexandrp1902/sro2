import type { BuyKind, HangarMsg } from '../net/protocol';
import type { WeaponParams } from '../sim/combat';
import type { Hulls } from '../sim/hulls';
import { lootItem, rarityColor, type LootRules } from '../sim/loot';
import { NO_SHOP, formatCredits, price, repairCost, type ShopRules } from '../sim/shop';
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

type Tab = 'cargo' | 'hulls' | 'weapons';

const TABS: { id: Tab; label: string }[] = [
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
  onUndock(): void;
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

  constructor(
    private readonly root: HTMLElement,
    private readonly hulls: Hulls,
    private readonly weapons: Weapons,
    private readonly handlers: DockHandlers,
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
    if (hangar?.docked && !this.open) this.tab = 'cargo'; // каждый заход в док начинается с груза
    this.hangar = hangar;
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
    head.append(el('div', 'dock-title', 'Станция'), el('div', 'dock-credits', formatCredits(credits)));
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
    if (this.tab === 'cargo') this.renderCargo(body);
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
    return line;
  }

  private renderCargo(body: HTMLElement): void {
    const cargo = this.cargo;
    const rules = this.loot;
    if (!cargo || !rules) return;
    body.append(el('div', 'dock-note', `Трюм ${round(cargo.used)} / ${round(cargo.max)}`));
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

  private renderHulls(body: HTMLElement, hangar: HangarMsg, credits: number): void {
    for (const id of this.hulls.ids()) {
      const hull = this.hulls.get(id);
      const stats = `корпус ${hull.hp} · щит ${hull.shield} · скорость ${hull.maxSpeed} · трюм ${hull.cargo}`;
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
