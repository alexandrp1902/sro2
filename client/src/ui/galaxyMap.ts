import type { GalaxyDto, GalaxySystemDto } from '../net/protocol';
import { dangerColor, dangerName, hops, jumpCost, jumpOutlook, pvpName, type JumpOutlook } from '../sim/galaxy';
import { color } from './cargoHud';

const SVG = 'http://www.w3.org/2000/svg';

/** Что пилот знает, открывая карту: где он, сколько топлива, где дом. */
export interface GalaxyMapState {
  galaxy: GalaxyDto;
  current: string;
  fuel: number;
  maxFuel: number;
  /** Система последней стыковки: туда корабль вернётся после гибели. */
  home: string | null;
}

const OUTLOOK_TEXT: Record<JumpOutlook, string> = {
  here: 'Вы здесь',
  far: 'Прямого маршрута нет — только через соседние системы',
  noFuel: 'Не хватает топлива на прыжок',
  oneWay: 'Туда хватит, обратно — нет: станции там нет, заправиться негде',
  ok: 'Можно прыгать: подлетите к вратам',
};

/**
 * Карта галактики (GDD §55): системы, маршруты и цена прыжка в топливе. Тап по системе — опасность, PvP,
 * станция и хватит ли топлива. Автопилота нет: карта — чтобы решить, куда лететь, а лететь к вратам — самому.
 */
export class GalaxyMap {
  private state: GalaxyMapState | null = null;
  private selected: string | null = null;

  constructor(private readonly root: HTMLElement) {
    root.addEventListener('pointerdown', (e) => {
      if (e.target === root) this.hide(); // тап мимо карточки закрывает карту
    });
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  toggle(): void {
    if (this.open) this.hide();
    else this.show();
  }

  show(): void {
    if (!this.state) return;
    this.root.hidden = false;
    this.render();
  }

  hide(): void {
    this.root.hidden = true;
    this.selected = null;
  }

  /** Новые данные: вошли в систему, заправились, прыгнули. Открытая карта перерисовывается. */
  set(state: GalaxyMapState | null): void {
    const key = JSON.stringify(state);
    if (key === JSON.stringify(this.state)) return;
    if (state?.current !== this.state?.current) this.selected = null;
    this.state = state;
    if (!state) this.hide();
    else if (this.open) this.render();
  }

  private render(): void {
    const state = this.state;
    if (!state) return;
    const { galaxy, current } = state;

    const card = el('div', 'galaxy-card');
    const head = el('div', 'galaxy-head');
    head.append(el('div', 'galaxy-title', 'Карта галактики'), el('div', 'galaxy-fuel', `Топливо ${state.fuel} / ${state.maxFuel}`));
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'galaxy-close';
    close.setAttribute('aria-label', 'Закрыть карту');
    close.textContent = '✕';
    close.addEventListener('click', () => this.hide());
    head.append(close);
    card.append(head);

    const svg = document.createElementNS(SVG, 'svg');
    svg.setAttribute('class', 'galaxy-svg');
    svg.setAttribute('viewBox', '0 0 100 100');
    const byId = new Map(galaxy.systems.map((s) => [s.id, s]));

    for (const link of galaxy.links) {
      const a = byId.get(link.a);
      const b = byId.get(link.b);
      if (!a || !b) continue;
      const fromHere = link.a === current || link.b === current;
      const affordable = state.fuel >= link.cost;
      const line = svgEl('line', {
        x1: a.x,
        y1: a.y,
        x2: b.x,
        y2: b.y,
        class: `galaxy-link${fromHere ? (affordable ? ' galaxy-link-open' : ' galaxy-link-poor') : ''}`,
      });
      const cost = svgEl('text', { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 - 1.2, class: 'galaxy-cost' });
      cost.textContent = String(link.cost);
      svg.append(line, cost);
    }

    for (const system of galaxy.systems) {
      const group = svgEl('g', { class: 'galaxy-node', 'data-id': system.id });
      if (system.id === current) group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 6.5, class: 'galaxy-here' }));
      if (system.id === this.selected) group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 5.6, class: 'galaxy-selected' }));
      group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 4, fill: color(dangerColor(system.danger)) }));
      if (system.station) {
        group.append(svgEl('rect', { x: system.x - 1.4, y: system.y - 1.4, width: 2.8, height: 2.8, class: 'galaxy-station' }));
      }
      const name = svgEl('text', { x: system.x, y: system.y + 8.2, class: 'galaxy-name' });
      name.textContent = system.id === state.home ? `${system.name} ⌂` : system.name;
      group.append(name);
      // Зона тапа крупнее кружка: пальцем по кружку в 8 единиц на телефоне не попасть.
      group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 9, class: 'galaxy-hit' }));
      group.addEventListener('click', () => {
        this.selected = system.id;
        this.render();
      });
      svg.append(group);
    }
    card.append(svg);
    card.append(this.info(byId.get(this.selected ?? current), state));
    card.append(el('div', 'galaxy-legend', 'Цвет — опасность, квадрат — станция, ⌂ — где вы появитесь после гибели. Числа — топливо на прыжок.'));
    this.root.replaceChildren(card);
  }

  /** Карточка выбранной системы (по умолчанию — текущей). */
  private info(system: GalaxySystemDto | undefined, state: GalaxyMapState): HTMLElement {
    const box = el('div', 'galaxy-info');
    if (!system) return box;
    const title = el('div', 'galaxy-info-name', system.name);
    title.style.color = color(dangerColor(system.danger));
    box.append(title);
    const facts = [dangerName(system.danger), pvpName(system.pvp), system.station ? 'есть станция' : 'станции нет'];
    box.append(el('div', 'galaxy-info-facts', facts.join(' · ')));
    const cost = jumpCost(state.galaxy, state.current, system.id);
    const outlook = jumpOutlook(state.galaxy, state.current, system.id, state.fuel);
    let text = OUTLOOK_TEXT[outlook];
    if (cost !== null && outlook !== 'here') text = `Прыжок · ${cost} топлива. ${text}`;
    if (outlook === 'far') {
      const count = hops(state.galaxy, state.current).get(system.id);
      if (count) text = `${count} ${count < 5 ? 'прыжка' : 'прыжков'} отсюда`;
    }
    box.append(el('div', 'galaxy-info-jump', text));
    return box;
  }
}

function el(tag: string, className: string, text?: string): HTMLElement {
  const node = document.createElement(tag);
  node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function svgEl(tag: string, attrs: Record<string, string | number>): SVGElement {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
  return node;
}
