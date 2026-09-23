import type { GalaxyDto, GalaxySystemDto } from '../net/protocol';
import { courseLine, courseView, hopsWord, type CourseView } from '../sim/course';
import { dangerColor, dangerName, gateNumber, hops, jumpOutlook, pvpName, regionName, type JumpOutlook } from '../sim/galaxy';
import { lootItem, type LootRules } from '../sim/loot';
import type { MarketRules } from '../sim/market';
import { levelColor, levelOf, type ReputationRules } from '../sim/reputation';
import { color } from './cargoHud';
import { repChip, repIcon } from './dockScreen';
import { gateBadges, mapViewBox, nodeBadges, regionLabelAt, routePoints, type BadgeKind } from './galaxyLayout';

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
  /**
   * Конечная система курса (M16b); null — курса нет. Строкой, а не массивом: путь выводится из галактики
   * каждый раз, а сравнение состояний идёт через JSON.stringify — лишнему состоянию тут не место.
   */
  course?: string | null;
  /** Врата текущей системы: по ним считается номер тех, через которые лежит курс. */
  gates?: readonly string[] | null;
}

const OUTLOOK_TEXT: Record<JumpOutlook, string> = {
  here: 'Вы здесь',
  far: 'Прямого маршрута нет — только через соседние системы',
  ok: 'Можно прыгать: подлетите к вратам',
};

/** Значки у узла: глиф и роль цвета (класс задаёт цвет из токенов). */
const BADGE_GLYPH: Record<BadgeKind, string> = { home: '⌂', objective: '★', invasion: '⚔', demand: '₪' };

/** Радиусы узла в единицах карты: ядро, кольцо отношения, «вы здесь», кольца событий, зона тапа. */
const R = { core: 3.2, rep: 5.2, here: 6.4, event: 7.4, eventOuter: 8.4, hit: 11 };

/**
 * Карта галактики (GDD §55): системы, маршруты и цена прыжка в топливе. Тап по системе — опасность, PvP,
 * станция и хватит ли топлива. Автопилота нет: карта — чтобы решить, куда лететь, а лететь к вратам — самому.
 */
export class GalaxyMap {
  private state: GalaxyMapState | null = null;
  private selected: string | null = null;

  /** onCourse — тап по системе прокладывает курс; null — курс снят (тап по системе, где стоим). */
  constructor(
    private readonly root: HTMLElement,
    private readonly onCourse: (to: string | null) => void = () => {},
  ) {
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
    const byId = new Map(galaxy.systems.map((s) => [s.id, s]));
    const course = courseView(galaxy, current, state.course, state.gates);

    const card = el('div', 'galaxy-card galaxy-card--map sro-pane sro-pane--window');
    const head = el('div', 'galaxy-head sro-head');
    const titles = el('div', 'galaxy-titles');
    titles.append(el('div', 'galaxy-title sro-head__title', 'Карта галактики'));
    // Подзаголовок: где мы и куда идём — видно раньше, чем глаз найдёт кольцо на карте.
    titles.append(el('div', 'galaxy-sub caption sro-muted', subtitle(nameOf(galaxy, current), course, galaxy)));
    head.append(titles);
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'galaxy-close sro-btn sro-btn--icon';
    close.setAttribute('aria-label', 'Закрыть карту');
    close.textContent = '✕';
    close.addEventListener('click', () => this.hide());
    head.append(close);
    card.append(head);

    const body = el('div', 'galaxy-body');
    const mapBox = el('div', 'galaxy-map');
    mapBox.append(this.svg(state, byId, course), legend());
    const side = el('div', 'galaxy-side');
    side.append(this.info(byId.get(this.selected ?? current), state, course));
    body.append(mapBox, side);
    card.append(body);
    this.root.replaceChildren(card);
  }

