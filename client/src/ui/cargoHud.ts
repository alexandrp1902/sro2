import { itemSprite, spriteUrl } from '../render/sprites';
import { lootItem, rarityColor, type LootRules } from '../sim/loot';
import { formatCredits } from '../sim/shop';

/** Трюм игрока, как его прислал сервер (GDD §21). */
export interface CargoState {
  used: number;
  max: number;
  items: Record<string, number>;
  credits: number;
  /** Из занятого — груз доставки (GDD §36): не продаётся, место освободится, когда задание сдано. */
  reserved: number;
}

/** Выбранный предмет: что это и далеко ли до него. */
export interface LootCardState {
  kind: 'loot';
  item: string;
  count: number;
  distance: number;
}

/** Выбранная станция: далеко ли и можно ли уже пристыковаться. */
export interface StationCardState {
  kind: 'station';
  distance: number;
  inRange: boolean;
}

/** Выбранные врата: куда ведут, далеко ли, хватит ли топлива, идёт ли подготовка прыжка. */
export interface GateCardState {
  kind: 'gate';
  /** Система за вратами. */
  name: string;
  distance: number;
  inRange: boolean;
  cost: number;
  fuel: number;
  /** Сколько секунд до прыжка; null — подготовки нет. */
  charging: number | null;
}

/** Выбранная планета: как называется, далеко ли и можно ли сесть (M15). */
export interface PlanetCardState {
  kind: 'planet';
  name: string;
  distance: number;
  /** Имя поселения; null — планета необитаема, садиться некуда. */
  settlement: string | null;
  /** Достаточно близко, чтобы сесть. */
  inRange: boolean;
}

/** Подсказка под именем планеты: можно ли сюда сесть и что для этого нужно. */
export function planetHint(card: PlanetCardState): string {
  if (!card.settlement) return 'поселения нет — садиться некуда';
  return card.inRange ? 'можно на посадку' : 'подлетите ближе, чтобы сесть';
}

export type SelectionCardState = LootCardState | StationCardState | GateCardState | PlanetCardState;

/** Зелёный, как круг дока. */
const STATION_COLOR = 0x6fe08a;
/** Фиолетовый, как сами врата. */
const GATE_COLOR = 0xb58cff;
/** Как подпись планеты в мире. */
const PLANET_COLOR = 0x9fc7a8;

/** Подсказка под именем врат: что сейчас мешает прыжку или что он стоит. */
export function gateHint(card: GateCardState): string {
  if (card.charging !== null) return `прыжок через ${Math.ceil(card.charging)} с`;
  if (!card.inRange) return 'подлетите ближе, чтобы прыгнуть';
  if (card.fuel < card.cost) return `не хватает топлива: ${card.fuel} из ${card.cost}`;
  return `прыжок · ${card.cost} топлива из ${card.fuel}`;
}

/**
 * Трюм и карточка выбранного предмета. Отдельно от боевого HUD: тот про бой и обновляется каждый кадр,
 * а трюм приходит событием и живёт своей жизнью.
 * На узком экране трюм свёрнут — левая колонка и так занята статусом, полётом, полосками и лентой.
 * Продают груз в доке станции (ui/dockScreen.ts), а не здесь.
 */
export class CargoHud {
  private state: CargoState | null = null;
  private rules: LootRules | null = null;
  private open = false;
  private lastKey = '';
  /** Взято ли «важное письмо» (M14): оно не предмет трюма, а состояние задания. */
  private letter = false;
  /** Карточка перестраивается, только когда меняется выбор; дистанция — просто текст. */
  private cardKey = '';
  private distanceEl: HTMLElement | null = null;

  /** @param onClearSelection ✕ на карточке: снять выбранный предмет или станцию */
  constructor(
    private readonly root: HTMLElement,
    private readonly lootRoot: HTMLElement,
    private readonly onClearSelection: () => void,
  ) {
    this.root.addEventListener('click', () => this.setOpen(!this.open));
  }

  setRules(rules: LootRules | undefined): void {
    this.rules = rules ?? null;
    this.lastKey = ''; // названия и редкость могли поменяться на лету
    this.cardKey = '';
    this.render();
  }

  setCargo(state: CargoState | null): void {
    this.state = state;
    this.render();
  }

  /** Письмо курьера: места оно не занимает, но в трюме его видно — иначе о нём просто забывают (M14). */
  setLetter(carrying: boolean): void {
    if (this.letter === carrying) return;
    this.letter = carrying;
    this.render();
  }

