/**
 * Анимация снижения (M15): между «нажал ПОСАДКА» и экраном поселения — короткий проход сквозь атмосферу.
 * Стыковка со станцией такого не получает: там корабль просто входит в шлюз, а посадка — это спуск.
 *
 * Кадры нарисованы для всех видов планет, кроме кольчатой (M15.6): на неё садятся сразу, без заставки.
 * Показывать вместо кадра пустой экран — только задержка на ровном месте (M16a).
 */

/**
 * Сколько длится проход целиком. Три секунды — столько, чтобы кадр успели рассмотреть (M16a):
 * за прежние 1.4 с он не успевал прочитаться. Это же число уходит в стиль переменной --landing-ms,
 * чтобы длительность жила в одном месте.
 */
const TOTAL_MS = 3000;

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

  /** Показать снижение на планету этого вида; без кадра не показывает ничего. */
  show(kind: string | null | undefined): void {
    this.stop();
    const art = landingArt(kind);
    // Кадра для этого вида нет — сразу открываем поселение. Держать три секунды пустой чёрный
    // экран незачем: искусственной задержки там, где смотреть не на что, быть не должно (M16a).
    if (!art) return;
    // Относительный url() в стиле браузер отсчитывал бы от файла стилей, а не от страницы, — как у сцен дока.
    this.root.style.setProperty('--landing-art', `url("${new URL(art, document.baseURI).href}")`);
    this.root.style.setProperty('--landing-ms', `${TOTAL_MS}ms`);
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