  /** Сама карта: облака регионов, связи, курс, бейджи врат и узлы систем. */
  private svg(state: GalaxyMapState, byId: Map<string, GalaxySystemDto>, course: CourseView | null): SVGElement {
    const { galaxy, current } = state;
    const box = mapViewBox(galaxy.systems);
    const svg = svgEl('svg', {
      class: 'galaxy-svg',
      viewBox: `${box.x} ${box.y} ${box.w} ${box.h}`,
      preserveAspectRatio: 'xMidYMid meet',
    });
    svg.setAttribute('style', `aspect-ratio: ${box.w} / ${box.h}`);

    // Размытие облака региона и стрелка курса — один раз на карту.
    const defs = svgEl('defs', {});
    const soft = svgEl('filter', { id: 'galaxy-soft', x: '-50%', y: '-50%', width: '200%', height: '200%' });
    soft.append(svgEl('feGaussianBlur', { stdDeviation: 4 }));
    const arrow = svgEl('marker', {
      id: 'galaxy-arrow',
      viewBox: '0 0 10 10',
      refX: 8,
      refY: 5,
      markerWidth: 4,
      markerHeight: 4,
      orient: 'auto',
      markerUnits: 'strokeWidth',
    });
    arrow.append(svgEl('path', { d: 'M0,0 L10,5 L0,10 z', class: 'galaxy-arrow' }));
    defs.append(soft, arrow);
    svg.append(defs);

    // Регионы (M11) — мягким облаком из размытых кругов, а не прямоугольником: у них нет границ, только цвет.
    for (const region of galaxy.regions ?? []) {
      const inside = galaxy.systems.filter((s) => s.region === region.id);
      if (inside.length === 0) continue;
      const cloud = svgEl('g', { class: 'galaxy-region', filter: 'url(#galaxy-soft)', fill: region.color });
      for (const s of inside) cloud.append(svgEl('circle', { cx: s.x, cy: s.y, r: 11 }));
      const at = regionLabelAt(inside);
      const label = svgEl('text', { x: at.x, y: at.y, class: 'galaxy-region-name', fill: region.color });
      label.textContent = region.name;
      svg.append(cloud, label);
    }

    for (const link of galaxy.links) {
      const a = byId.get(link.a);
      const b = byId.get(link.b);
      if (!a || !b) continue;
      const fromHere = link.a === current || link.b === current;
      svg.append(svgEl('line', { x1: a.x, y1: a.y, x2: b.x, y2: b.y, class: `galaxy-link${fromHere ? ' galaxy-link-open' : ''}` }));
    }

    // Курс (M16b) — своей ломаной поверх связей, со стрелкой на конечной системе: видно и путь, и направление.
    const path = (course?.path ?? []).map((id) => byId.get(id)).filter((s): s is GalaxySystemDto => !!s);
    const points = routePoints(path);
    if (points.length >= 2) {
      svg.append(svgEl('polyline', { points: points.map((p) => `${p.x},${p.y}`).join(' '), class: 'galaxy-route', 'marker-end': 'url(#galaxy-arrow)' }));
    }

    // Номера врат — бейджами у текущей системы и на курсе; остальные перечисляет карточка.
    for (const badge of gateBadges(galaxy, current, course?.path ?? [])) {
      const g = svgEl('g', { class: `galaxy-gate${badge.route ? ' galaxy-gate--route' : ''}` });
      g.append(svgEl('circle', { cx: badge.x, cy: badge.y, r: 2.1 }));
      const n = svgEl('text', { x: badge.x, y: badge.y });
      n.textContent = String(badge.n);
      g.append(n);
      svg.append(g);
    }

    for (const system of galaxy.systems) svg.append(this.node(system, state));
    return svg;
  }

