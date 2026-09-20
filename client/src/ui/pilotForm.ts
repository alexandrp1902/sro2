import type { CareerDto, DeniedCode, NameFreeMsg } from '../net/protocol';
import { normalizeServerUrl } from '../net/serverUrl';

/** Сколько ждать после последней буквы, прежде чем спросить сервер про ник. */
const CHECK_DELAY_MS = 300;

/** Почему сервер не пустил: он шлёт код, текст живёт здесь. */
const DENIED: Record<DeniedCode, string> = {
  badName: 'Ник — от 3 до 16 символов',
  badPassword: 'Пароль — от 4 до 64 символов',
  wrongPassword: 'Ник занят, пароль не подходит',
  badKey: 'Вход на этом устройстве устарел — введите пароль',
  badCareer: 'Этот путь пока закрыт',
};

/** Карточка пути на экране: какая выбрана и какая недоступна. Чистая — её и проверяют тесты. */
export interface CareerCard extends CareerDto {
  chosen: boolean;
}

/**
 * Что показать в ряду путей (M15.5). Карточки видны, только когда ник свободен: вошедшему
 * в старый аккаунт выбирать нечего, и предлагать ему выбор — обманывать.
 *
 * @param chosen Что выбрал игрок; null — ещё ничего, берётся путь по умолчанию.
 */
export function careerCards(message: NameFreeMsg | null, chosen: string | null): CareerCard[] {
  if (!message?.free || message.careers.length === 0) return [];
  const picked = chosen !== null && message.careers.some((c) => c.id === chosen && c.enabled) ? chosen : message.career;
  return message.careers.map((career) => ({ ...career, chosen: career.id === picked }));
}

export function describeDenied(code: string): string {
  return DENIED[code as DeniedCode] ?? 'Вход отклонён';
}

export interface PilotFormHandlers {
  /** Войти по нику и паролю; свободный ник заводит аккаунт, и тогда применяется путь. */
  onLogin(name: string, password: string, career: string | null): void;
  onLogout(): void;
  /** Спросить сервер, свободен ли ник (M15.5): от ответа зависит, показывать ли карточки пути. */
  onCheckName(name: string): void;
}

export interface PilotFormState {
  /** Адрес игрового сервера; null — не задан (GitHub Pages без ?server). */
  url: string | null;
  name: string;
  /** Уже вошли: можно просто закрыть окно или выйти. */
  loggedIn: boolean;
}

/**
 * Окно «Пилот» (GDD §61): вход по нику и паролю и адрес сервера. Свободный ник с паролем заводит аккаунт —
 * отдельной регистрации нет. Пока пилот не вошёл, окно не закрывается: без аккаунта играть не на чем.
 */
export class PilotForm {
  private readonly nameInput: HTMLInputElement;
  private readonly passwordInput: HTMLInputElement;
  private readonly serverInput: HTMLInputElement;
  private readonly error: HTMLElement;
  private readonly cancel: HTMLButtonElement;
  private readonly logout: HTMLButtonElement;
  private readonly submit: HTMLButtonElement;
  private readonly careers: HTMLElement;
  private state: PilotFormState = { url: null, name: '', loggedIn: false };
  /** Последний ответ сервера про ник; null — ещё не спрашивали или ответ устарел. */
  private free: NameFreeMsg | null = null;
  private chosen: string | null = null;
  private checkTimer = 0;

