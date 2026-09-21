import { formatSectors, type Aim, type AimState } from '../sim/combat';
import { clock } from '../util/clock';

const RENDER_INTERVAL_MS = 100;
/** Сколько держать точные корпус и щит на телефоне после тапа по полоскам. */
const DETAIL_MS = 4000;

const STATE_TEXT: Record<AimState, string> = {
  ready: 'в секторе',
  arc: 'вне сектора',
  range: 'далеко',
  protected: 'под защитой',
  dead: 'уничтожен',
};

export interface Vitals {
  hp: number;
  maxHp: number;
  sh: number;
  maxSh: number;
}

export interface OwnStatus extends Vitals {
  /** Сколько ещё действует защита после появления, с; 0 — нет. */
  protectedSeconds: number;
  /** Сколько пиратов сейчас целится в меня. */
  attackers: number;
}

export interface TargetStatus extends Vitals {
  name: string;
  hullName: string;
  aim: Aim;
  /** Сколько единиц мира в одном секторе — в них показываем дистанцию до цели. */
  sectorUnit: number;
  /** Огонь включён — под карточкой «ОГОНЬ ПО ГОТОВНОСТИ» (ПК: там нет кнопки, которая бы это показала). */
  fire: boolean;
  /** Цель — пилот не из группы: на карточке кнопка «В группу» (GDD §37). */
  invite: boolean;
  /** Цель — живой пилот рядом: на карточке кнопка «Обмен» (M16b). */
  trade: boolean;
  /** Цель — участник своей группы: по нему не стреляют. */
  member: boolean;
}

export interface DeathStatus {
  /** Кто уничтожил; пусто — неизвестно. */
  by: string;
  /** До возвращения на базу, с. */
  seconds: number;
}

/**
 * Полоска «Корпус» или «Щит» (SRO Steel: Bar): строка «подпись · число» над тонкой полоской.
 * Без подписи (панель группы) — только полоска, ещё тоньше.
 */
export class Bar {
  private readonly fill: HTMLElement;
  private readonly num: HTMLElement | null;
  private shown = '';

  constructor(parent: HTMLElement, kind: 'hull' | 'shield', title: string) {
    if (title) {
      const row = document.createElement('div');
      row.className = `sro-bar-row sro-bar-row--${kind}`;
      const label = document.createElement('span');
      label.className = 'sro-label';
      label.textContent = title;
      this.num = document.createElement('span');
      this.num.className = 'sro-num';
      row.append(label, this.num);
      parent.append(row);
    } else this.num = null;
    const bar = document.createElement('div');
    bar.className = `sro-bar sro-bar--${kind}${title ? '' : ' sro-bar--thin'}`;
    this.fill = document.createElement('div');
    this.fill.className = 'sro-bar__fill';
    bar.append(this.fill);
    parent.append(bar);
  }

  set(value: number, max: number): void {
    const key = `${value}|${max}`;
    if (key === this.shown) return;
    this.shown = key;
    const share = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
    this.fill.style.width = `${share * 100}%`;
    if (this.num) this.num.textContent = max > 0 ? `${Math.ceil(value)} / ${Math.round(max)}` : '—';
  }
}

/**
 * Боевой HUD (GDD §9, §24, §41): свои корпус и щит, карточка цели, экран «Корабль уничтожен».
 * Весь текст — через textContent: ники приходят от других игроков.
 */
