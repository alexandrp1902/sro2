/**
 * Окно «Сменить пароль» (M15.7). Открывается из бургер-меню и живёт рядом с ним, а не внутри дока:
 * экран дока перерисовывается целиком после каждой покупки и унёс бы окно вместе с набранным текстом.
 *
 * Сервер отвечает уведомлением в ленту (passwordChanged / wrongPassword / badPassword), поэтому своих
 * ответов окно не разбирает: набрал — отправил — закрылось. Проверяет оно только то, что видно на месте,
 * — длину и совпадение повтора, — чтобы не гонять на сервер заведомо негодное.
 */

/** Те же границы, что у AccountStore.MinPasswordLength / MaxPasswordLength на сервере. */
export const MIN_PASSWORD_LENGTH = 4;
export const MAX_PASSWORD_LENGTH = 64;

/**
 * Что не так с набранным. Чистая: её и проверяют тесты — DOM для этого не нужен.
 *
 * @returns текст ошибки или null, если можно отправлять
 */
export function passwordProblem(current: string, fresh: string, repeat: string): string | null {
  if (current.length === 0) return 'Введите текущий пароль';
  if (fresh.length < MIN_PASSWORD_LENGTH || fresh.length > MAX_PASSWORD_LENGTH)
    return `Новый пароль — от ${MIN_PASSWORD_LENGTH} до ${MAX_PASSWORD_LENGTH} символов`;
  if (fresh !== repeat) return 'Пароли не совпадают';
  if (fresh === current) return 'Новый пароль совпадает со старым';
  return null;
}

export class PasswordForm {
  private readonly current: HTMLInputElement;
  private readonly fresh: HTMLInputElement;
  private readonly repeat: HTMLInputElement;
  private readonly error: HTMLElement;

  constructor(
    private readonly root: HTMLElement,
    private readonly onChange: (current: string, fresh: string) => void,
  ) {
    // onsubmit: пока скрипт не загрузился (или упал), браузер не должен увести пароли в адрес страницы.
    const card = document.createElement('form');
    card.className = 'password-card sro-pane sro-pane--window';
    card.setAttribute('onsubmit', 'return false');

    const title = document.createElement('div');
    title.className = 'password-title title-dialog';
    title.textContent = 'Сменить пароль';
    const hint = document.createElement('div');
    hint.className = 'password-hint sro-muted';
    hint.textContent = 'На других устройствах придётся войти заново: смена пароля отзывает прежние входы.';
    card.append(title, hint);

    this.current = this.field(card, 'password-current', 'Текущий пароль', 'current-password');
    this.fresh = this.field(card, 'password-new', 'Новый пароль', 'new-password');
    this.repeat = this.field(card, 'password-repeat', 'Новый пароль ещё раз', 'new-password');

    this.error = document.createElement('div');
    this.error.className = 'password-error sro-danger';
    this.error.hidden = true;
    card.append(this.error);

    const actions = document.createElement('div');
    actions.className = 'password-actions sro-dialog__actions';
    const apply = document.createElement('button');
    apply.type = 'submit';
    apply.className = 'sro-btn sro-btn--primary';
    apply.textContent = 'Сменить';
    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'sro-btn';
    cancel.textContent = 'Отмена';
    actions.append(apply, cancel);
    card.append(actions);

    apply.addEventListener('click', () => this.submit());
    card.addEventListener('submit', () => this.submit()); // Enter в любом поле
    cancel.addEventListener('click', () => this.hide());
    // Щелчок мимо карточки закрывает окно — как у меню, «Управления» и карты галактики.
    root.addEventListener('pointerdown', (e) => {
      if (e.target === root) this.hide();
    });

    root.append(card);
    root.hidden = true;
  }

  private field(card: HTMLElement, id: string, label: string, autocomplete: AutoFill): HTMLInputElement {
    const caption = document.createElement('label');
    caption.className = 'password-label sro-field';
    caption.htmlFor = id;
    caption.textContent = label;
    const input = document.createElement('input');
    input.id = id;
    input.className = 'sro-input';
    input.type = 'password';
    input.maxLength = MAX_PASSWORD_LENGTH;
    input.autocomplete = autocomplete;
    card.append(caption, input);
    return input;
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  show(): void {
    this.current.value = this.fresh.value = this.repeat.value = '';
    this.error.hidden = true;
    this.root.hidden = false;
    this.current.focus();
  }

  hide(): void {
    // Набранное в окне не остаётся: пароль не должен лежать в живом DOM дольше, чем нужен.
    this.current.value = this.fresh.value = this.repeat.value = '';
    this.root.hidden = true;
  }

  private submit(): void {
    const problem = passwordProblem(this.current.value, this.fresh.value, this.repeat.value);
    if (problem !== null) {
      this.error.textContent = problem;
      this.error.hidden = false;
      return;
    }
    const current = this.current.value;
    const fresh = this.fresh.value;
    this.hide();
    this.onChange(current, fresh);
  }
}