  constructor(
    private readonly root: HTMLElement,
    handlers: PilotFormHandlers,
  ) {
    this.nameInput = root.querySelector('input[name=name]')!;
    this.passwordInput = root.querySelector('input[name=password]')!;
    this.serverInput = root.querySelector('input[name=server]')!;
    this.error = root.querySelector('.connect-error')!;
    this.cancel = root.querySelector('.connect-cancel')!;
    this.logout = root.querySelector('.connect-logout')!;
    this.submit = root.querySelector('button[type=submit]')!;
    this.careers = root.querySelector('.connect-careers')!;

    // Спрашиваем про ник не на каждую букву, а когда печатать перестали.
    this.nameInput.addEventListener('input', () => {
      this.free = null;
      this.renderCareers();
      window.clearTimeout(this.checkTimer);
      const name = this.nameInput.value.trim();
      if (name.length < 3) return;
      this.checkTimer = window.setTimeout(() => handlers.onCheckName(name), CHECK_DELAY_MS);
    });

    root.querySelector('form')!.addEventListener('submit', (e) => {
      e.preventDefault();
      const server = this.serverInput.value.trim();
      let url: string | null = null;
      try {
        url = server ? normalizeServerUrl(server) : null;
      } catch {
        this.setError('Неверный адрес сервера');
        return;
      }
      if (url !== null && url !== this.state.url) {
        const params = new URLSearchParams(location.search);
        params.set('server', url);
        location.search = params.toString(); // перезагрузка: адрес подхватит resolveServerUrl
        return;
      }
      if (url === null) {
        this.setError('Укажите адрес игрового сервера');
        return;
      }

      const name = this.nameInput.value.trim();
      const password = this.passwordInput.value;
      if (this.state.loggedIn && !password && name === this.state.name) {
        this.hide(); // ничего не меняли
        return;
      }
      if (!name) return this.setError('Введите ник');
      if (!password) return this.setError('Введите пароль');
      this.setError('');
      this.submit.textContent = 'Вход…';
      // Путь шлём, только когда карточки видны: значит ник свободен и аккаунт заводится сейчас.
      const cards = careerCards(this.free, this.chosen);
      handlers.onLogin(name, password, cards.find((c) => c.chosen)?.id ?? null);
    });
    this.cancel.addEventListener('click', () => this.hide());
    this.logout.addEventListener('click', () => {
      handlers.onLogout();
      this.show({ ...this.state, loggedIn: false });
    });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** Ответ сервера про ник: свободен — показываем карточки пути. */
  setNameFree(message: NameFreeMsg): void {
    // Ответ про прежний ник: игрок успел допечатать, и показывать его уже нельзя.
    if (message.name !== this.nameInput.value.trim()) return;
    this.free = message;
    this.renderCareers();
  }

  show(state: PilotFormState, error = ''): void {
    this.state = state;
    this.free = null;
    this.chosen = null;
    this.renderCareers();
    this.nameInput.value = state.name;
    this.passwordInput.value = '';
    this.serverInput.value = state.url ?? '';
    this.cancel.hidden = !state.loggedIn;
    this.logout.hidden = !state.loggedIn;
    this.submit.textContent = 'Войти';
    this.setError(error);
    this.root.hidden = false;
    (state.url && !state.name ? this.nameInput : state.url ? this.passwordInput : this.serverInput).focus();
  }

  /** Вход не удался: окно остаётся, ник на месте, пароль — заново. */
  showError(text: string): void {
    this.passwordInput.value = '';
    this.submit.textContent = 'Войти';
    this.setError(text);
  }

  hide(): void {
    this.root.hidden = true;
    this.passwordInput.value = '';
    // Фокус в поле ввода глушит клавиши управления (keyboard.ts).
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  }

  private setError(text: string): void {
    this.error.textContent = text;
  }

  private renderCareers(): void {
    const cards = careerCards(this.free, this.chosen);
    this.careers.hidden = cards.length === 0;
    if (cards.length === 0) {
      this.careers.replaceChildren();
      return;
    }
    const title = document.createElement('div');
    title.className = 'connect-label sro-field';
    title.textContent = 'Новый пилот: с чего начнёте';
    const row = document.createElement('div');
    row.className = 'connect-career-row';
    for (const card of cards) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'connect-career';
      button.disabled = !card.enabled;
      button.setAttribute('aria-pressed', String(card.chosen));
      const name = document.createElement('div');
      name.className = 'connect-career-name';
      name.textContent = card.name;
      const hint = document.createElement('div');
      hint.className = 'connect-career-hint';
      hint.textContent = card.hint;
      button.append(name, hint);
      button.addEventListener('click', () => {
        this.chosen = card.id;
        this.renderCareers();
      });
      row.append(button);
    }
    this.careers.replaceChildren(title, row);
  }
}
