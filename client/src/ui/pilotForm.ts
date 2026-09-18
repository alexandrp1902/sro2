import type { DeniedCode } from '../net/protocol';
import { normalizeServerUrl } from '../net/serverUrl';

/** Почему сервер не пустил: он шлёт код, текст живёт здесь. */
const DENIED: Record<DeniedCode, string> = {
  badName: 'Ник — от 3 до 16 символов',
  badPassword: 'Пароль — от 4 до 64 символов',
  wrongPassword: 'Ник занят, пароль не подходит',
  badKey: 'Вход на этом устройстве устарел — введите пароль',
};

export function describeDenied(code: string): string {
  return DENIED[code as DeniedCode] ?? 'Вход отклонён';
}

export interface PilotFormHandlers {
  /** Войти по нику и паролю; свободный ник заводит аккаунт. */
  onLogin(name: string, password: string): void;
  onLogout(): void;
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
  private state: PilotFormState = { url: null, name: '', loggedIn: false };

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
      handlers.onLogin(name, password);
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

  show(state: PilotFormState, error = ''): void {
    this.state = state;
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
}
