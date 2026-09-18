export type FireAim = 'ready' | 'blocked' | 'none';

/**
 * Шаг выбора цели по клавише (ПК): Q, Shift+←, Shift+Tab — предыдущая; E, Shift+→, Tab — следующая.
 * @returns 0 — клавиша не выбирает цель (← и → без Shift поворачивают корпус)
 */
export function targetStep(e: { code: string; shiftKey: boolean }): -1 | 0 | 1 {
  switch (e.code) {
    case 'KeyQ':
      return -1;
    case 'KeyE':
      return 1;
    case 'Tab':
      return e.shiftKey ? -1 : 1;
    case 'ArrowLeft':
      return e.shiftKey ? -1 : 0;
    case 'ArrowRight':
      return e.shiftKey ? 1 : 0;
    default:
      return 0;
  }
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
  /** Выбран предмет — та же кнопка берёт его, а не стреляет. true — команда ушла, огонь не трогаем. */
  onGrab: (() => boolean) | null = null;

  private on = false;
  private reloadStart = 0;
  private reloadMs = 0;
  private shownReload = -1;
  private shownAim = '';
  private grabMode = false;
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

  /** Пока выбран предмет, кнопка подписана «ВЗЯТЬ»: на телефоне другой кнопки для подбора нет. */
  setGrabMode(on: boolean): void {
    if (on === this.grabMode) return;
    this.grabMode = on;
    this.el.dataset.grab = String(on);
    if (this.label) this.label.textContent = on ? 'ВЗЯТЬ' : 'ОГОНЬ';
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
 * Клавиши боя на ПК (GDD §7): Space — взять выбранный предмет, а если предмет не выбран, то огонь вкл/выкл;
 * Q/E, Shift+←/→, Tab/Shift+Tab — предыдущая/следующая цель; Esc — снять предмет, потом цель.
 */
export function bindCombatKeys(
  fire: FireControl,
  actions: { step(direction: -1 | 1): void; clear(): void; grab(): boolean },
): void {
  const isTyping = (e: Event) => e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;

  window.addEventListener('keydown', (e) => {
    if (isTyping(e) || e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.code === 'Space') {
      e.preventDefault(); // иначе Space нажал бы кнопку в фокусе или прокрутил страницу
      if (!e.repeat && !actions.grab()) fire.toggle();
      return;
    }
    const step = targetStep(e);
    if (step !== 0) {
      e.preventDefault();
      if (!e.repeat) actions.step(step);
    } else if (e.code === 'Escape') {
      actions.clear();
    }
  });
  // Вкладка ушла в фон — связь рвётся; вернувшись, игрок включит огонь сам.
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') fire.release();
  });
}
