import type { TradeEventMsg, TradeOfferDto, TradeStateMsg } from '../net/protocol';
import { lootItem, type LootRules } from '../sim/loot';
import { formatCredits } from '../sim/shop';
import type { CargoState } from './cargoHud';

/** Строка ленты о событии обмена (M16b). */
export function describeTradeEvent(message: TradeEventMsg): string {
  const name = message.name ?? 'Пилот';
  switch (message.code) {
    case 'invited':
      return `Предложение обмена отправлено: ${name}`;
    case 'opened':
      return `Обмен с ${name}`;
    case 'declined':
      return `${name} отказывается меняться`;
    case 'expired':
      return `${name} не ответил на предложение обмена`;
    case 'busy':
      return `${name} уже меняется с кем-то`;
    case 'gone':
      return 'Этого пилота уже нет в игре';
    case 'stale':
      return 'Предложение изменилось — посмотрите заново';
    case 'tooFar':
      return 'Обмен сорван: слишком далеко друг от друга';
    case 'docked':
      return 'Обмен сорван: из дока не меняются';
    case 'dead':
      return 'Обмен сорван: корабль уничтожен';
    case 'jumped':
      return 'Обмен сорван: один ушёл в другую систему';
    case 'left':
      return `${name} уходит со стола`;
    case 'noRoom':
      return 'Обмен не прошёл: не хватило места в трюме';
    case 'noCredits':
      return 'Обмен не прошёл: не хватило кредитов';
    case 'noItems':
      return 'Обмен не прошёл: обещанного груза не оказалось';
    case 'done':
      return 'Обмен состоялся';
  }
}

/** Отказ — это предупреждение (жёлтым), а не тревога: никто не умер и ничего не потеряно. */
export function isTradeWarning(code: TradeEventMsg['code']): boolean {
  return code !== 'done' && code !== 'opened' && code !== 'invited';
}

/** Сколько этого предмета можно положить на стол: не больше, чем лежит в трюме. */
export function clampGive(have: number, want: number): number {
  return Math.max(0, Math.min(have, Math.round(want)));
}

/** Кредитов на стол — не больше, чем есть на счету. */
export function clampCredits(balance: number, want: number): number {
  return Math.max(0, Math.min(balance, Math.round(want)));
}

/** Занятый объём после обмена: отдал — освободилось, принял — заняло. Бронь груза задания не двигается. */
export function holdAfter(
  cargo: CargoState,
  own: TradeOfferDto | null,
  their: TradeOfferDto | null,
  volume: (item: string) => number,
): number {
  let used = cargo.used;
  for (const [item, count] of Object.entries(own?.items ?? {})) used -= volume(item) * count;
  for (const [item, count] of Object.entries(their?.items ?? {})) used += volume(item) * count;
  return Math.max(0, used);
}

/** Влезет ли то, что дают, в трюм после того, как уедет то, что отдаём. */
export function tradeFits(
  cargo: CargoState,
  own: TradeOfferDto | null,
  their: TradeOfferDto | null,
  volume: (item: string) => number,
): boolean {
  return holdAfter(cargo, own, their, volume) <= cargo.max;
}

/** Хватает ли кредитов на то, что обещано со своей стороны. */
export function creditsFit(cargo: CargoState, own: TradeOfferDto | null): boolean {
  return (own?.credits ?? 0) <= cargo.credits;
}

export interface TradeRow {
  item: string;
  name: string;
  /** Сколько лежит в трюме. */
  have: number;
  /** Сколько уже на столе. */
  give: number;
}

/**
 * Строки своей половины: всё, что лежит в трюме, плюс то, что уже обещано. Обещанное показываем,
 * даже если груза не осталось (продал, выбросил, потерял): иначе строка исчезла бы, а обещание — нет.
 */
export function tradeRows(cargo: CargoState, own: TradeOfferDto | null, name: (item: string) => string): TradeRow[] {
  const items = new Set([...Object.keys(cargo.items), ...Object.keys(own?.items ?? {})]);
  return [...items]
    .map((item) => ({ item, name: name(item), have: cargo.items[item] ?? 0, give: own?.items[item] ?? 0 }))
    .sort((a, b) => a.name.localeCompare(b.name, 'ru'));
}

