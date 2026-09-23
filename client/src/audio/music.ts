import type { AudioEngine } from './engine';
import type { Mood } from './mood';
import type { CombatTheme } from './settings';
import meta from './musicMeta.json';

/**
 * Саундтрек. Слои (tools/music.py) одной длины запускаются разом и крутятся по кругу; меняется
 * только громкость каждого. Поэтому «бой начался» — это не склейка и не пауза, а полсекунды на кроссфейд.
 *
 * Слои зациклены средствами Web Audio: буфер у всех одинаковой длины, все стартуют в одну и ту же секунду,
 * и разойтись они не могут, сколько бы игра ни шла.
 *
 * Боевых тем три (манифест, themes), и качается только выбранная: четыре лишних слоя — это пять мегабайт
 * распакованного звука на телефоне. При смене темы новые слои встают в круг с того же места, где сейчас
 * старые, — по времени старта петли, а не «с начала».
 */

type StemName = keyof typeof meta.stems;

/** Слои, которые звучат при любой боевой теме: покой и док. */
const SHARED: StemName[] = (Object.keys(meta.stems) as StemName[]).filter(
  (name) => !Object.values(meta.themes).some((names) => (names as string[]).includes(name)),
);

/**
 * Громкость каждого слоя по настроению. Это решение микшера, а не запись, поэтому живёт здесь,
 * а не в манифесте: правится без перерисовки музыки. Боевые громкости продублированы в CHECK_MIX
 * в tools/music.py — там по ним считается спектр смеси при приёмке.
 *
 * Дрон не смолкает нигде, кроме гибели, — он держит тональность, и переходы всегда имеют опору.
 * В бою он и пэд почти убраны: у боевых тем своё основание (струнные, бас, рифф), и дрон поверх
 * него только мажет.
 */
const MIX: Record<StemName, Record<Mood, number>> = {
  'calm-drone': { calm: 0.9, combat: 0.3, dock: 0.35, dead: 0.18 },
  'calm-pad': { calm: 0.85, combat: 0.12, dock: 0, dead: 0 },
  // В бою арпеджио молчит: звонкий щипок поверх ударных и есть тот самый «аркадный» призвук.
  'calm-arp': { calm: 0.4, combat: 0, dock: 0, dead: 0 },
  'taiko-drums': { calm: 0, combat: 1, dock: 0, dead: 0 },
  'taiko-strings': { calm: 0, combat: 0.8, dock: 0, dead: 0 },
  // Флейта и смычковый лид выключены по итогам прослушивания: самый высокий голос темы раздражал.
  // Файлы на месте — вернуть можно одной цифрой.
  'taiko-flute': { calm: 0, combat: 0, dock: 0, dead: 0 },
  'taiko-hits': { calm: 0, combat: 0.7, dock: 0, dead: 0 },
  'chase-drums': { calm: 0, combat: 1, dock: 0, dead: 0 },
  'chase-bass': { calm: 0, combat: 0.75, dock: 0, dead: 0 },
  'chase-strings': { calm: 0, combat: 0.8, dock: 0, dead: 0 },
  'chase-brass': { calm: 0, combat: 0.8, dock: 0, dead: 0 },
  'duel-drums': { calm: 0, combat: 1, dock: 0, dead: 0 },
  'duel-riff': { calm: 0, combat: 0.9, dock: 0, dead: 0 },
  'duel-choir': { calm: 0, combat: 0.6, dock: 0, dead: 0 },
  'duel-lead': { calm: 0, combat: 0, dock: 0, dead: 0 },
  'dock-pad': { calm: 0, combat: 0, dock: 0.9, dead: 0.3 },
};

/**
 * Сколько длится переход. В бой — быстро, из боя — медленно: тревога должна приходить сразу,
 * а отпускать должно постепенно, иначе кажется, что музыка «выключилась».
 */
const FADE: Record<Mood, number> = { combat: 0.6, calm: 2.5, dock: 1.2, dead: 0.8 };

/** Смена боевой темы на ходу: старые слои уходят, новые входят за это время. */
const THEME_FADE = 0.8;

