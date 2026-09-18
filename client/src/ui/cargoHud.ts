import { lootItem, rarityColor, type LootRules } from '../sim/loot';

/** Трюм игрока, как его прислал сервер (GDD §21). */
export interface CargoState {
  used: number;
  max: number;
  items: Record<string, number>;
  credits: number;
}

/** Выбранный предмет: что это и далеко ли до него. */
export interface LootCardState {
  item: string;
  count: number;
  distance: number;
}

/**
 * Трюм и карточка выбранного предмета. Отдельно от боевого HUD: тот про бой и обновляется каждый кадр,
 * а трюм приходит событием и живёт своей жизнью.
 * На узком экране трюм свёрнут — левая колонка и так занята статусом, полётом, полосками и лентой.
 */
export class CargoHud {
  private state: CargoState | null = null;
  private rules: LootRules | null = null;
  private open = false;
  private atStation = false;
  private lastKey = '';

  constructor(
    private readonly root: HTMLElement,
    private readonly lootRoot: HTMLElement,
    private readonly onClearLoot: () => void,
    private readonly onSell: (item?: string) => void,
  ) {
    this.root.addEventListener('click', (e) => {
      if (e.target instanceof HTMLButtonElement) return; // клик по кнопке продажи панель не сворачивает
      this.setOpen(!this.open);
    });
  }

  setRules(rules: LootRules | undefined): void {
    this.rules = rules ?? null;
    this.lastKey = ''; // названия и редкость могли поменяться на лету
    this.render();
  }

  setCargo(state: CargoState | null): void {
    this.state = state;
    this.render();
  }

  /** В круге станции трюм превращается в прилавок: у каждого груза появляется цена и кнопка продажи. */
  setAtStation(at: boolean): void {
    if (at === this.atStation) return;
    this.atStation = at;
    if (at) this.setOpen(true); // иначе кнопки продажи остались бы спрятанными в свёрнутой панели
    this.lastKey = '';
    this.render();
  }

  /** Карточка выбранного предмета; null — предмет не выбран. */
  update(loot: LootCardState | null): void {
    if (!loot || !this.rules) {
      this.lootRoot.hidden = true;
      return;
    }
    const item = lootItem(this.rules, loot.item);
    const name = item?.name ?? loot.item;
    const count = loot.count > 1 ? ` ×${loot.count}` : '';
    this.lootRoot.hidden = false;
    this.lootRoot.innerHTML = '';
    this.lootRoot.append(
      row('loot-name', `${name}${count}`, color(rarityColor(this.rules, loot.item))),
      row('loot-distance', `${Math.round(loot.distance)} м`),
      clearButton(this.onClearLoot),
    );
  }

  private render(): void {
    const { state, rules } = this;
    if (!state || !rules) {
      this.root.hidden = true;
      return;
    }

    const key = `${state.used}|${state.max}|${state.credits}|${this.atStation}|${JSON.stringify(state.items)}`;
    if (key === this.lastKey) return;
    this.lastKey = key;

    this.root.hidden = false;
    // Перегруз возможен после пересадки на корпус поменьше: груз не выбрасывается (GDD §24).
    this.root.dataset.over = String(state.used > state.max);
    this.root.innerHTML = '';

    const head = document.createElement('div');
    head.className = 'cargo-head';
    head.append(row('cargo-title', `Трюм ${round(state.used)} / ${round(state.max)}`));
    if (state.credits > 0) head.append(row('cargo-credits', `${state.credits} кр`));
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
      line.append(dot, document.createTextNode(`${lootItem(rules, id)?.name ?? id} ×${count}`));
      if (this.atStation) {
        const price = (lootItem(rules, id)?.price ?? 0) * count;
        line.append(sellButton(`${price} кр`, () => this.onSell(id)));
      }
      list.append(line);
    }
    this.root.append(list);

    if (this.atStation && Object.keys(state.items).length > 1) {
      const all = sellButton(`Продать всё · ${totalPrice(state, rules)} кр`, () => this.onSell());
      all.classList.add('cargo-sell-all');
      this.root.append(all);
    }
  }

  private setOpen(open: boolean): void {
    this.open = open;
    this.root.dataset.open = String(open);
  }
}

function sellButton(label: string, onClick: () => void): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'cargo-sell';
  button.textContent = label;
  button.addEventListener('click', (e) => {
    e.stopPropagation();
    button.blur(); // иначе Space «нажмёт» кнопку вместо подбора или огня
    onClick();
  });
  return button;
}

function totalPrice(state: CargoState, rules: LootRules): number {
  let total = 0;
  for (const [id, count] of Object.entries(state.items)) total += (lootItem(rules, id)?.price ?? 0) * count;
  return total;
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
  button.setAttribute('aria-label', 'Снять выбор предмета');
  button.textContent = '✕';
  button.addEventListener('click', onClear);
  return button;
}

function color(value: number): string {
  return `#${value.toString(16).padStart(6, '0')}`;
}

/** Объём показываем без хвоста «.0»: 12, а не 12.0. */
function round(value: number): string {
  return String(Math.round(value * 10) / 10);
}