/** Строки чужой половины: только то, что на столе. */
export function theirRows(their: TradeOfferDto | null, name: (item: string) => string): TradeRow[] {
  return Object.entries(their?.items ?? {})
    .map(([item, count]) => ({ item, name: name(item), have: count, give: count }))
    .sort((a, b) => a.name.localeCompare(b.name, 'ru'));
}

/** «Трюм после обмена: 14 / 30». */
export function holdLine(after: number, max: number): string {
  return `Трюм после обмена: ${Math.round(after)} / ${Math.round(max)}`;
}

/** Что мешает нажать «Готов»; null — ничто не мешает. */
export function readyProblem(
  cargo: CargoState,
  own: TradeOfferDto | null,
  their: TradeOfferDto | null,
  volume: (item: string) => number,
): string | null {
  if (!creditsFit(cargo, own)) return 'Столько кредитов у вас нет';
  if (!tradeFits(cargo, own, their, volume)) return 'В трюм это не влезет';
  return null;
}

const el = (tag: string, className: string, text?: string): HTMLElement => {
  const node = document.createElement(tag);
  node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
};

function button(label: string, className: string, onClick: () => void): HTMLButtonElement {
  const node = document.createElement('button');
  node.type = 'button';
  node.className = className;
  node.textContent = label;
  node.addEventListener('click', () => {
    node.blur();
    onClick();
  });
  return node;
}

export interface TradeHandlers {
  /** Своя половина целиком: сервер принимает её как есть и двигает редакцию. */
  onOffer: (credits: number, items: Record<string, number>) => void;
  onReady: (rev: number) => void;
  onCancel: () => void;
}

/**
 * Окно обмена (M16b): слева своя половина со степперами, справа чужая — только чтение.
 * Истина здесь всегда серверная: окно рисует tradeState и закрывается только по нему, а не по своей
 * кнопке. Иначе у двоих на экранах оказались бы разные сделки.
 */
export class TradeWindow {
  private state: TradeStateMsg | null = null;
  private cargo: CargoState | null = null;
  private loot: LootRules | null = null;

