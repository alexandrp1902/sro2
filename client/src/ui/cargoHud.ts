import { gearIcon, spriteUrl } from '../render/sprites';
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

/** Выбранные врата: куда ведут, далеко ли, идёт ли подготовка прыжка. */
export interface GateCardState {
  kind: 'gate';
  /** Система за вратами. */
  name: string;
  distance: number;
  inRange: boolean;
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

/** Подсказка под именем врат: что сейчас мешает прыжку. Топлива прыжок не стоит (M15.6). */
export function gateHint(card: GateCardState): string {
  if (card.charging !== null) return `прыжок через ${Math.ceil(card.charging)} с`;
  return card.inRange ? 'прыжок готов' : 'подлетите ближе, чтобы прыгнуть';
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
  /** Корабль в доке: там за борт бросать некуда, и кнопок выброса нет (M15.1). */
  private docked = false;

  /**
   * @param onClearSelection ✕ на карточке: снять выбранный предмет или станцию
   * @param onJettison выбросить стопку за борт; null — выброс недоступен (нет связи)
   */
  constructor(
    private readonly root: HTMLElement,
    private readonly lootRoot: HTMLElement,
    private readonly onClearSelection: () => void,
    private readonly onJettison: ((item: string) => void) | null = null,
  ) {
    this.root.addEventListener('click', () => this.setOpen(!this.open));
  }

  /** В доке выброс не показываем: там груз продают. */
  setDocked(docked: boolean): void {
    if (docked === this.docked) return;
    this.docked = docked;
    this.lastKey = ''; // список перестроится: кнопки появляются и исчезают
    this.render();
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
      this.distanceEl = row('loot-distance sro-num sro-muted', '');
      this.lootRoot.replaceChildren();
      if (card.kind === 'loot') {
        const item = lootItem(rules!, card.item);
        const count = card.count > 1 ? ` ×${card.count}` : '';
        // Редкость — цвет данных, он остаётся; станция, врата и планета зовутся просто сильным текстом.
        this.lootRoot.append(row('loot-name sro-strong', `${item?.name ?? card.item}${count}`, color(rarityColor(rules!, card.item))));
        this.lootRoot.append(this.distanceEl);
      } else if (card.kind === 'planet') {
        const name = card.settlement ? `Поселение «${card.settlement}»` : `Планета ${card.name}`;
        this.lootRoot.append(row('loot-name sro-strong', name), this.distanceEl);
        this.lootRoot.append(row(hintClass(!!card.settlement && card.inRange), planetHint(card)));
      } else if (card.kind === 'gate') {
        this.lootRoot.append(row('loot-name sro-strong', `Врата → ${card.name}`), this.distanceEl);
        this.lootRoot.append(row(hintClass(card.inRange && card.charging === null), gateHint(card)));
      } else {
        this.lootRoot.append(row('loot-name sro-strong', 'Станция'), this.distanceEl);
        this.lootRoot.append(row(hintClass(card.inRange), card.inRange ? 'можно в док' : 'подлетите ближе, чтобы пристыковаться'));
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
    head.append(row('cargo-title sro-label', `Трюм ${round(state.used)} / ${round(state.max)}`));
    head.append(row('cargo-credits sro-num sro-gain', formatCredits(state.credits)));
    this.root.append(head);

    const bar = document.createElement('div');
    bar.className = state.used > state.max ? 'sro-bar sro-bar--thin sro-bar--over' : 'sro-bar sro-bar--thin sro-bar--cargo';
    const fill = document.createElement('div');
    fill.className = 'sro-bar__fill';
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
      icon.src = spriteUrl(gearIcon(rules, id));
      icon.alt = '';
      line.append(dot, icon, document.createTextNode(`${lootItem(rules, id)?.name ?? id} ×${count}`));
      // Выбросить стопку целиком (M15.1): в полёте — освободить место, когда трюм забит не тем.
      if (this.onJettison && !this.docked) {
        const out = document.createElement('button');
        out.className = 'cargo-drop';
        out.type = 'button';
        out.textContent = '✕';
        out.title = `Выбросить: ${lootItem(rules, id)?.name ?? id} ×${count}`;
        out.setAttribute('aria-label', out.title);
        out.addEventListener('click', (e) => {
          e.stopPropagation(); // клик по трюму сворачивает список — выброс не должен его закрывать
          this.onJettison?.(id);
        });
        line.append(out);
      }
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

/** Подсказка карточки: зелёная («ok») только когда цель готова — можно в док, прыгать, садиться. */
function hintClass(ready: boolean): string {
  return ready ? 'loot-hint sro-ok' : 'loot-hint sro-muted';
}

function clearButton(onClear: () => void): HTMLElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'loot-clear sro-btn sro-btn--ghost sro-btn--xs';
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