export class CombatHud {
  private readonly ownHull: Bar;
  private readonly ownShield: Bar;
  private readonly ownProtect: HTMLElement;
  private readonly ownThreat: HTMLElement;
  private readonly targetName: HTMLElement;
  private readonly targetClass: HTMLElement;
  private readonly targetHull: Bar;
  private readonly targetShield: Bar;
  private readonly targetInfo: HTMLElement;
  private readonly invite: HTMLButtonElement;
  private readonly trade: HTMLButtonElement;
  private readonly deathBy: HTMLElement;
  private readonly deathTimer: HTMLElement;
  private lastRender = 0;
  /** Таймер, который снова прячет числа корпуса и щита на телефоне. */
  private detailTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly shipEl: HTMLElement,
    private readonly targetEl: HTMLElement,
    private readonly deathEl: HTMLElement,
    onClearTarget: () => void,
    onInvite: () => void,
    onTrade: () => void = () => {},
  ) {
    this.ownHull = new Bar(shipEl, 'hull', 'Корпус');
    this.ownShield = new Bar(shipEl, 'shield', 'Щит');
    this.ownProtect = div(shipEl, 'protect sro-muted');
    this.ownThreat = div(shipEl, 'threat sro-danger');
    // На телефоне подписи и числа скрыты (две полоски и всё), но узнать точные значения надо уметь:
    // тап по блоку показывает их на DETAIL_MS, повторный — гасит сразу. На ПК числа видны всегда.
    shipEl.dataset.detail = 'false';
    shipEl.addEventListener('click', () => this.showDetail(shipEl.dataset.detail !== 'true'));

    const head = div(targetEl, 'target-head sro-target__head');
    this.targetName = div(head, 'target-name sro-target__name');
    this.targetClass = div(head, 'target-class sro-target__class');
    // Позвать пилота в группу — прямо с карточки: так удобно и на телефоне.
    this.invite = document.createElement('button');
    this.invite.type = 'button';
    this.invite.className = 'target-invite sro-btn sro-btn--xs';
    this.invite.textContent = 'В группу';
    this.invite.hidden = true;
    this.invite.addEventListener('click', () => {
      this.invite.blur();
      onInvite();
    });
    head.append(this.invite);
    // Обмен — оттуда же, где зовут в группу: цель уже выбрана, и второй раз её искать незачем (M16b).
    this.trade = document.createElement('button');
    this.trade.type = 'button';
    this.trade.className = 'target-trade sro-btn sro-btn--xs';
    this.trade.textContent = 'Обмен';
    this.trade.hidden = true;
    this.trade.addEventListener('click', () => {
      this.trade.blur();
      onTrade();
    });
    head.append(this.trade);
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'target-close sro-target__close';
    close.textContent = '✕';
    close.setAttribute('aria-label', 'Снять цель');
    close.addEventListener('click', () => {
      close.blur(); // иначе Space «нажмёт» кнопку
      onClearTarget();
    });
    head.append(close);
    this.targetHull = new Bar(targetEl, 'hull', 'Корпус');
    this.targetShield = new Bar(targetEl, 'shield', 'Щит');
    this.targetInfo = div(targetEl, 'target-info sro-target__info sro-num');
    div(targetEl, 'target-fire sro-target__fire').textContent = 'ОГОНЬ ПО ГОТОВНОСТИ';

    div(deathEl, 'death-title title-death').textContent = 'Корабль уничтожен';
    this.deathBy = div(deathEl, 'death-by');
    this.deathTimer = div(deathEl, 'death-timer sro-num');
  }

  update(own: OwnStatus | null, target: TargetStatus | null, death: DeathStatus | null): void {
    const now = performance.now();
    // Карточка и плашка огня появляются и исчезают сразу, числа обновляются не чаще RENDER_INTERVAL_MS.
    const visibilityChanged =
      this.shipEl.hidden !== !own ||
      this.targetEl.hidden !== !target ||
      this.deathEl.hidden !== !death ||
      (target !== null && this.targetEl.dataset.fire !== String(target.fire)) ||
      (target !== null && (this.invite.hidden === target.invite || this.trade.hidden === target.trade));
    if (!visibilityChanged && now - this.lastRender < RENDER_INTERVAL_MS) return;
    this.lastRender = now;

    this.shipEl.hidden = !own;
    if (own) {
      this.ownHull.set(own.hp, own.maxHp);
      this.ownShield.set(own.sh, own.maxSh);
      setText(this.ownProtect, own.protectedSeconds > 0 ? `защита ${Math.ceil(own.protectedSeconds)} с` : '');
      setText(this.ownThreat, own.attackers > 0 ? `под атакой: ${own.attackers}` : '');
    }

    this.targetEl.hidden = !target;
    if (target) {
      setText(this.targetName, target.name);
      setText(this.targetClass, target.member ? `${target.hullName} · в группе` : target.hullName);
      this.invite.hidden = !target.invite;
      this.trade.hidden = !target.trade;
      // На телефоне карточку заменили полосками над кораблём (M16c), но «В группу» и «Обмен» больше
      // нажать негде — ради них карточка остаётся, когда на ней есть кнопки. Прячет её style.css.
      this.targetEl.dataset.actions = String(target.invite || target.trade);
      this.targetEl.dataset.member = String(target.member);
      this.targetHull.set(target.hp, target.maxHp);
      this.targetShield.set(target.sh, target.maxSh);
      const { state, distance, chance } = target.aim;
      setText(
        this.targetInfo,
        state === 'dead'
          ? STATE_TEXT.dead
          : `${formatSectors(distance, target.sectorUnit)} сект. · шанс ${Math.round(chance)}% · ${STATE_TEXT[state]}`,
      );
      this.targetEl.dataset.state = state;
      this.targetEl.dataset.fire = String(target.fire);
    }

    this.deathEl.hidden = !death;
    if (death) {
      setText(this.deathBy, death.by ? `уничтожил: ${death.by}` : '');
      setText(this.deathTimer, `возвращение на базу через ${clock(death.seconds)}`);
    }
  }

  /** Показать числа корпуса и щита поверх полосок и через DETAIL_MS убрать. Нужно только на телефоне. */
  private showDetail(show: boolean): void {
    if (this.detailTimer !== null) clearTimeout(this.detailTimer);
    this.detailTimer = null;
    this.shipEl.dataset.detail = String(show);
    if (show) this.detailTimer = setTimeout(() => (this.shipEl.dataset.detail = 'false'), DETAIL_MS);
  }
}

function div(parent: HTMLElement, className: string): HTMLElement {
  const el = document.createElement('div');
  el.className = className;
  parent.append(el);
  return el;
}

function setText(el: HTMLElement, text: string): void {
  if (el.textContent !== text) el.textContent = text;
}
