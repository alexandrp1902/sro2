/**
 * Карточка сюжетного диалога (M20a): кто говорит, что говорит и что можно ответить.
 *
 * Портретов в игре пока нет, поэтому говорящего представляет значок кампании — две буквы имени
 * в рамке, нарисованные кодом. Слот под картинку в разметке уже есть (`.dialog-face`), и когда
 * портреты появятся, он заполняется одной строкой, не трогая ничего остального.
 *
 * Живёт своим хостом рядом с остальными карточками, а не внутри дока: док перерисовывается целиком,
 * и карточка, вложенная в него, пропадала бы от любой покупки.
 */

import type { DialogOptionDto } from '../net/protocol';

/** Что показывает карточка. Отдельный тип: демо-экран строит её без сервера. */
export interface DialogView {
  who: string;
  role: string;
  lines: string[];
  options?: DialogOptionDto[] | null;
}

/**
 * Монограмма говорящего: первые буквы имени и фамилии, а у одного слова — две его первые.
 * Чистая — на ней стоят тесты: «Ева Морен» → «ЕМ», «Перехват» → «ПЕ», кавычки и скобки не в счёт.
 */
export function monogram(who: string): string {
  const words = who
    .split(/[\s·]+/)
    .map((w) => w.replace(/[^\p{L}\p{N}]/gu, ''))
    .filter((w) => w.length > 0);
  if (words.length === 0) return '?';
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase();
  return (words[0][0] + words[1][0]).toUpperCase();
}

/** Подпись под именем: «инженер рудника · Тихая война», без пустых разделителей. */
export function dialogCaption(role: string, campaign: string): string {
  return [role, campaign].filter((s) => s.trim().length > 0).join(' · ');
}

export class DialogCard {
  private readonly face: HTMLElement;
  private readonly who: HTMLElement;
  private readonly caption: HTMLElement;
  private readonly body: HTMLElement;
  private readonly actions: HTMLElement;

  /** Кого спрашиваем; пустая строка — карточка просто показывает реплику. */
  private answer: ((flag: string) => void) | null = null;

  constructor(private readonly root: HTMLElement) {
    const head = document.createElement('div');
    head.className = 'dialog-head';
    this.face = document.createElement('div');
    this.face.className = 'dialog-face';
    const names = document.createElement('div');
    names.className = 'dialog-names';
    this.who = document.createElement('div');
    this.who.className = 'dialog-who sro-dialog__title';
    this.caption = document.createElement('div');
    this.caption.className = 'dialog-role sro-label sro-muted';
    names.append(this.who, this.caption);
    head.append(this.face, names);

    this.body = document.createElement('div');
    this.body.className = 'dialog-lines';
    this.actions = document.createElement('div');
    this.actions.className = 'sro-dialog__actions';
    root.append(head, this.body, this.actions);
    root.hidden = true;
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  /**
   * Показать реплику. Пока карточка открыта, следующая её заменяет: сервер шлёт их по одной,
   * и очередь из двух карточек читалась бы как «нажми дважды».
   */
  show(view: DialogView, campaign: string, onAnswer?: (flag: string) => void): void {
    this.face.textContent = monogram(view.who);
    this.who.textContent = view.who;
    this.caption.textContent = dialogCaption(view.role, campaign);
    this.body.replaceChildren(
      ...view.lines.map((line) => {
        const row = document.createElement('div');
        row.className = 'dialog-line sro-dialog__text';
        row.textContent = line;
        return row;
      }),
    );

    const options = view.options ?? [];
    this.answer = options.length > 0 ? (onAnswer ?? null) : null;
    this.actions.replaceChildren(
      ...(options.length > 0
        ? options.map((option, i) => this.button(option.label, i === 0 ? 'sro-btn sro-btn--primary' : 'sro-btn', () => {
            const reply = this.answer;
            this.hide();
            reply?.(option.flag);
          }))
        : [this.button('Дальше', 'sro-btn', () => this.hide())]),
    );
    this.root.hidden = false;
  }

  hide(): void {
    this.root.hidden = true;
    this.answer = null;
  }

  private button(label: string, className: string, onClick: () => void): HTMLButtonElement {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `dialog-btn ${className}`;
    button.textContent = label;
    button.addEventListener('click', () => {
      button.blur();
      onClick();
    });
    return button;
  }
}
