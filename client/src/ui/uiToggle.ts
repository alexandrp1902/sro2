/**
 * Кнопка «скрыть» / «показать» внизу по центру экрана телефона: убирает панели и кнопки полёта и
 * возвращает их обратно. Главное назначение — достать цель, которую закрывали кнопки: тапнуть её в мире
 * и вернуть интерфейс.
 *
 * Прячет только DOM — HUD, стик и кнопки боя — классом ui-hidden на body (правила в style.css). То, что
 * Pixi рисует поверх мира, остаётся: стрелки к целям по краям экрана, выделение цели, полоски, дуга
 * оружия. Стик и огонь при этом не отпускаются: курс и стрельба идут как шли. Состояние не сохраняется:
 * после перезагрузки или стыковки интерфейс снова на месте, иначе можно потерять управление и не понять,
 * куда оно делось.
 */
export class UiToggle {
  private hidden = false;
  private docked = false;

  constructor(
    private readonly button: HTMLButtonElement,
    private readonly touch: boolean,
    private readonly onChange: (hidden: boolean) => void,
  ) {
    button.addEventListener('click', () => {
      button.blur();
      this.set(!this.hidden);
    });
    this.paint();
  }

  /** В доке кнопка не нужна: там свой экран. Стыковка заодно возвращает интерфейс. */
  setDocked(docked: boolean): void {
    this.docked = docked;
    if (docked) this.set(false);
    else this.paint();
  }

  private set(hidden: boolean): void {
    if (hidden === this.hidden) {
      this.paint();
      return;
    }
    this.hidden = hidden;
    document.body.classList.toggle('ui-hidden', hidden);
    this.paint();
    this.onChange(hidden);
  }

  private paint(): void {
    this.button.hidden = !this.touch || this.docked;
    this.button.textContent = this.hidden ? 'показать' : 'скрыть';
  }
}