  /** Узел системы: ядро по форме места, кольца состояний, значки событий, имя и зона тапа. */
  private node(system: GalaxySystemDto, state: GalaxyMapState): SVGElement {
    const { current } = state;
    const { x, y } = system;
    const group = svgEl('g', { class: 'galaxy-node', 'data-id': system.id });
    // Форма говорит о месте: квадрат — есть станция, круг — станции нет. Цвет — опасность.
    const fill = color(dangerColor(system.danger));
    group.append(
      system.station
        ? svgEl('rect', { x: x - R.core, y: y - R.core, width: R.core * 2, height: R.core * 2, rx: 1, class: 'galaxy-core', fill })
        : svgEl('circle', { cx: x, cy: y, r: R.core, class: 'galaxy-core', fill }),
    );
    // Отношение — кольцом вокруг узла: цвет ядра занят опасностью, а она для маршрута важнее.
    const repValue = state.rep?.[system.id];
    if (repValue !== undefined && state.repRules) {
      group.append(svgEl('circle', { cx: x, cy: y, r: R.rep, class: 'galaxy-rep', stroke: levelColor(levelOf(state.repRules, repValue)) }));
    }
    if (system.id === current) group.append(svgEl('circle', { cx: x, cy: y, r: R.here, class: 'galaxy-here' }));
    else if (system.id === this.selected) group.append(svgEl('circle', { cx: x, cy: y, r: R.here, class: 'galaxy-selected' }));
    const objective = system.id === state.objective;
    const invasion = system.id === state.invasion;
    if (objective) group.append(svgEl('circle', { cx: x, cy: y, r: R.event, class: 'galaxy-objective' }));
    if (invasion) group.append(svgEl('circle', { cx: x, cy: y, r: objective ? R.eventOuter : R.event, class: 'galaxy-invasion' }));

    // Значки событий — бейджами у узла, а не суффиксами в имени: в 3 px «Rigel ⚔» не прочесть.
    for (const badge of nodeBadges({ home: system.id === state.home, objective, invasion, demand: system.id === state.demand })) {
      const g = svgEl('g', { class: `galaxy-badge galaxy-badge--${badge.kind}` });
      g.append(svgEl('circle', { cx: x + badge.dx, cy: y + badge.dy, r: 2.4 }));
      const glyph = svgEl('text', { x: x + badge.dx, y: y + badge.dy });
      glyph.textContent = BADGE_GLYPH[badge.kind];
      g.append(glyph);
      group.append(g);
    }

    const name = svgEl('text', { x, y: y + 9.4, class: `galaxy-name${system.id === current ? ' galaxy-name--here' : ''}` });
    name.textContent = system.name;
    group.append(name);
    // Зона тапа крупнее кружка: пальцем по кружку в 6 единиц на телефоне не попасть.
    group.append(svgEl('circle', { cx: x, cy: y, r: R.hit, class: 'galaxy-hit' }));
    group.addEventListener('click', () => {
      this.selected = system.id;
      // Выбор системы и есть прокладка курса; тап по той, где стоим, курс снимает.
      this.onCourse(system.id === current ? null : system.id);
      this.render();
    });
    return group;
  }

  /** Карточка выбранной системы (по умолчанию — текущей). */
  private info(system: GalaxySystemDto | undefined, state: GalaxyMapState, course: CourseView | null): HTMLElement {
    const box = el('div', 'galaxy-info');
    if (!system) return box;
    const title = el('div', 'galaxy-info-name', system.name);
    title.style.color = color(dangerColor(system.danger));
    box.append(title);
    const facts = [dangerName(system.danger), pvpName(system.pvp), system.station ? 'есть станция' : 'станции нет'];
    const region = regionName(state.galaxy, system.region);
    if (region) facts.unshift(region);
    box.append(el('div', 'galaxy-info-facts sro-muted', facts.join(' · ')));

    // Врата системы по номерам (M16b): на карте номера стоят только у текущей, здесь — у любой выбранной.
    const gates = system.gates ?? [];
    if (gates.length > 0) {
      const line = el('div', 'galaxy-info-gates sro-muted');
      line.append(el('span', 'galaxy-info-label label', 'Врата'));
      gates.forEach((to, i) => {
        const n = gateNumber(i);
        const item = el('span', 'galaxy-info-gate', `${n} → ${nameOf(state.galaxy, to)}`);
        // Врата курса в текущей системе — сталью: их и искать в мире.
        if (system.id === state.current && course?.gate === n) item.classList.add('galaxy-info-gate--route');
        if (i > 0) line.append(el('span', 'galaxy-info-sep', ' · '));
        line.append(item);
      });
      box.append(line);
    }

    // Чем здесь торгуют (M12): «производит» — где это дёшево купить, «покупает» — куда везти.
    // Ключ места, а не системы (M15): на карте показываем станцию — поселения видно уже на месте.
    const profile = state.market?.places?.[`st:${system.id}`];
    if (profile && state.loot) {
      const names = (ids: string[] | null | undefined): string =>
        (ids ?? []).map((id) => lootItem(state.loot!, id)?.name ?? id).join(', ');
      const produces = names(profile.produces);
      const consumes = names(profile.consumes);
      if (produces) box.append(labelled('Производит', produces));
      if (consumes) box.append(labelled('Покупает', consumes));
    }
    // Отношение властей (M13): по нему закрывается док и звереют рейнджеры.
    const repValue = state.rep?.[system.id];
    if (repValue !== undefined && state.repRules) {
      // Плашка та же, что в доке: подпись, цвет и значок ступени считает repChip.
      const chip = repChip(state.repRules, repValue);
      const line = labelled('Отношение', chip.text);
      const value = line.lastChild as HTMLElement;
      value.className = 'galaxy-info-rep';
      value.style.color = chip.color;
      // Значок внутри подписи, а не рядом: маска красится в currentColor, то есть в цвет ступени.
      if (chip.icon) value.prepend(repIcon(chip.icon));
      box.append(line);
    }
    const outlook = jumpOutlook(state.galaxy, state.current, system.id);
    let text = OUTLOOK_TEXT[outlook];
    if (outlook === 'far') {
      const count = hops(state.galaxy, state.current).get(system.id);
      if (count) text = `${hopsWord(count)} отсюда`;
    }
    box.append(el('div', 'galaxy-info-jump', text));
    // Курс — в карточке конечной системы и в карточке текущей: открыл карту и сразу видишь, куда шёл.
    // В карточке посторонней системы его нет: там он сбивал бы с толку.
    if (course && (course.to === system.id || system.id === state.current)) {
      const name = course.to === system.id ? null : nameOf(state.galaxy, course.to);
      const line = el('div', 'galaxy-info-course');
      line.append(el('span', 'sro-dot'), el('span', '', courseLine(course, name)));
      box.append(line);
    }
    return box;
  }
}

