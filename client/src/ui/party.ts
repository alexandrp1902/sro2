import type { BountyMsg, PartyEventMsg, PartyInviteMsg, PartyMemberDto, PartyStateMsg } from '../net/protocol';
import { formatSectors } from '../sim/combat';
import { formatCredits } from '../sim/shop';
import { Bar } from './combatHud';

/** Строка ленты о событии группы. */
export function describePartyEvent(message: PartyEventMsg): string {
  const name = message.name ?? '';
  switch (message.code) {
    case 'invited':
      return `Приглашение отправлено: ${name}`;
    case 'joined':
      return name ? `${name} вступает в группу` : 'Вы в группе';
    case 'left':
      return `${name} выходит из группы`;
    case 'declined':
      return `${name} отклоняет приглашение`;
    case 'expired':
      return name ? `${name} не ответил на приглашение` : 'Приглашение устарело';
    case 'full':
      return 'В группе нет мест';
    case 'busy':
      return `${name} уже в группе`;
    case 'gone':
      return 'Этого пилота уже нет в игре';
    case 'disbanded':
      return 'Группа распалась';
  }
}

/** «+60 кр за Пират Ур.1» или с дележом — «+90 кр за Пират Ур.2 (делёж на 2)». */
export function describeBounty(message: BountyMsg): string {
  const split = message.shared > 1 ? ` (делёж на ${message.shared})` : '';
  return `+${formatCredits(message.amount)} за ${message.name}${split}`;
}

/** Где я: для расстояния до участников в той же системе. */
export interface Me {
  id: number;
  system: string;
  x: number;
  y: number;
}

export interface PartyRow {
  id: number;
  name: string;
  /**
   * Номер участника — по порядку вступления, а не по порядку строк: я в панели всегда первый,
   * и нумерация по строкам переставляла бы номера товарищей от того, кто смотрит. С этим же номером
   * участник подписан меткой на миникарте.
   */
  n: number;
  leader: boolean;
  self: boolean;
  hp: number;
  maxHp: number;
  sh: number;
  maxSh: number;
  /** «1.2с» — в той же системе; иначе имя системы. */
  where: string;
  /** «нет связи», «сбит», «в доке» — или пусто. */
  status: string;
}

/** Метка участника группы на миникарте: ромб с номером, независимо от радара (M16b). */
export interface PartyMark {
  id: number;
  n: number;
  x: number;
  y: number;
}

/** Больше стольких строк — панель переходит в компактный вид: одна полоска вместо двух. */
export const PARTY_COMPACT_FROM = 6;

export function partyCompact(rows: number): boolean {
  return rows >= PARTY_COMPACT_FROM;
}

/** «Группа · 7/10»; предел неизвестен (старый сервер, витрина) — просто «Группа». */
export function partyTitle(size: number, maxSize: number): string {
  return maxSize > 0 ? `Группа · ${size}/${maxSize}` : 'Группа';
}

/** Корпус и щит одной полоской: в компактном виде на вторую нет места, а живучесть — это их сумма. */
export function combinedBar(row: PartyRow): { value: number; max: number } {
  return { value: row.hp + row.sh, max: row.maxHp + row.maxSh };
}

/**
 * Метки группы для миникарты: только те, кто в этой системе, жив, не в доке и не я сам.
 * Радар им не указ — точки берутся из состояния группы, а не из снапшота. Но если корабль виден
 * (locate вернул точку), берём её: она идёт 20 раз в секунду, а состояние группы — раз в секунду.
 */
export function partyMarks(
  members: readonly PartyMemberDto[],
  me: Me,
  locate: (id: number) => { x: number; y: number } | null,
): PartyMark[] {
  const marks: PartyMark[] = [];
  members.forEach((m, i) => {
    if (m.id === me.id || m.dead || m.docked || m.system !== me.system) return;
    const live = locate(m.id);
    marks.push({ id: m.id, n: i + 1, x: live?.x ?? m.x, y: live?.y ?? m.y });
  });
  return marks;
}

