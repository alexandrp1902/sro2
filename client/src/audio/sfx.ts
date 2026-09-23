import type { SfxBank } from './bank';
import { cueGain, cueLength, type SfxCue, type Voice } from './cues';
import type { AudioEngine } from './engine';
import { HEARABLE, SfxBudget, type Spatial } from './mixer';

/** Насколько случайно сдвигается высота каждого повтора: без этого очередь звучит как заевшая пластинка. */
const JITTER = 0.06;

/** Проигрыватель звуков: собирает три узла на голос и снимает их, как только звук доиграл. */
export class Sfx {
  private readonly playing = new Map<number, AudioBufferSourceNode>();

  constructor(
    private readonly engine: AudioEngine,
    private readonly bank: SfxBank,
    private readonly budget = new SfxBudget(),
  ) {}

  /**
   * @param place где это прозвучало; null — звук «в рубке» (тревога, стыковка, щелчок тумблера)
   * @param source чей это звук: по нему ограничивается залп одного корабля. 0 — ничей
   * @returns голос, который можно оборвать (гул прыжка); null — звук не прозвучал
   */
  play(voice: Voice, place: Spatial | null, source: number, now: number): number | null {
    const gain = cueGain(voice.cue) * voice.gain * (place?.gain ?? 1);
    if (gain < HEARABLE) return null;

    const rate = voice.rate * (1 + (Math.random() - 0.5) * JITTER);
    const ms = cueLength(voice.cue) / Math.max(rate, 0.2);
    const grant = this.budget.allow(voice.cue, source, voice.priority, ms, now);
    if (!grant) return null;

    const buffer = this.bank.pick(voice.cue);
    if (!buffer) {
      this.budget.release(grant.id);
      return null;
    }
    if (grant.stop !== null) this.stop(grant.stop);

    const ctx = this.engine.ctx;
    const node = ctx.createBufferSource();
    node.buffer = buffer;
    node.playbackRate.value = rate;

    const level = ctx.createGain();
    level.gain.value = gain * grant.gain;

    if (place) {
      const panner = ctx.createStereoPanner();
      panner.pan.value = place.pan;
      node.connect(panner).connect(level);
    } else {
      node.connect(level);
    }
    level.connect(this.engine.sfxBus);

    this.playing.set(grant.id, node);
    node.onended = () => {
      this.playing.delete(grant.id);
      this.budget.release(grant.id);
      level.disconnect();
    };
    node.start();
    return grant.id;
  }

  /** Сыграть без мира и без бюджета: проверка громкости в настройках. */
  preview(cue: SfxCue): void {
    this.play({ cue, rate: 1, gain: 1, priority: 1000 }, null, 0, performance.now());
  }

  /** Прыжок, обрыв связи, смерть: всё смолкает разом. */
  stopAll(): void {
    for (const id of [...this.playing.keys()]) this.stop(id);
    this.budget.clear();
  }

  /** Оборвать конкретный голос: так гаснет гул прыжка, когда прыжок сорвали. */
  stop(id: number): void {
    const node = this.playing.get(id);
    if (!node) return;
    this.playing.delete(id);
    node.onended = null;
    try {
      node.stop();
    } catch {
      // уже остановлен
    }
    this.budget.release(id);
  }
}
