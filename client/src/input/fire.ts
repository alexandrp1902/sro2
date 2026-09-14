export type FireAim = 'ready' | 'blocked' | 'none';

/**
 * Атака (GDD §47): пока нажат Space или кнопка «Огонь», пушка стреляет сама по готовности.
 * Серверу уходит только смена состояния (fire {on}).
 */
export class FireControl {
  /** Атаку нажали — вызывается до отправки: здесь выбирается цель, если её нет. */
  onPress: (() => void) | null = null;
  onChange: ((on: boolean) => void) | null = null;

  private key = false;
  private button = false;
  private on = false;
  private reloadStart = 0;
  private reloadMs = 0;
  private shownReload = -1;
  private shownAim = '';

  constructor(private readonly el: HTMLElement) {
    // На ПК кнопка не нужна: показываем на сенсорных экранах или после первого касания.
    if (matchMedia('(pointer: coarse)').matches) el.hidden = false;
    window.addEventListener('pointerdown', (e) => {
      if (e.pointerType === 'touch') el.hidden = false;
    });

    el.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      try {
        el.setPointerCapture(e.pointerId); // палец, съехавший с кнопки, продолжает стрелять
      } catch {
        // указатель уже исчез
      }
      this.setButton(true);
    });
    // Пункт управления, звонок, свайп home-indicator дают pointercancel — это отпускание.
    for (const type of ['pointerup', 'pointercancel', 'lostpointercapture'] as const) {
      el.addEventListener(type, () => this.setButton(false));
    }
  }

  get held(): boolean {
    return this.on;
  }

  setKey(down: boolean): void {
    this.key = down;
    this.sync();
  }

  setButton(down: boolean): void {
    this.button = down;
    this.sync();
  }

  /** Отпустить всё (потеря фокуса, фон, уничтожение): стрелять снова — только новым нажатием. */
  release(): void {
    this.key = false;
    this.button = false;
    this.sync();
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

  private sync(): void {
    const on = this.key || this.button;
    this.el.dataset.active = String(on);
    if (on === this.on) return;
    this.on = on;
    if (on) this.onPress?.();
    this.onChange?.(on);
  }
}

/** Клавиши боя на ПК (GDD §7): Space — атака, Tab — следующая цель, Esc — снять цель. */
export function bindCombatKeys(fire: FireControl, actions: { next(): void; clear(): void }): void {
  const isTyping = (e: Event) => e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;

  window.addEventListener('keydown', (e) => {
    if (isTyping(e) || e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.code === 'Space') {
      e.preventDefault(); // иначе Space нажал бы кнопку в фокусе или прокрутил страницу
      if (!e.repeat) fire.setKey(true);
    } else if (e.code === 'Tab') {
      e.preventDefault();
      actions.next();
    } else if (e.code === 'Escape') {
      actions.clear();
    }
  });
  window.addEventListener('keyup', (e) => {
    if (e.code !== 'Space') return;
    e.preventDefault();
    fire.setKey(false);
  });
  window.addEventListener('blur', () => fire.release());
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') fire.release();
  });
}