  constructor(
    private readonly root: HTMLElement,
    private readonly handlers: TradeHandlers,
  ) {
    root.hidden = true;
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  setRules(loot: LootRules): void {
    this.loot = loot;
    if (this.open) this.render();
  }

  setCargo(cargo: CargoState): void {
    this.cargo = cargo;
    if (this.open) this.render();
  }

  /** null или active: false — сделки нет, окно закрывается. */
  set(state: TradeStateMsg | null): void {
    this.state = state?.active ? state : null;
    if (!this.state) {
      this.root.hidden = true;
      this.root.replaceChildren();
      return;
    }
    this.root.hidden = false;
    this.render();
  }

  private name(item: string): string {
    return (this.loot && lootItem(this.loot, item)?.name) || item;
  }

  /** Объём предмета: у снаряжения он свой (M11), и lootItem знает про оба каталога. */
  private volume(item: string): number {
    return (this.loot && lootItem(this.loot, item)?.volume) ?? 1;
  }

  private render(): void {
    const state = this.state;
    if (!state) return;
    const cargo = this.cargo ?? { used: 0, max: 0, items: {}, credits: 0, reserved: 0 };
    const own = state.own;
    const their = state.their;

    const card = el('div', 'trade-card sro-pane sro-pane--window');
    const head = el('div', 'trade-head sro-head');
    // Через точку, а не «Обмен с Борей»: ники не склоняются, и во всех окнах игры имя стоит само по себе.
    head.append(el('div', 'trade-title sro-head__title', `Обмен · ${their?.name ?? 'пилот'}`));
    head.append(button('✕', 'trade-close sro-btn sro-btn--icon', this.handlers.onCancel));
    card.append(head);

    const columns = el('div', 'trade-columns');
    columns.append(this.ownColumn(cargo, own), this.theirColumn(their));
    card.append(columns);

    const after = holdAfter(cargo, own, their, (item) => this.volume(item));
    const hold = el('div', 'trade-hold sro-num', holdLine(after, cargo.max));
    const problem = readyProblem(cargo, own, their, (item) => this.volume(item));
    if (problem) hold.classList.add('sro-warn');
    card.append(hold);
    if (problem) card.append(el('div', 'trade-problem sro-warn', problem));

    const actions = el('div', 'trade-actions sro-dialog__actions');
    const ready = button(
      own?.ready ? 'Готов · ждём' : 'Готов',
      `trade-ready sro-btn${own?.ready ? '' : ' sro-btn--primary'}`,
      () => this.handlers.onReady(state.rev),
    );
    ready.disabled = !!problem || !!own?.ready;
    actions.append(ready, button('Отмена', 'trade-cancel sro-btn', this.handlers.onCancel));
    card.append(actions);

    this.root.replaceChildren(card);
  }

  /** Своя половина: кредиты и груз со степперами. */
  private ownColumn(cargo: CargoState, own: TradeOfferDto | null): HTMLElement {
    const box = el('div', 'trade-col');
    box.append(el('div', 'trade-col-head sro-label', 'Вы отдаёте'));

    const items = { ...(own?.items ?? {}) };
    const send = (credits: number, next: Record<string, number>): void => {
      for (const [item, count] of Object.entries(next)) if (count <= 0) delete next[item];
      this.handlers.onOffer(credits, next);
    };

    const credits = el('div', 'trade-row sro-row');
    credits.append(el('div', 'trade-name sro-row__name', 'Кредиты'));
    credits.append(el('div', 'trade-have sro-row__meta sro-num', `у вас ${formatCredits(cargo.credits)}`));
    const own_credits = own?.credits ?? 0;
    const step = (by: number) => send(clampCredits(cargo.credits, own_credits + by), { ...items });
    const creditStepper = el('div', 'trade-qty sro-stepper');
    creditStepper.append(button('−', 'sro-btn', () => step(-100)));
    creditStepper.append(el('div', 'trade-qty-value sro-stepper__value sro-num', String(own_credits)));
    creditStepper.append(button('+', 'sro-btn', () => step(100)));
    creditStepper.append(button('Все', 'sro-btn', () => send(cargo.credits, { ...items })));
    creditStepper.append(button('0', 'sro-btn', () => send(0, { ...items })));
    credits.append(creditStepper);
    box.append(credits);

    for (const row of tradeRows(cargo, own, (item) => this.name(item))) {
      const line = el('div', 'trade-row sro-row');
      line.append(el('div', 'trade-name sro-row__name', row.name));
      line.append(el('div', 'trade-have sro-row__meta sro-num', `в трюме ${row.have}`));
      const stepper = el('div', 'trade-qty sro-stepper');
      const set = (count: number) => send(own?.credits ?? 0, { ...items, [row.item]: clampGive(row.have, count) });
      stepper.append(button('−', 'sro-btn', () => set(row.give - 1)));
      stepper.append(el('div', 'trade-qty-value sro-stepper__value sro-num', String(row.give)));
      stepper.append(button('+', 'sro-btn', () => set(row.give + 1)));
      stepper.append(button('Все', 'sro-btn', () => set(row.have)));
      line.append(stepper);
      box.append(line);
    }
    return box;
  }

  /** Чужая половина: только чтение. */
  private theirColumn(their: TradeOfferDto | null): HTMLElement {
    const box = el('div', 'trade-col');
    box.append(el('div', 'trade-col-head sro-label', `${their?.name ?? 'Пилот'} отдаёт`));
    if (their?.credits) {
      const line = el('div', 'trade-row sro-row');
      line.append(el('div', 'trade-name sro-row__name', 'Кредиты'));
      line.append(el('div', 'trade-have sro-row__meta sro-num sro-gain', formatCredits(their.credits)));
      box.append(line);
    }
    for (const row of theirRows(their, (item) => this.name(item))) {
      const line = el('div', 'trade-row sro-row');
      line.append(el('div', 'trade-name sro-row__name', row.name));
      line.append(el('div', 'trade-have sro-row__meta sro-num', `×${row.give}`));
      box.append(line);
    }
    if (!their?.credits && theirRows(their, (item) => this.name(item)).length === 0) {
      box.append(el('div', 'trade-empty sro-muted', 'Пока ничего'));
    }
    box.append(el('div', 'trade-ready-mark sro-muted', their?.ready ? 'Готов' : 'Ещё не готов'));
    return box;
  }
}
