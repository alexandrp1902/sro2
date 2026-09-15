import type { Aim, AimState } from '../sim/combat';

const RENDER_INTERVAL_MS = 100;

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
}

export interface DeathStatus {
  /** Кто уничтожил; пусто — неизвестно. */
  by: string;
  /** До возвращения на базу, с. */
  seconds: number;
}

/** Полоска «Корпус» или «Щит» с числом. */
class Bar {
  private readonly fill: HTMLElement;
  private readonly label: HTMLElement;
  private shown = '';

  constructor(
    parent: HTMLElement,
    kind: 'hull' | 'shield',
    private readonly title: string,
  ) {
    const bar = document.createElement('div');
    bar.className = `bar bar-${kind}`;
    this.fill = document.createElement('i');
    this.label = document.createElement('span');
    bar.append(this.fill, this.label);
    parent.append(bar);
  }

  set(value: number, max: number): void {
    const key = `${value}|${max}`;
    if (key === this.shown) return;
    this.shown = key;
    const share = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
    this.fill.style.width = `${share * 100}%`;
    this.label.textContent = max > 0 ? `${this.title} ${Math.ceil(value)} / ${Math.round(max)}` : `${this.title} —`;
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
  private readonly deathBy: HTMLElement;
  private readonly deathTimer: HTMLElement;
  private lastRender = 0;

  constructor(
    private readonly shipEl: HTMLElement,
    private readonly targetEl: HTMLElement,
    private readonly deathEl: HTMLElement,
    onClearTarget: () => void,
  ) {
    this.ownHull = new Bar(shipEl, 'hull', 'Корпус');
    this.ownShield = new Bar(shipEl, 'shield', 'Щит');
    this.ownProtect = div(shipEl, 'protect');
    this.ownThreat = div(shipEl, 'threat');

    const head = div(targetEl, 'target-head');
    this.targetName = div(head, 'target-name');
    this.targetClass = div(head, 'target-class');
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'target-close';
    close.textContent = '✕';
    close.setAttribute('aria-label', 'Снять цель');
    close.addEventListener('click', () => {
      close.blur(); // иначе Space «нажмёт» кнопку
      onClearTarget();
    });
    head.append(close);
    this.targetHull = new Bar(targetEl, 'hull', 'Корпус');
    this.targetShield = new Bar(targetEl, 'shield', 'Щит');
    this.targetInfo = div(targetEl, 'target-info');

    div(deathEl, 'death-title').textContent = 'КОРАБЛЬ УНИЧТОЖЕН';
    this.deathBy = div(deathEl, 'death-by');
    this.deathTimer = div(deathEl, 'death-timer');
  }

  update(own: OwnStatus | null, target: TargetStatus | null, death: DeathStatus | null): void {
    const now = performance.now();
    // Карточка появляется и исчезает сразу, числа обновляются не чаще RENDER_INTERVAL_MS.
    const visibilityChanged = this.shipEl.hidden !== !own || this.targetEl.hidden !== !target || this.deathEl.hidden !== !death;
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
      setText(this.targetClass, target.hullName);
      this.targetHull.set(target.hp, target.maxHp);
      this.targetShield.set(target.sh, target.maxSh);
      const { state, distance, chance } = target.aim;
      setText(
        this.targetInfo,
        state === 'dead' ? STATE_TEXT.dead : `${Math.round(distance)} м · шанс ${Math.round(chance)}% · ${STATE_TEXT[state]}`,
      );
      this.targetEl.dataset.state = state;
    }

    this.deathEl.hidden = !death;
    if (death) {
      setText(this.deathBy, death.by ? `уничтожил: ${death.by}` : '');
      setText(this.deathTimer, `возвращение на базу через ${clock(death.seconds)}`);
    }
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

/** 00:08 */
function clock(seconds: number): string {
  const total = Math.max(0, Math.ceil(seconds));
  return `${String(Math.floor(total / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`;
}