/** Своя группа — из partyState; всё остальное (цвет, прицел, панель) спрашивает её. */
export class PartyBoard {
  private leader = 0;
  private members: PartyMemberDto[] = [];
  private limit = 0;
  private readonly idSet = new Set<number>();

  apply(message: PartyStateMsg): void {
    this.leader = message.leader;
    this.members = message.members;
    this.limit = message.maxSize ?? 0;
    this.idSet.clear();
    for (const m of message.members) this.idSet.add(m.id);
  }

  clear(): void {
    this.apply({ t: 'partyState', leader: 0, members: [] });
  }

  get size(): number {
    return this.members.length;
  }

  /** Предел группы с сервера; 0 — ещё не знаем. */
  get maxSize(): number {
    return this.limit;
  }

  /** Id всех участников, включая себя; пусто — не в группе. */
  get ids(): ReadonlySet<number> {
    return this.idSet;
  }

  /** Этот корабль — в своей группе (кроме меня самого). */
  isMember(id: number, ownId: number): boolean {
    return id !== ownId && this.idSet.has(id);
  }

  /** Метки участников этой системы для миникарты (M16b). */
  marks(me: Me, locate: (id: number) => { x: number; y: number } | null): PartyMark[] {
    return partyMarks(this.members, me, locate);
  }

  /** Строки панели: я первым, дальше по порядку вступления. */
  rows(me: Me, sectorUnit: number): PartyRow[] {
    const number = new Map(this.members.map((m, i) => [m.id, i + 1]));
    const ordered = [...this.members].sort((a, b) => Number(b.id === me.id) - Number(a.id === me.id));
    return ordered.map((m) => ({
      id: m.id,
      name: m.name,
      n: number.get(m.id)!,
      leader: m.id === this.leader,
      self: m.id === me.id,
      hp: m.hp,
      maxHp: m.maxHp,
      sh: m.sh,
      maxSh: m.maxSh,
      where:
        m.id === me.id
          ? ''
          : m.system === me.system && !m.docked
            ? `${formatSectors(Math.hypot(m.x - me.x, m.y - me.y), sectorUnit)}с`
            : m.systemName,
      status: !m.online ? 'нет связи' : m.dead ? 'сбит' : m.docked ? 'в доке' : '',
    }));
  }
}

/**
 * Строка панели: ник, где он, полоски корпуса и щита. В компактном виде (группа больше пяти)
 * у чужих строк остаётся одна полоска — корпус со щитом вместе; своя строка всегда полная.
 */
class RowView {
  readonly el: HTMLElement;
  private readonly name: HTMLElement;
  private readonly where: HTMLElement;
  private readonly hull: Bar;
  private readonly shield: Bar;

  constructor(parent: HTMLElement) {
    this.el = document.createElement('div');
    this.el.className = 'party-row';
    const head = document.createElement('div');
    head.className = 'party-head';
    this.name = document.createElement('span');
    this.name.className = 'party-name';
    this.where = document.createElement('span');
    this.where.className = 'party-where sro-num sro-muted';
    head.append(this.name, this.where);
    this.el.append(head);
    this.hull = new Bar(this.el, 'hull', '');
    this.shield = new Bar(this.el, 'shield', '');
    parent.append(this.el);
  }

  set(row: PartyRow, compact: boolean): void {
    // Номер показывается только в компактном виде: там же, где появляются метки на карте и где
    // строк столько, что имена на глаз уже не пересчитать. Свой номер тоже нужен — иначе в счёте дыра.
    const name = `${compact ? `${row.n} ` : ''}${row.leader ? '★ ' : ''}${row.name}`;
    if (this.name.textContent !== name) this.name.textContent = name;
    const where = [row.where, row.status].filter(Boolean).join(' · ');
    if (this.where.textContent !== where) this.where.textContent = where;
    this.el.dataset.self = String(row.self);
    this.el.dataset.status = row.status ? 'off' : 'on';
    const one = compact && !row.self;
    this.el.dataset.compact = String(one);
    if (one) {
      const bar = combinedBar(row);
      this.hull.set(bar.value, bar.max);
    } else {
      this.hull.set(row.hp, row.maxHp);
      this.shield.set(row.sh, row.maxSh);
    }
  }
}

