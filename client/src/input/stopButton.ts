import type { Controls } from './controls';
import { revealOnTouch } from './gestures';

/**
 * Кнопка «Стоп» на телефоне (M20c) — переключатель: нажал, и корабль стоит, пока не нажмёшь снова.
 * Стиком при этом можно крутиться на месте: замок держит только тягу, направление проходит как обычно.
 *
 * Единственный стоп до неё — двойной тап по стику, и он срабатывал не всегда: касание, сдвинувшееся
 * больше чем на TAP_SLOP_PX, тапом уже не считается.
 */
export class StopButton {
  private shown: boolean | null = null;

  constructor(
    private readonly el: HTMLElement,
    private readonly controls: Controls,
  ) {
    revealOnTouch(el);
    el.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      this.controls.toggleStop();
      this.render();
    });
    this.render();
  }

  /** Зовётся каждый кадр: замок снимают и со стороны (прыжок, док, гибель), а события об этом нет. */
  render(): void {
    const on = this.controls.stopLock;
    if (on === this.shown) return;
    this.shown = on;
    this.el.setAttribute('aria-pressed', String(on));
  }
}
