import type { SfxCue } from './cues';

/**
 * Где звук слышно и сколько звуков разрешено разом. Чистый модуль: ни одного обращения к Web Audio,
 * поэтому правила проверяются тестами без браузера.
 */

export interface Place {
  x: number;
  y: number;
}

/** Слушатель — это камера: игрок слышит оттуда, откуда смотрит. */
export interface Listener extends Place {
  zoom: number;
  /** Половина ширины окна в пикселях: по ней считается, где звук «у края экрана». */
  halfWidth: number;
}

export interface Spatial {
  pan: number;
  gain: number;
}

/** Дальше этого не слышно ничего. Чуть шире радара (2000), чтобы бой за краем экрана доносился глухо. */
export const AUDIBLE = 2600;

/** Панорама не доходит до краёв: в наушниках звук, упёртый в −1, «выпадает» из головы. */
const PAN_LIMIT = 0.8;

const clamp = (v: number, lo: number, hi: number) => (v < lo ? lo : v > hi ? hi : v);

/**
 * Панорама и громкость звука, прозвучавшего в точке мира.
 *
 * Громкость падает по степени, а не по прямой: линейное затухание оставляет далёкие выстрелы
 * неправдоподобно громкими почти до самой границы слышимости.
 */
export function spatial(at: Place, listener: Listener): Spatial {
  const dx = at.x - listener.x;
  const dy = at.y - listener.y;
  const distance = Math.hypot(dx, dy);
  const gain = clamp(1 - distance / AUDIBLE, 0, 1) ** 1.6;
  const pan = clamp((dx * listener.zoom) / Math.max(listener.halfWidth, 1), -1, 1) * PAN_LIMIT;
  return { pan, gain };
}

/** Тише этого звук не создаётся вовсе: узлы графа стоят дороже, чем тишина, которую никто не услышит. */
export const HEARABLE = 0.02;

/** Сколько звуков может звучать разом. Каждый — три узла графа, на телефоне это уже заметно. */
const MAX_VOICES = 16;
/**
 * Сколько звуков разом от одного корабля. Залп из шести пушек — это шесть выстрелов в один тик,
 * и без ограничения он превращается в кашу. Второй выстрел залпа играется тише первого: два одинаковых
 * звука в унисон дают не «мощнее», а «громче вдвое».
 */
const PER_SOURCE = 2;
const SECOND_GAIN = 0.75;
/** Окно, внутри которого выстрелы считаются одним залпом. */
const VOLLEY_MS = 70;

/** Реже этого один и тот же звук не повторяется, мс. Приём тот же, что у строки о защите в ленте. */
const GAP: Record<string, number> = { shot: 45, hit: 60, explode: 90, whizz: 200, loot: 120, block: 60 };
const DEFAULT_GAP = 40;

/**
 * Звуки не ниже этой важности антиспам не глушит: свой выстрел и попадание по мне — боевая информация,
 * и потерять её из-за того, что кто-то рядом выстрелил тем же стволом на сорок миллисекунд раньше, нельзя.
 */
const NEVER_MUTED = 60;

function gapOf(cue: SfxCue): number {
  const head = cue.split('-')[0];
  return GAP[head] ?? GAP[cue] ?? DEFAULT_GAP;
}

interface Active {
  id: number;
  source: number;
  priority: number;
  startedAt: number;
  until: number;
}

/** Разрешение на звук: чем его играть и какой уже играющий голос при этом оборвать. */
export interface Grant {
  id: number;
  gain: number;
  /** Голос, который вытеснен этим звуком; null — никого вытеснять не пришлось. */
  stop: number | null;
}

/**
 * Кто сейчас звучит и кого пускать дальше. Держит три правила: общий потолок голосов, потолок на корабль
 * и минимальный промежуток между одинаковыми звуками.
 */
export class SfxBudget {
  private readonly active: Active[] = [];
  private readonly lastAt = new Map<string, number>();
  private nextId = 1;

  /**
   * @param ms сколько звук будет играть — по нему голос освобождается сам, даже если о нём забыли
   * @returns null — звук отбрасывается, узлы графа под него не создаются
   */
  allow(cue: SfxCue, source: number, priority: number, ms: number, now: number): Grant | null {
    this.prune(now);

    const last = this.lastAt.get(cue);
    if (priority < NEVER_MUTED && last !== undefined && now - last < gapOf(cue)) return null;

    let fromSource = 0;
    for (const voice of this.active) if (voice.source === source && now - voice.startedAt < VOLLEY_MS) fromSource++;
    if (source !== 0 && fromSource >= PER_SOURCE) return null;

    let stop: number | null = null;
    if (this.active.length >= MAX_VOICES) {
      let weakest = this.active[0];
      for (const voice of this.active) if (voice.priority < weakest.priority) weakest = voice;
      if (weakest.priority >= priority) return null;
      stop = weakest.id;
      this.release(weakest.id);
    }

    const id = this.nextId++;
    this.active.push({ id, source, priority, startedAt: now, until: now + ms });
    this.lastAt.set(cue, now);
    return { id, gain: fromSource > 0 ? SECOND_GAIN : 1, stop };
  }

  /** Звук доиграл (или его оборвали) — голос свободен. */
  release(id: number): void {
    const at = this.active.findIndex((voice) => voice.id === id);
    if (at >= 0) this.active.splice(at, 1);
  }

  /** Прыжок, обрыв связи, смерть: всё смолкло, и повторам больше ничего не мешает. */
  clear(): void {
    this.active.length = 0;
    this.lastAt.clear();
  }

  get playing(): number {
    return this.active.length;
  }

  private prune(now: number): void {
    for (let i = this.active.length - 1; i >= 0; i--) if (this.active[i].until <= now) this.active.splice(i, 1);
  }
}