  /** Карточка выбранного предмета или станции; null — ничего не выбрано. Зовётся каждый кадр. */
  update(card: SelectionCardState | null): void {
    const rules = this.rules;
    if (!card || (card.kind === 'loot' && !rules)) {
      this.lootRoot.hidden = true;
      this.cardKey = '';
      return;
    }
    const key =
      card.kind === 'loot'
        ? `loot|${card.item}|${card.count}`
        : card.kind === 'gate'
          ? `gate|${card.name}|${gateHint(card)}`
          : card.kind === 'planet'
            ? `planet|${card.name}`
            : `station|${card.inRange}`;
    if (key !== this.cardKey) {
      this.cardKey = key;
      this.distanceEl = row('loot-distance', '');
      this.lootRoot.replaceChildren();
      if (card.kind === 'loot') {
        const item = lootItem(rules!, card.item);
        const count = card.count > 1 ? ` ×${card.count}` : '';
        this.lootRoot.append(row('loot-name', `${item?.name ?? card.item}${count}`, color(rarityColor(rules!, card.item))));
        this.lootRoot.append(this.distanceEl);
      } else if (card.kind === 'planet') {
        const name = card.settlement ? `Поселение «${card.settlement}»` : `Планета ${card.name}`;
        this.lootRoot.append(row('loot-name', name, color(PLANET_COLOR)), this.distanceEl);
        this.lootRoot.append(row('loot-hint', planetHint(card)));
      } else if (card.kind === 'gate') {
        this.lootRoot.append(row('loot-name', `Врата → ${card.name}`, color(GATE_COLOR)), this.distanceEl);
        this.lootRoot.append(row('loot-hint', gateHint(card)));
      } else {
        this.lootRoot.append(row('loot-name', 'Станция', color(STATION_COLOR)), this.distanceEl);
        this.lootRoot.append(row('loot-hint', card.inRange ? 'можно в док' : 'подлетите ближе, чтобы пристыковаться'));
      }
      this.lootRoot.append(clearButton(this.onClearSelection));
      this.lootRoot.dataset.kind = card.kind;
      this.lootRoot.hidden = false;
    }
    const distance = `${Math.round(card.distance)} м`;
    if (this.distanceEl && this.distanceEl.textContent !== distance) this.distanceEl.textContent = distance;
  }

  private render(): void {
    const { state, rules } = this;
    if (!state || !rules) {
      this.root.hidden = true;
      return;
    }

    const key = `${state.used}|${state.max}|${state.credits}|${state.reserved}|${this.letter}|${JSON.stringify(state.items)}`;
    if (key === this.lastKey) return;
    this.lastKey = key;

    this.root.hidden = false;
    // Перегруз возможен после пересадки на корпус поменьше: груз не выбрасывается (GDD §24).
    this.root.dataset.over = String(state.used > state.max);
    this.root.innerHTML = '';

    const head = document.createElement('div');
    head.className = 'cargo-head';
    head.append(row('cargo-title', `Трюм ${round(state.used)} / ${round(state.max)}`));
    head.append(row('cargo-credits', formatCredits(state.credits)));
    this.root.append(head);

    const bar = document.createElement('div');
    bar.className = 'cargo-bar';
    const fill = document.createElement('div');
    fill.className = 'cargo-bar-fill';
    fill.style.width = `${Math.min(100, state.max > 0 ? (state.used / state.max) * 100 : 0)}%`;
    bar.append(fill);
    this.root.append(bar);

    const list = document.createElement('div');
    list.className = 'cargo-list';
    for (const [id, count] of Object.entries(state.items)) {
      const line = document.createElement('div');
      line.className = 'cargo-item';
      const dot = document.createElement('span');
      dot.className = 'cargo-dot';
      dot.style.background = color(rarityColor(rules, id));
      const icon = document.createElement('img');
      icon.className = 'cargo-icon';
      icon.src = spriteUrl(itemSprite(id));
      icon.alt = '';
      line.append(dot, icon, document.createTextNode(`${lootItem(rules, id)?.name ?? id} ×${count}`));
      list.append(line);
    }
    if (state.reserved > 0) {
      const line = document.createElement('div');
      line.className = 'cargo-item cargo-mission';
      line.textContent = `Груз задания · ${state.reserved} ед.`;
      list.append(line);
    }
    if (this.letter) {
      const line = document.createElement('div');
      line.className = 'cargo-item cargo-mission';
      const icon = document.createElement('img');
      icon.className = 'cargo-icon';
      icon.src = spriteUrl('item-letter');
      icon.alt = '';
      line.append(icon, document.createTextNode('Письмо · места не занимает'));
      list.append(line);
    }
    this.root.append(list);
  }

  private setOpen(open: boolean): void {
    this.open = open;
    this.root.dataset.open = String(open);
  }
}

function row(className: string, text: string, textColor?: string): HTMLElement {
  const div = document.createElement('div');
  div.className = className;
  div.textContent = text;
  if (textColor) div.style.color = textColor;
  return div;
}

function clearButton(onClear: () => void): HTMLElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'loot-clear';
  button.setAttribute('aria-label', 'Снять выбор');
  button.textContent = '✕';
  button.addEventListener('click', onClear);
  return button;
}

export function color(value: number): string {
  return `#${value.toString(16).padStart(6, '0')}`;
}

/** Объём показываем без хвоста «.0»: 12, а не 12.0. */
export function round(value: number): string {
  return String(Math.round(value * 10) / 10);
}
