import type { GalaxyDto, GalaxySystemDto } from '../net/protocol';
import { dangerColor, dangerName, hops, jumpOutlook, pvpName, regionName, type JumpOutlook } from '../sim/galaxy';
import { lootItem, type LootRules } from '../sim/loot';
import type { MarketRules } from '../sim/market';
import { levelColor, levelIndex, levelOf, repLabel, type ReputationRules } from '../sim/reputation';
import { color } from './cargoHud';

const SVG = 'http://www.w3.org/2000/svg';

/** Что пилот знает, открывая карту: где он и где дом. */
export interface GalaxyMapState {
  galaxy: GalaxyDto;
  current: string;
  /** Система последней стыковки: туда корабль вернётся после гибели. */
  home: string | null;
  /** Куда ведёт задание (GDD §36); null — никуда или цель здесь. */
  objective?: string | null;
  /** Где вторжение пиратов (GDD §38) — объявлено или идёт; null — нигде. */
  invasion?: string | null;
  /** Где событие спроса (M15.5) — объявлено или идёт приём; null — нигде. */
  demand?: string | null;
  /** Правила рынка (M12): по ним видно, что где производят и скупают. */
  market?: MarketRules | null;
  /** Каталог груза: названия товаров для строки «производит / покупает». */
  loot?: LootRules | null;
  /**
   * Отношение систем к пилоту (M13), по id системы; только ненулевые. В GalaxyDto ему не место:
   * тот приходит в общем config и одинаков для всех, а это — личное.
   */
  rep?: Record<string, number> | null;
  repRules?: ReputationRules | null;
}

/**
 * Значок отношения у названия системы: крестик — враг (док закрыт), ромб — друг и выше.
 * Нейтральные и просто недоверчивые системы значка не получают: иначе карта зарябит.
 */
function repMark(state: GalaxyMapState, id: string): string {
  const value = state.rep?.[id];
  if (value === undefined || !state.repRules) return '';
  const index = levelIndex(state.repRules, value);
  const last = (state.repRules.levels ?? []).length - 1;
  if (index === 0) return ' ✖';
  return index >= last - 1 && last >= 3 ? ' ♦' : '';
}

const OUTLOOK_TEXT: Record<JumpOutlook, string> = {
  here: 'Вы здесь',
  far: 'Прямого маршрута нет — только через соседние системы',
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
    head.append(el('div', 'galaxy-title', 'Карта галактики'));
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

    // Регионы (M11) — облаком под системами: видно, где кончается Ядро и начинается Рубеж.
    for (const region of galaxy.regions ?? []) {
      const inside = galaxy.systems.filter((s) => s.region === region.id);
      if (inside.length === 0) continue;
      const xs = inside.map((s) => s.x);
      const ys = inside.map((s) => s.y);
      const pad = 7;
      const x = Math.min(...xs) - pad;
      const y = Math.min(...ys) - pad;
      svg.append(
        svgEl('rect', {
          x,
          y,
          width: Math.max(...xs) - Math.min(...xs) + pad * 2,
          height: Math.max(...ys) - Math.min(...ys) + pad * 2,
          rx: 6,
          class: 'galaxy-region',
          fill: region.color,
        }),
      );
      const label = svgEl('text', { x: x + 1.5, y: y + 4, class: 'galaxy-region-name', fill: region.color });
      label.textContent = region.name;
      svg.append(label);
    }

    for (const link of galaxy.links) {
      const a = byId.get(link.a);
      const b = byId.get(link.b);
      if (!a || !b) continue;
      const fromHere = link.a === current || link.b === current;
      const line = svgEl('line', {
        x1: a.x,
        y1: a.y,
        x2: b.x,
        y2: b.y,
        class: `galaxy-link${fromHere ? ' galaxy-link-open' : ''}`,
      });
      // Подписи у маршрута больше нет: с M15.6 он ничего не стоит, и цифра была только про топливо.
      svg.append(line);
    }

    for (const system of galaxy.systems) {
      const group = svgEl('g', { class: 'galaxy-node', 'data-id': system.id });
      if (system.id === current) group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 6.5, class: 'galaxy-here' }));
      if (system.id === this.selected) group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 5.6, class: 'galaxy-selected' }));
      if (system.id === state.objective) group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 7.4, class: 'galaxy-objective' }));
      if (system.id === state.invasion) group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 8.6, class: 'galaxy-invasion' }));
      if (system.id === state.demand) group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 8.6, class: 'galaxy-demand' }));
      group.append(svgEl('circle', { cx: system.x, cy: system.y, r: 4, fill: color(dangerColor(system.danger)) }));
      // Отношение — кольцом вокруг узла: цвет кружка занят опасностью, а она для маршрута важнее.
      const repValue = state.rep?.[system.id];
      if (repValue !== undefined && state.repRules) {
        group.append(svgEl('circle', {
          cx: system.x,
          cy: system.y,
          r: 6.2,
          class: 'galaxy-rep',
          stroke: levelColor(levelOf(state.repRules, repValue)),
        }));
      }
      if (system.station) {
        group.append(svgEl('rect', { x: system.x - 1.4, y: system.y - 1.4, width: 2.8, height: 2.8, class: 'galaxy-station' }));
      }
      const name = svgEl('text', { x: system.x, y: system.y + 8.2, class: 'galaxy-name' });
      name.textContent =
        `${system.name}${repMark(state, system.id)}${system.id === state.home ? ' ⌂' : ''}` +
        `${system.id === state.objective ? ' ★' : ''}${system.id === state.invasion ? ' ⚔' : ''}` +
        `${system.id === state.demand ? ' ₪' : ''}`;
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
    const regions = (galaxy.regions ?? []).map((r) => r.name).join(' · ');
    card.append(
      el(
        'div',
        'galaxy-legend',
        `${regions ? `Регионы: ${regions}. ` : ''}Цвет — опасность, квадрат — станция, кольцо — отношение властей ` +
          `(✖ — док закрыт, ♦ — вас тут ценят), ⌂ — где вы появитесь после гибели, ★ — цель задания, ` +
          `⚔ — вторжение пиратов, ₪ — событие спроса. Числа — топливо на прыжок.`,
      ),
    );
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
    const region = regionName(state.galaxy, system.region);
    if (region) facts.unshift(region);
    box.append(el('div', 'galaxy-info-facts', facts.join(' · ')));
    // Чем здесь торгуют (M12): «производит» — где это дёшево купить, «покупает» — куда везти.
    // Ключ места, а не системы (M15): на карте показываем станцию — поселения видно уже на месте.
    const profile = state.market?.places?.[`st:${system.id}`];
    if (profile && state.loot) {
      const names = (ids: string[] | null | undefined): string =>
        (ids ?? []).map((id) => lootItem(state.loot!, id)?.name ?? id).join(', ');
      const produces = names(profile.produces);
      const consumes = names(profile.consumes);
      if (produces) box.append(el('div', 'galaxy-info-trade', `Производит: ${produces}`));
      if (consumes) box.append(el('div', 'galaxy-info-trade', `Покупает: ${consumes}`));
    }
    // Отношение властей (M13): по нему закрывается док и звереют рейнджеры.
    const repValue = state.rep?.[system.id];
    if (repValue !== undefined && state.repRules) {
      const level = levelOf(state.repRules, repValue);
      const line = el('div', 'galaxy-info-trade', `Отношение: ${repLabel(level, repValue)}`);
      line.style.color = levelColor(level);
      box.append(line);
    }
    const outlook = jumpOutlook(state.galaxy, state.current, system.id);
    let text = OUTLOOK_TEXT[outlook];
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
