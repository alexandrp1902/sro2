import type { Line } from './chatter';
import type { AudioEngine } from './engine';
import type { Sfx } from './sfx';
import meta from './radioMeta.json';

/**
 * Проигрывание реплик эфира (M17b): щелчок рации → приглушение музыки и звуков → голос, панорамированный
 * по положению корабля, → щелчок отбоя → возврат громкости.
 *
 * Файлы реплик качаются лениво и по одному: субтитр в ленте появляется сразу, голос — как скачается.
 * Если скачивание тянется дольше, чем живёт субтитр, голос не играется вовсе: реплика без контекста хуже тишины.
 * Говорит всегда один; новая реплика с большим приоритетом обрывает текущую, с меньшим — теряется.
 */

const DUCK_MUSIC = 0.3;
// Звуки боя на время реплики — вполовину: иначе залп из шести пушек съедает согласные.
const DUCK_SFX = 0.5;
const LATE_MS = 2500;
/** Голос слышен отовсюду одинаково — это рация, а не крик через космос; панорама только намекает, откуда. */
const PAN_SCALE = 0.6;

const lineUrl = (id: string) => `radio/${id}.mp3`;

interface Speaking {
  token: number;
  priority: number;
  /** Чей это голос: погиб — реплика обрывается на полуслове. */
  shipId: number;
  source: AudioBufferSourceNode | null;
}

export class Radio {
  private readonly buffers = new Map<string, Promise<AudioBuffer | null>>();
  private current: Speaking | null = null;
  private token = 0;

  constructor(
    private readonly engine: AudioEngine,
    private readonly sfx: Sfx | null,
  ) {}

  /** Есть ли у реплики файл: манифест — единственное, чему верим. */
  static has(id: string): boolean {
    return id in meta.lines;
  }

  /** @returns взялась ли реплика в эфир (иначе — сейчас говорят важнее) */
  say(line: Line, pan: number): boolean {
    if (this.current && this.current.priority >= line.priority) return false;
    this.stop();
    const token = ++this.token;
    const speaking: Speaking = { token, priority: line.priority, shipId: line.shipId, source: null };
    this.current = speaking;
    const started = performance.now();
    this.click('squelch-open');
    this.engine.duck(DUCK_MUSIC, DUCK_SFX, 0.15);

    void this.load(line.id).then((buffer) => {
      if (this.current !== speaking) return; // перебили, пока качалось
      if (!buffer || performance.now() - started > LATE_MS) {
        this.finish(speaking);
        return;
      }
      const ctx = this.engine.ctx;
      const source = ctx.createBufferSource();
      source.buffer = buffer;
      const panner = ctx.createStereoPanner();
      panner.pan.value = Math.max(-1, Math.min(1, pan * PAN_SCALE));
      source.connect(panner).connect(this.engine.radioBus);
      source.onended = () => {
        if (this.current === speaking) this.finish(speaking);
      };
      speaking.source = source;
      source.start();
    });
    return true;
  }

  /**
   * Говоривший уничтожен: реплика обрывается на полуслове, со щелчком отбоя. Пират, который грозит
   * из уже разлетевшегося корабля, — то, что ломает всю иллюзию эфира.
   */
  cutOff(shipId: number): void {
    const speaking = this.current;
    if (!speaking || speaking.shipId !== shipId) return;
    this.stop();
    this.click('squelch-close');
    this.engine.duck(1, 1, 0.4);
  }

  /** Прыжок, обрыв связи, гибель: эфир смолкает без щелчка отбоя. */
  stopAll(): void {
    if (!this.current) return;
    this.stop();
    this.engine.duck(1, 1, 0.3);
  }

  private stop(): void {
    const speaking = this.current;
    if (!speaking) return;
    this.current = null;
    try {
      speaking.source?.stop();
    } catch {
      // уже кончился
    }
  }

  private finish(speaking: Speaking): void {
    if (this.current !== speaking) return;
    this.current = null;
    this.click('squelch-close');
    this.engine.duck(1, 1, 0.6);
  }

  private click(cue: 'squelch-open' | 'squelch-close'): void {
    this.sfx?.play({ cue, rate: 1, gain: 1, priority: 90 }, null, 0, performance.now());
  }

  private load(id: string): Promise<AudioBuffer | null> {
    let pending = this.buffers.get(id);
    if (!pending) {
      pending = Radio.has(id)
        ? fetch(lineUrl(id))
            .then((response) => response.arrayBuffer())
            .then((bytes) => this.engine.ctx.decodeAudioData(bytes))
            .catch(() => null)
        : Promise.resolve(null);
      this.buffers.set(id, pending);
    }
    return pending;
  }
}
