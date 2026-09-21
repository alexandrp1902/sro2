import type { CareerDto, DeniedCode, NameFreeMsg } from '../net/protocol';
import { normalizeServerUrl } from '../net/serverUrl';

/** Сколько ждать после последней буквы, прежде чем спросить сервер про ник. */
const CHECK_DELAY_MS = 300;

/** Заставка стартового экрана: рантайм-копия art/splash/splash-login.png (tools/splash.py). */
const START_ART = 'splash/login.webp';

/** Почему сервер не пустил: он шлёт код, текст живёт здесь. */
const DENIED: Record<DeniedCode, string> = {
  badName: 'Логин — от 3 до 16 символов',
  badPassword: 'Пароль — от 4 до 64 символов',
  wrongPassword: 'Пароль не подходит',
  badKey: 'Вход на этом устройстве устарел — введите пароль',
  badCareer: 'Этот путь пока закрыт',
  noAccount: 'Такого пилота нет — заведите его на вкладке «Регистрация»',
  nameTaken: 'Этот логин уже занят — войдите на вкладке «Вход»',
};

/** Вкладка окна (M15.8): вход в свой аккаунт или заведение нового. */
export type PilotTab = 'login' | 'register';

/** Что на вкладке своё: подпись кнопки, ожидание после нажатия и строка под названием игры. */
const TABS: Record<PilotTab, { submit: string; wait: string; hint: string }> = {
  login: { submit: 'Войти', wait: 'Вход…', hint: 'Введите логин и пароль своего пилота' },
  register: {
    submit: 'Создать пилота',
    wait: 'Создаём…',
    hint: 'Придумайте логин и пароль — так заводится новый пилот',
  },
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

/**
 * С какой вкладки открыть окно (M15.8). Ника не помним и живой сессии нет — это первый заход, и входить
 * такому игроку некуда: открываем «Регистрацию». Прежний экран этот случай прятал за одной кнопкой «Войти».
 */
export function startTab(state: PilotFormState): PilotTab {
  return state.loggedIn || state.name !== '' ? 'login' : 'register';
}

/** Чего не хватает в полях; null — можно отправлять. Длину логина и пароля по-прежнему судит сервер. */
export function formProblem(
  tab: PilotTab,
  fields: { name: string; password: string; confirm: string },
): string | null {
  if (!fields.name) return 'Введите логин';
  if (!fields.password) return 'Введите пароль';
  if (tab !== 'register') return null;
  // Пароль не восстановить: опечатка при заведении аккаунта теряет его насовсем.
  if (!fields.confirm) return 'Повторите пароль';
  if (fields.confirm !== fields.password) return 'Пароли не совпадают';
  return null;
}

/**
 * Подсказка под полем логина (M15.8): вкладка обещает одно, а ответ сервера про ник говорит другое.
 * Это предупреждение, пока пилот печатает, — отказать всё равно должен сервер при входе.
 */
export function nameNote(tab: PilotTab, message: NameFreeMsg | null): string {
  if (!message) return '';
  if (tab === 'login' && message.free) return 'Такого пилота нет — он заводится на вкладке «Регистрация»';
  if (tab === 'register' && !message.free) return 'Логин уже занят — под ним входят на вкладке «Вход»';
  return '';
}

export interface PilotFormHandlers {
  /** Войти или завести пилота: create — вкладка «Регистрация», и тогда же применяется путь. */
  onLogin(name: string, password: string, career: string | null, create: boolean): void;
  onLogout(): void;
  /** Спросить сервер, свободен ли ник (M15.5): от ответа зависят карточки пути и подсказка под логином. */
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
 * Окно «Пилот» (GDD §61): вкладка «Вход» пускает в свой аккаунт, «Регистрация» заводит нового пилота
 * (M15.8), и обе спрашивают адрес сервера. Пока пилот не вошёл, окно не закрывается: без аккаунта играть
 * не на чем.
 */
export class PilotForm {
  private readonly nameInput: HTMLInputElement;
  private readonly passwordInput: HTMLInputElement;
  private readonly confirmInput: HTMLInputElement;
  private readonly confirmRow: HTMLElement;
  private readonly serverInput: HTMLInputElement;
  private readonly hint: HTMLElement;
  private readonly note: HTMLElement;
  private readonly error: HTMLElement;
  private readonly cancel: HTMLButtonElement;
  private readonly logout: HTMLButtonElement;
  private readonly submit: HTMLButtonElement;
  private readonly careers: HTMLElement;
  private readonly tabs: HTMLButtonElement[];
  private state: PilotFormState = { url: null, name: '', loggedIn: false };
  private tab: PilotTab = 'login';
  /** Последний ответ сервера про ник; null — ещё не спрашивали или ответ устарел. */
  private free: NameFreeMsg | null = null;
  private chosen: string | null = null;
  private checkTimer = 0;
  /** Заставку просим один раз за сеанс: второй показ окна берёт её из кеша браузера. */
  private artAsked = false;

  constructor(
    private readonly root: HTMLElement,
    handlers: PilotFormHandlers,
  ) {
    this.nameInput = root.querySelector('input[name=name]')!;
    this.passwordInput = root.querySelector('input[name=password]')!;
    this.confirmInput = root.querySelector('input[name=confirm]')!;
    this.confirmRow = root.querySelector('.connect-confirm')!;
    this.serverInput = root.querySelector('input[name=server]')!;
    this.hint = root.querySelector('.connect-hint')!;
    this.note = root.querySelector('.connect-note')!;
    this.error = root.querySelector('.connect-error')!;
    this.cancel = root.querySelector('.connect-cancel')!;
    this.logout = root.querySelector('.connect-logout')!;
    this.submit = root.querySelector('button[type=submit]')!;
    this.careers = root.querySelector('.connect-careers')!;
    this.tabs = [...root.querySelectorAll<HTMLButtonElement>('.connect-tab')];

    for (const tab of this.tabs) {
      tab.addEventListener('click', () => this.setTab(tab.dataset.tab === 'register' ? 'register' : 'login'));
    }

    // Спрашиваем про ник не на каждую букву, а когда печатать перестали.
    this.nameInput.addEventListener('input', () => {
      this.free = null;
      this.renderCareers();
      this.renderNote();
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
      const problem = formProblem(this.tab, { name, password, confirm: this.confirmInput.value });
      if (problem) return this.setError(problem);
      this.setError('');
      this.submit.textContent = TABS[this.tab].wait;
      // Путь шлём, только когда карточки видны: значит ник свободен и аккаунт заводится сейчас.
      const cards = this.cards();
      handlers.onLogin(name, password, cards.find((c) => c.chosen)?.id ?? null, this.tab === 'register');
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

  /** Ответ сервера про ник: от него зависят карточки пути и подсказка под логином. */
  setNameFree(message: NameFreeMsg): void {
    // Ответ про прежний ник: игрок успел допечатать, и показывать его уже нельзя.
    if (message.name !== this.nameInput.value.trim()) return;
    this.free = message;
    this.renderCareers();
    this.renderNote();
  }

  show(state: PilotFormState, error = ''): void {
    // Окно уже открыто — значит это отказ сервера поверх него, и выбранную вкладку менять нельзя.
    const fresh = this.root.hidden;
    this.state = state;
    this.free = null;
    this.chosen = null;
    // Ник ставим только в свежем окне: на отказе игрок должен видеть то, что набрал, а не прежний ник.
    if (fresh) this.nameInput.value = state.name;
    this.passwordInput.value = '';
    this.serverInput.value = state.url ?? '';
    this.cancel.hidden = !state.loggedIn;
    this.logout.hidden = !state.loggedIn;
    this.setTab(fresh ? startTab(state) : this.tab);
    this.setError(error);
    // Живой сессии нет — это стартовый экран игры, а не окно поверх мира: за ним заставка, а не затемнение.
    const start = !state.loggedIn;
    this.root.classList.toggle('connect--start', start);
    if (start) this.loadStartArt();
    this.root.hidden = false;
    (state.url && !state.name ? this.nameInput : state.url ? this.passwordInput : this.serverInput).focus();
  }

  /** Вход не удался: окно остаётся на своей вкладке, ник на месте, пароль — заново. */
  showError(text: string): void {
    this.passwordInput.value = '';
    this.confirmInput.value = '';
    this.submit.textContent = TABS[this.tab].submit;
    this.setError(text);
  }

  hide(): void {
    this.root.hidden = true;
    this.passwordInput.value = '';
    this.confirmInput.value = '';
    // Фокус в поле ввода глушит клавиши управления (keyboard.ts).
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  }

  /**
   * Сменить вкладку: пароль остаётся набранным — с «Такого пилота нет» игрок уходит регистрироваться
   * с тем же паролем, и заново его печатать незачем.
   */
  private setTab(tab: PilotTab): void {
    this.tab = tab;
    for (const button of this.tabs) button.setAttribute('aria-pressed', String(button.dataset.tab === tab));
    this.hint.textContent = TABS[tab].hint;
    this.submit.textContent = TABS[tab].submit;
    this.confirmRow.hidden = tab !== 'register';
    this.confirmInput.value = '';
    // Менеджеру паролей важно, какое перед ним поле: прежний пароль или придуманный сейчас.
    this.passwordInput.autocomplete = tab === 'register' ? 'new-password' : 'current-password';
    this.setError('');
    this.renderCareers();
    this.renderNote();
  }

  private setError(text: string): void {
    this.error.textContent = text;
  }

  /**
   * Заставка стартового экрана. Ставим её на фон только готовой: иначе она проступает полосами
   * поверх градиента. Картинки нет или сеть молчит — остаётся градиент, и экран рабочий.
   */
  private loadStartArt(): void {
    if (this.artAsked) return;
    this.artAsked = true;
    // Относительный url() в стиле браузер отсчитывал бы от файла стилей, а не от страницы, — как у сцен дока.
    const url = new URL(START_ART, document.baseURI).href;
    const image = new Image();
    image.decoding = 'async';
    image.src = url;
    const show = (): void => {
      this.root.style.setProperty('--connect-art', `url("${url}")`);
      this.root.dataset.art = 'ready';
    };
    // onload ставится всегда, а не только вместо decode(): decode() умеет отказать на совершенно целой
    // картинке (вкладка в фоне, headless-браузер), и тогда заставки не было бы вовсе. Показать дважды
    // безопасно — show() идемпотентен.
    image.onload = show;
    if (typeof image.decode === 'function') void image.decode().then(show, () => {});
  }

  /** Путь выбирают только при заведении аккаунта — значит на вкладке «Регистрация» и со свободным ником. */
  private cards(): CareerCard[] {
    return careerCards(this.tab === 'register' ? this.free : null, this.chosen);
  }

  private renderNote(): void {
    this.note.textContent = nameNote(this.tab, this.free);
  }

  private renderCareers(): void {
    const cards = this.cards();
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