/** «Sol · курс на Rigel: 2 прыжка» / «Sol · курса нет». */
function subtitle(here: string, course: CourseView | null, galaxy: GalaxyDto): string {
  if (!course) return `${here} · курса нет`;
  const to = nameOf(galaxy, course.to);
  if (course.done) return `${here} · вы на месте`;
  if (course.lost) return `${here} · маршрута до ${to} нет`;
  return `${here} · курс на ${to}: ${hopsWord(course.hops)}`;
}

/** Строка карточки с подписью в стиле label: «ПРОИЗВОДИТ  Металл, Руда». */
function labelled(label: string, value: string): HTMLElement {
  const line = el('div', 'galaxy-info-trade sro-muted');
  line.append(el('span', 'galaxy-info-label label', label), el('span', '', value));
  return line;
}

/** Легенда — ряд чипов с образцом знака; словами описан только смысл, форму показывает сам образец. */
function legend(): HTMLElement {
  const box = el('div', 'galaxy-legend');
  const key = (swatch: SVGElement, text: string): void => {
    const chip = el('span', 'galaxy-key caption sro-muted');
    const svg = svgEl('svg', { viewBox: '0 0 12 12', class: 'galaxy-key-swatch', 'aria-hidden': 'true' });
    svg.append(swatch);
    chip.append(svg, el('span', '', text));
    box.append(chip);
  };
  const danger = svgEl('g', {});
  [1, 3, 5].forEach((level, i) => danger.append(svgEl('circle', { cx: 2.5 + i * 3.5, cy: 6, r: 1.6, fill: color(dangerColor(level)) })));
  key(danger, 'цвет — опасность');
  key(svgEl('rect', { x: 3, y: 3, width: 6, height: 6, rx: 1, class: 'galaxy-key-station' }), 'квадрат — станция');
  key(svgEl('circle', { cx: 6, cy: 6, r: 4, class: 'galaxy-key-rep' }), 'кольцо — отношение властей');
  key(svgEl('line', { x1: 1, y1: 6, x2: 11, y2: 6, class: 'galaxy-key-route' }), 'пунктир — курс');
  const badges: [BadgeKind, string][] = [
    ['home', 'дом: сюда вернётесь после гибели'],
    ['objective', 'цель задания'],
    ['invasion', 'вторжение пиратов'],
    ['demand', 'событие спроса'],
  ];
  for (const [kind, text] of badges) {
    const g = svgEl('g', { class: `galaxy-badge galaxy-badge--${kind}` });
    g.append(svgEl('circle', { cx: 6, cy: 6, r: 5 }));
    const glyph = svgEl('text', { x: 6, y: 6, class: 'galaxy-key-glyph' });
    glyph.textContent = BADGE_GLYPH[kind];
    g.append(glyph);
    key(g, text);
  }
  return box;
}

/** Имя системы по id; неизвестная — сам id. */
function nameOf(galaxy: GalaxyDto, id: string): string {
  return galaxy.systems.find((s) => s.id === id)?.name ?? id;
}

function el(tag: string, className: string, text?: string): HTMLElement {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function svgEl(tag: string, attrs: Record<string, string | number>): SVGElement {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
  return node;
}
