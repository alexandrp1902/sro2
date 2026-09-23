import type { AudioEngine } from './engine';
import type { Mood } from './mood';
import meta from './musicMeta.json';

/**
 * Саундтрек. Восемь слоёв (tools/music.py) одной длины запускаются разом и крутятся по кругу; меняется
 * только громкость каждого. Поэтому «бой начался» — это не склейка и не пауза, а полсекунды на кроссфейд.
 *
 * Слои зациклены средствами Web Audio: буфер у всех одинаковой длины, все стартуют в одну и ту же секунду,
 * и разойтись они не могут, сколько бы игра ни шла.
 */

type StemName = keyof typeof meta.stems;

/**
 * Громкость каждого слоя по настроению. Это решение микшера, а не запись, поэтому живёт здесь,
 * а не в манифесте: правится без перерисовки музыки.
 *
 * Дрон не смолкает нигде, кроме гибели, — он держит тональность, и переходы всегда имеют опору.
 * Пэд покоя остаётся и в бою: без него бой звучит как отдельный трек, а не как та же музыка, но злее.
 */
const MIX: Record<StemName, Record<Mood, number>> = {
  'calm-drone': { calm: 0.9, combat: 0.5, dock: 0.35, dead: 0.18 },
  'calm-pad': { calm: 0.85, combat: 0.3, dock: 0, dead: 0 },
  'calm-arp': { calm: 0.4, combat: 0.15, dock: 0, dead: 0 },
  'combat-bass': { calm: 0, combat: 0.9, dock: 0, dead: 0 },
  'combat-drums': { calm: 0, combat: 1, dock: 0, dead: 0 },
  'combat-lead': { calm: 0, combat: 0.7, dock: 0, dead: 0 },
  'combat-braam': { calm: 0, combat: 0.8, dock: 0, dead: 0 },
  'dock-pad': { calm: 0, combat: 0, dock: 0.9, dead: 0.3 },
};

/**
 * Сколько длится переход. В бой — быстро, из боя — медленно: тревога должна приходить сразу,
 * а отпускать должно постепенно, иначе кажется, что музыка «выключилась».
 */
const FADE: Record<Mood, number> = { combat: 0.6, calm: 2.5, dock: 1.2, dead: 0.8 };

const stemUrl = (name: string) => `music/${name}.mp3`;

export class Music {
  private readonly buffers = new Map<string, AudioBuffer>();
  private readonly layers = new Map<string, GainNode>();
  private loading: Promise<void> | null = null;
  private mood: Mood = 'calm';
  private playing = false;

  constructor(private readonly engine: AudioEngine) {}

  /** Скачать слои. Музыка тяжелее звуков, поэтому грузится отдельно и позже — бой важнее. */
  load(): Promise<void> {
    this.loading ??= this.loadAll();
    return this.loading;
  }

  /** Запустить круг. Раньше первого жеста бесполезно: браузер не даст звука. */
  start(): void {
    if (this.playing || this.buffers.size === 0) return;
    this.playing = true;
    const ctx = this.engine.ctx;
    const at = ctx.currentTime + 0.12;
    for (const [name, buffer] of this.buffers) {
      const source = ctx.createBufferSource();
      source.buffer = buffer;
      source.loop = true;
      const gain = ctx.createGain();
      gain.gain.value = 0;
      source.connect(gain).connect(this.engine.musicBus);
      source.start(at);
      this.layers.set(name, gain);
    }
    this.apply(this.mood, 1.5);
  }

  setMood(mood: Mood): void {
    if (mood === this.mood) return;
    this.mood = mood;
    this.apply(mood, FADE[mood]);
  }

  get current(): Mood {
    return this.mood;
  }

  private apply(mood: Mood, seconds: number): void {
    for (const [name, gain] of this.layers) {
      const level = MIX[name as StemName]?.[mood] ?? 0;
      this.engine.ramp(gain, level, seconds);
    }
  }

  private async loadAll(): Promise<void> {
    await Promise.all(
      Object.keys(meta.stems).map((name) =>
        fetch(stemUrl(name))
          .then((response) => response.arrayBuffer())
          .then((bytes) => this.engine.ctx.decodeAudioData(bytes))
          .then((buffer) => void this.buffers.set(name, buffer))
          .catch(() => {
            // Слой не скачался — остальные всё равно звучат.
          }),
      ),
    );
  }
}
