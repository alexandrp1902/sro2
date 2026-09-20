import { keymap as defaultKeymap, type Keymap } from './keymap';

export type FireAim = 'ready' | 'blocked' | 'none';

/**
 * Что делает кнопка огня: стреляет, берёт выбранный предмет, стыкует со станцией, садится на планету (M15),
 * начинает гиперпрыжок у выбранных врат или отменяет его подготовку.
 */
export type FireMode = 'fire' | 'grab' | 'dock' | 'land' | 'jump' | 'cancel';

const MODE_LABELS: Record<FireMode, string> = {
  fire: 'ОГОНЬ',
  grab: 'ВЗЯТЬ',
  dock: 'ДОК',
  land: 'ПОСАДКА',
  jump: 'ПРЫЖОК',
  cancel: 'ОТМЕНА',
};

/**
 * Шаг выбора цели по клавише (ПК), по раскладке: по умолчанию Q, Shift+Tab — предыдущая; E, Tab — следующая.
 * @returns 0 — клавиша не выбирает цель
 */
export function targetStep(e: { code: string; shiftKey: boolean }, keys: Keymap = defaultKeymap): -1 | 0 | 1 {
  const action = keys.actionFor(e);
  return action === 'targetPrev' ? -1 : action === 'targetNext' ? 1 : 0;
}

/**
 * Атака (GDD §47) — переключатель: Space, кнопка «ОГОНЬ» или двойной тап по цели включают огонь, повторное
 * нажатие выключает. Пока огонь включён, пушка стреляет по цели сама по готовности. Кнопка горит, пока огонь включён.
 * Серверу уходит только смена состояния (fire {on}).
 */
export class FireControl {
  /** Огонь включают — вызывается до отправки: здесь выбирается цель, если её нет. false — стрелять не в кого. */
  onPress: (() => boolean) | null = null;
  onChange: ((on: boolean) => void) | null = null;
  /** Выбран предмет или станция — та же кнопка берёт его или стыкует, а не стреляет. true — огонь не трогаем. */
  onGrab: (() => boolean) | null = null;

  private on = false;
  private reloadStart = 0;
  private reloadMs = 0;
  private shownReload = -1;
  private shownAim = '';
  private mode: FireMode = 'fire';
  private readonly stateLabel: HTMLElement | null;
  private label: HTMLElement | null = null;

  /** @param pad кнопки боя телефона (огонь и выбор цели): на ПК не нужны — показываем на сенсорных экранах */
  constructor(
    private readonly el: HTMLElement,
    pad: HTMLElement,
  ) {
    this.stateLabel = el.querySelector('.fire-state');
    if (matchMedia('(pointer: coarse)').matches) pad.hidden = false;
    window.addEventListener('pointerdown', (e) => {
      if (e.pointerType === 'touch') pad.hidden = false;
    });
    this.label = el.querySelector('.fire-label');
    el.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      if (this.onGrab?.()) return;
      this.toggle();
    });
    this.show();
  }

  get active(): boolean {
    return this.on;
  }

  toggle(): void {
    this.set(!this.on);
  }

  set(on: boolean): void {
    if (on === this.on) return;
    if (on && this.onPress && !this.onPress()) return;
    this.on = on;
    this.show();
    this.onChange?.(on);
  }

  /** Выключить (цель снята, вкладка в фоне, корабль уничтожен): стрелять снова — только новым нажатием. */
  release(): void {
    this.set(false);
  }

  /**
   * Пока выбран предмет, кнопка подписана «ВЗЯТЬ», станция — «ДОК», врата — «ПРЫЖОК» (а во время подготовки —
   * «ОТМЕНА»): на телефоне других кнопок для подбора, стыковки и прыжка нет.
   */
  setMode(mode: FireMode): void {
    if (mode === this.mode) return;
    this.mode = mode;
    this.el.dataset.grab = String(mode !== 'fire');
    if (this.label) this.label.textContent = MODE_LABELS[mode];
  }

  /** Свой выстрел: кольцо перезарядки начинается заново. */
  reloadFrom(now: number, ms: number): void {
    this.reloadStart = now;
    this.reloadMs = ms;
  }

  /** @param aim можно ли сейчас попасть по цели — от этого цвет кнопки */
  render(now: number, aim: FireAim): void {
    const reload = this.reloadMs > 0 ? Math.min(1, (now - this.reloadStart) / this.reloadMs) : 1;
    const rounded = Math.round(reload * 60) / 60;
    if (rounded !== this.shownReload) {
      this.shownReload = rounded;
      this.el.style.setProperty('--reload', String(rounded));
    }
    if (aim !== this.shownAim) {
      this.shownAim = aim;
      this.el.dataset.aim = aim;
    }
  }

  private show(): void {
    this.el.dataset.active = String(this.on);
    this.el.setAttribute('aria-pressed', String(this.on));
    if (this.stateLabel) this.stateLabel.textContent = this.on ? 'вкл' : 'выкл';
  }
}

/**
 * Клавиши боя на ПК (GDD §7), по раскладке. По умолчанию: Space — взять выбранный предмет, пристыковаться
 * к выбранной станции или вылететь из дока, а иначе огонь вкл/выкл; Q/E, Tab/Shift+Tab — предыдущая/следующая
 * цель; Esc — снять предмет или станцию, потом цель.
 */
export function bindCombatKeys(
  fire: FireControl,
  actions: { step(direction: -1 | 1): void; clear(): void; grab(): boolean },
  keys: Keymap = defaultKeymap,
): void {
  const isTyping = (e: Event) => e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;

  window.addEventListener('keydown', (e) => {
    if (keys.capturing || isTyping(e) || e.ctrlKey || e.metaKey || e.altKey) return;
    const action = keys.actionFor(e);
    if (action === 'fire') {
      e.preventDefault(); // иначе Space нажал бы кнопку в фокусе или прокрутил страницу
      if (!e.repeat && !actions.grab()) fire.toggle();
      return;
    }
    const step = targetStep(e, keys);
    if (step !== 0) {
      e.preventDefault();
      if (!e.repeat) actions.step(step);
    } else if (action === 'targetClear') {
      actions.clear();
    }
  });
  // Вкладка ушла в фон — связь рвётся; вернувшись, игрок включит огонь сам.
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') fire.release();
  });
}