const stemUrl = (name: string) => `music/${name}.mp3`;

interface Layer {
  source: AudioBufferSourceNode;
  gain: GainNode;
}

export class Music {
  private readonly buffers = new Map<string, AudioBuffer>();
  private readonly loading = new Map<string, Promise<void>>();
  private readonly layers = new Map<string, Layer>();
  private mood: Mood = 'calm';
  /** Когда стартовала петля по часам контекста; null — ещё не играем. */
  private startedAt: number | null = null;

  constructor(
    private readonly engine: AudioEngine,
    private theme: CombatTheme,
  ) {}

  /** Скачать слои текущей темы. Музыка тяжелее звуков, поэтому грузится отдельно и позже — бой важнее. */
  load(): Promise<void> {
    return Promise.all(this.wanted().map((name) => this.fetch(name))).then(() => undefined);
  }

  /** Запустить круг. Раньше первого жеста бесполезно: браузер не даст звука. */
  start(): void {
    if (this.startedAt !== null || this.buffers.size === 0) return;
    this.startedAt = this.engine.ctx.currentTime + 0.12;
    for (const name of this.wanted()) this.play(name, this.startedAt, 0);
    this.apply(this.mood, 1.5);
  }

  setMood(mood: Mood): void {
    if (mood === this.mood) return;
    this.mood = mood;
    this.apply(mood, FADE[mood]);
  }

  /**
   * Сменить боевую тему. Старые слои гаснут и останавливаются, новые скачиваются и входят в круг
   * с текущего места петли: игрок слышит переход как кроссфейд, а не как перезапуск музыки.
   */
  setTheme(theme: CombatTheme): void {
    if (theme === this.theme) return;
    const outgoing = meta.themes[this.theme] as string[];
    this.theme = theme;
    for (const name of outgoing) {
      const layer = this.layers.get(name);
      if (layer) {
        this.engine.ramp(layer.gain, 0, THEME_FADE);
        layer.source.stop(this.engine.ctx.currentTime + THEME_FADE + 0.1);
        this.layers.delete(name);
      }
      this.buffers.delete(name);
      this.loading.delete(name);
    }
    if (this.startedAt === null) return; // ещё не играем — новая тема загрузится обычным путём
    void this.load().then(() => {
      if (this.startedAt === null || this.theme !== theme) return;
      const at = this.engine.ctx.currentTime + 0.05;
      const loop = meta.loopSeconds;
      const offset = (((at - this.startedAt) % loop) + loop) % loop;
      for (const name of meta.themes[theme] as string[]) {
        if (!this.layers.has(name)) this.play(name, at, offset);
      }
      this.apply(this.mood, THEME_FADE);
    });
  }

  get current(): Mood {
    return this.mood;
  }

  private wanted(): StemName[] {
    return [...SHARED, ...(meta.themes[this.theme] as StemName[])];
  }

  private play(name: string, at: number, offset: number): void {
    const buffer = this.buffers.get(name);
    if (!buffer) return;
    const ctx = this.engine.ctx;
    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.loop = true;
    const gain = ctx.createGain();
    gain.gain.value = 0;
    source.connect(gain).connect(this.engine.musicBus);
    source.start(at, offset);
    this.layers.set(name, { source, gain });
  }

  private apply(mood: Mood, seconds: number): void {
    for (const [name, layer] of this.layers) {
      const level = MIX[name as StemName]?.[mood] ?? 0;
      this.engine.ramp(layer.gain, level, seconds);
    }
  }

  private fetch(name: string): Promise<void> {
    if (this.buffers.has(name)) return Promise.resolve();
    let pending = this.loading.get(name);
    if (!pending) {
      pending = fetch(stemUrl(name))
        .then((response) => response.arrayBuffer())
        .then((bytes) => this.engine.ctx.decodeAudioData(bytes))
        .then((buffer) => void this.buffers.set(name, buffer))
        .catch(() => {
          // Слой не скачался — остальные всё равно звучат.
        });
      this.loading.set(name, pending);
    }
    return pending;
  }
}
