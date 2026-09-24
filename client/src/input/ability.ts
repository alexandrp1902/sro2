import { revealOnTouch } from './gestures';

/** Что показывает кнопка: ставить нечего, готово, откат, работает. */
export type AbilityState = 'empty' | 'ready' | 'cooling' | 'active';

const STATE_LABELS: Record<AbilityState, string> = {
  empty: '—',
  ready: 'МОД',
  cooling: 'МОД',
  active: 'МОД',
};

/**
 * Кнопка активного модуля на телефоне (M20c) — пока заглушка: активируемых модулей в игре нет.
 * «Маскировка» из modules.json — пассивный множитель дальности обнаружения, его читает сервер сам,
 * и мин в игре тоже нет. Поэтому кнопка всегда в состоянии empty: погашена, с пунктирным кольцом,
 * подписана «—» и в сеть не ходит.
 *
 * Сделана по образцу FireControl и стоит на своём месте в раскладке, чтобы настоящая способность
 * встала сюда без переделки разметки: появятся setState('ready'), кольцо отката по образцу .fire-ring
 * и onPress с сообщением протокола.
 */
export class AbilityButton {
  /** Пока null: нажатие ничего не делает, кроме подсказки, которую ставит main.ts. */
  onPress: (() => void) | null = null;

  private state: AbilityState = 'empty';
  private readonly label: HTMLElement | null;

  constructor(private readonly el: HTMLElement) {
    this.label = el.querySelector('.sro-pad-btn__label');
    revealOnTouch(el);
    // aria-disabled, а не disabled: на телефоне title не виден, и совсем глухая кнопка ничему не учит —
    // нажатие должно сказать словами, почему ничего не произошло.
    el.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      this.onPress?.();
    });
    this.show();
  }

  setState(state: AbilityState): void {
    if (state === this.state) return;
    this.state = state;
    this.show();
  }

  /** @param _now время кадра: понадобится кольцу отката, когда появится первая способность */
  render(_now: number): void {
    // Пустой кнопке нечего перерисовывать: кольцо у неё пунктирное, от CSS.
  }

  private show(): void {
    this.el.dataset.state = this.state;
    this.el.setAttribute('aria-disabled', String(this.state === 'empty'));
    if (this.label) this.label.textContent = STATE_LABELS[this.state];
  }
}
