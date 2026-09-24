/** Вопрос «точно?» поверх HUD (M15.5). Устроен как карточка приглашения в группу: текст и две кнопки. */

export interface ConfirmLines {
  title: string;
  text: string;
  yes: string;
  no: string;
}

/**
 * Текст подтверждения выхода. Чистая: тексты проверяют тесты.
 *
 * В полёте корабль остаётся в космосе, пока сервер его не уберёт (после обрыва связи он ждёт около
 * минуты), и об этом надо предупредить. В доке он припаркован, и спрашивать не о чем.
 */
export function logoutLines(docked: boolean): ConfirmLines {
  return {
    title: 'Выйти из игры?',
    text: docked
      ? 'Корабль останется в доке.'
      : 'Корабль останется в космосе, пока сервер его не уберёт, — около минуты. Его могут сбить.',
    yes: 'Выйти',
    no: 'Отмена',
  };
}

/**
 * Продажа модуля со склада (M20): вопрос перед тем, как вещь исчезнет. Кнопка «Продать» стоит вплотную
 * к «Поставить», и промах пальцем на телефоне стоил бы модуля — выкупить его можно только за полную цену,
 * а там, где его не продают, и вовсе никак.
 *
 * @param credits сколько дадут; 0 — здесь это не покупают, и вещь просто выбрасывают
 */
export function sellItemLines(name: string, credits: number, price: (value: number) => string): ConfirmLines {
  return credits > 0
    ? {
        title: 'Продать?',
        text: `${name} уйдёт со склада за ${price(credits)}. Обратно — только за полную цену.`,
        yes: 'Продать',
        no: 'Отмена',
      }
    : {
        title: 'Выбросить?',
        text: `${name} здесь не покупают. Выброшенное пропадёт совсем.`,
        yes: 'Выбросить',
        no: 'Отмена',
      };
}

export class ConfirmCard {
  private readonly title: HTMLElement;
  private readonly text: HTMLElement;
  private readonly yes: HTMLButtonElement;
  private readonly no: HTMLButtonElement;
  private answer: (() => void) | null = null;

  constructor(private readonly root: HTMLElement) {
    this.title = document.createElement('div');
    this.title.className = 'confirm-title sro-dialog__title';
    this.text = document.createElement('div');
    this.text.className = 'confirm-text sro-dialog__text';
    const buttons = document.createElement('div');
    buttons.className = 'confirm-buttons sro-dialog__actions';
    // Опасная кнопка слева, «Отмена» справа; красная — потому что действие не отменить.
    this.yes = document.createElement('button');
    this.yes.type = 'button';
    this.yes.className = 'confirm-yes sro-btn sro-btn--danger';
    this.no = document.createElement('button');
    this.no.type = 'button';
    this.no.className = 'confirm-no sro-btn';
    this.yes.addEventListener('click', () => {
      this.yes.blur();
      const answer = this.answer;
      this.hide();
      answer?.();
    });
    this.no.addEventListener('click', () => {
      this.no.blur();
      this.hide();
    });
    buttons.append(this.yes, this.no);
    root.append(this.title, this.text, buttons);
    root.hidden = true;
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  ask(lines: ConfirmLines, onYes: () => void): void {
    this.title.textContent = lines.title;
    this.text.textContent = lines.text;
    this.yes.textContent = lines.yes;
    this.no.textContent = lines.no;
    this.answer = onYes;
    this.root.hidden = false;
  }

  hide(): void {
    this.answer = null;
    this.root.hidden = true;
  }
}