/** Панель группы (GDD §37): ПК — слева под трюмом, телефон — справа, компактно. Кнопка «Выйти». */
export class PartyPanel {
  private readonly list: HTMLElement;
  private readonly title: HTMLElement;
  private readonly views: RowView[] = [];

  constructor(
    private readonly root: HTMLElement,
    onLeave: () => void,
  ) {
    const head = document.createElement('div');
    head.className = 'party-title';
    const title = document.createElement('span');
    title.className = 'sro-label';
    title.textContent = 'Группа';
    this.title = title;
    const leave = document.createElement('button');
    leave.type = 'button';
    leave.className = 'party-leave sro-btn sro-btn--ghost sro-btn--xs';
    leave.textContent = 'Выйти';
    leave.addEventListener('click', () => {
      leave.blur();
      onLeave();
    });
    head.append(title, leave);
    this.list = document.createElement('div');
    this.list.className = 'party-list';
    root.append(head, this.list);
    root.hidden = true;
  }

  /** null или пусто — прятать. maxSize — для заголовка «Группа · 7/10»; 0 — предел неизвестен. */
  update(rows: PartyRow[] | null, maxSize = 0): void {
    const show = !!rows && rows.length > 0;
    if (this.root.hidden === show) this.root.hidden = !show;
    if (!rows || !show) return;
    const title = partyTitle(rows.length, maxSize);
    if (this.title.textContent !== title) this.title.textContent = title;
    const compact = partyCompact(rows.length);
    this.root.dataset.compact = String(compact);
    while (this.views.length < rows.length) this.views.push(new RowView(this.list));
    while (this.views.length > rows.length) this.views.pop()!.el.remove();
    rows.forEach((row, i) => this.views[i].set(row, compact));
  }
}

/** Карточка «Bob зовёт в группу [Принять] [Отклонить]» с обратным отсчётом; одна за раз — новая заменяет старую. */
export class InviteCard {
  private from = 0;
  private until = 0;
  private readonly text: HTMLElement;
  private readonly timer: HTMLElement;

  constructor(
    private readonly root: HTMLElement,
    onAnswer: (accept: boolean, from: number) => void,
  ) {
    this.text = document.createElement('div');
    this.text.className = 'invite-text sro-dialog__title';
    this.timer = document.createElement('div');
    this.timer.className = 'invite-timer sro-dialog__text sro-num';
    const buttons = document.createElement('div');
    buttons.className = 'invite-buttons sro-dialog__actions';
    for (const [label, accept] of [
      ['Принять', true],
      ['Отклонить', false],
    ] as const) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = accept ? 'invite-accept sro-btn sro-btn--primary' : 'invite-decline sro-btn';
      button.textContent = label;
      button.addEventListener('click', () => {
        button.blur();
        if (this.from) onAnswer(accept, this.from);
        this.hide();
      });
      buttons.append(button);
    }
    root.append(this.text, this.timer, buttons);
    root.hidden = true;
  }

  show(message: PartyInviteMsg, now: number): void {
    this.from = message.from;
    this.until = now + message.seconds * 1000;
    this.text.textContent = `${message.name} зовёт в группу`;
    this.root.hidden = false;
    this.tick(now);
  }

  /** Отсчёт; истекло — карточка прячется сама. */
  tick(now: number): void {
    if (this.root.hidden) return;
    const left = Math.ceil((this.until - now) / 1000);
    if (left <= 0) {
      this.hide();
      return;
    }
    const text = `${left} с`;
    if (this.timer.textContent !== text) this.timer.textContent = text;
  }

  hide(): void {
    this.from = 0;
    this.root.hidden = true;
  }
}
