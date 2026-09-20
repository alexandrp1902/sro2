/**
 * Анимация снижения (M15): между «нажал ПОСАДКА» и экраном поселения — короткий проход сквозь атмосферу.
 * Стыковка со станцией такого не получает: там корабль просто входит в шлюз, а посадка — это спуск.
 *
 * Кадры нарисованы для всех видов планет, кроме кольчатой (M15.6): для неё анимация идёт без кадра —
 * затемнение и проявление сцены, — и это лучше, чем показывать чужой биом.
 */

/** Сколько длится проход целиком: дольше — и это начинает раздражать на второй посадке. */
const TOTAL_MS = 1400;

/** Столько в конце занимает растворение: экран поселения проступает из-под кадра. */
const FADE_MS = 500;

/**
 * Вид планеты (shared/galaxy.json) → имя кадра снижения. Почти везде имя совпадает с видом, но у газового
 * гиганта нет: на него не садятся, садятся на орбитальную платформу над ним, и кадр у него её.
 * Кольчатой планеты (ringed) здесь нет — кадра для неё не нарисовали.
 */
const LANDING_ART: Record<string, string> = {
  terran: 'terran',
  desert: 'desert',
  ice: 'ice',
  jungle: 'jungle',
  lava: 'lava',
  barren: 'barren',
  ocean: 'ocean',
  toxic: 'toxic',
  gas: 'orbital-platform',
};

/** Адрес кадра снижения; null — для этого вида его не нарисовали. */
export function landingArt(kind: string | null | undefined): string | null {
  const art = kind ? LANDING_ART[kind] : undefined;
  return art ? `dock/landing-${art}.webp` : null;
}

/**
 * Полноэкранная заставка снижения. Живёт сама: показали — через TOTAL_MS исчезнет.
 * Повторный вызов обрывает предыдущую: две посадки подряд не должны накладываться.
 */
export class Landing {
  private readonly root: HTMLElement;
  private timer = 0;

  constructor(parent: HTMLElement = document.body) {
    this.root = document.createElement('div');
    this.root.className = 'landing';
    this.root.hidden = true;
    this.root.setAttribute('aria-hidden', 'true');
    parent.append(this.root);
  }

  /** Показать снижение на планету этого вида. */
  show(kind: string | null | undefined): void {
    this.stop();
    const art = landingArt(kind);
    // Относительный url() в стиле браузер отсчитывал бы от файла стилей, а не от страницы, — как у сцен дока.
    this.root.style.setProperty('--landing-art', art ? `url("${new URL(art, document.baseURI).href}")` : 'none');
    this.root.hidden = false;
    // Перезапуск анимации: без этого второй показ подряд не проигрывается заново.
    this.root.classList.remove('landing-run');
    void this.root.offsetWidth;
    this.root.classList.add('landing-run');
    this.timer = window.setTimeout(() => this.stop(), TOTAL_MS);
  }

  /** Убрать заставку немедленно: вылет, обрыв связи, смена экрана. */
  stop(): void {
    if (this.timer) window.clearTimeout(this.timer);
    this.timer = 0;
    this.root.hidden = true;
    this.root.classList.remove('landing-run');
  }

  /** Сколько миллисекунд идёт растворение — здесь же, чтобы стиль и код не разъезжались. */
  static get fadeMs(): number {
    return FADE_MS;
  }

  static get totalMs(): number {
    return TOTAL_MS;
  }
}
