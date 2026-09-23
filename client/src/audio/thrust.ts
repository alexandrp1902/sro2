import type { SfxBank } from './bank';
import type { AudioEngine } from './engine';

/**
 * Гул двигателя (M17b): одна петля «engine» из банка крутится всё время, а газ управляет её громкостью
 * и высотой. Полный газ — гул громче и выше, отпустили — стихает за доли секунды, но не обрывается.
 *
 * Идёт по шине звуков, а не музыки: это шум корабля, и ползунок «Звуки» должен его глушить.
 */

const GAIN = 0.7;
const RATE_IDLE = 0.8;
const RATE_FULL = 1.25;

export class Thrust {
  private source: AudioBufferSourceNode | null = null;
  private gain: GainNode | null = null;

  constructor(
    private readonly engine: AudioEngine,
    private readonly bank: SfxBank,
  ) {}

  /** Раз в кадр. @param throttle газ 0..1; @param muted в доке и после гибели двигатель молчит */
  set(throttle: number, muted: boolean): void {
    const level = muted ? 0 : Math.min(1, Math.max(0, throttle));
    if (level > 0 && !this.source) this.start();
    if (!this.source || !this.gain) return;
    this.engine.ramp(this.gain, GAIN * level ** 0.6, 0.12);
    const t = this.engine.ctx.currentTime;
    this.source.playbackRate.cancelScheduledValues(t);
    this.source.playbackRate.setTargetAtTime(RATE_IDLE + (RATE_FULL - RATE_IDLE) * level, t, 0.15);
  }

  /** Прыжок, обрыв связи: петля останавливается; при следующем газе заведётся заново. */
  stop(): void {
    try {
      this.source?.stop();
    } catch {
      // уже остановлен
    }
    this.source = null;
    this.gain = null;
  }

  private start(): void {
    const buffer = this.bank.pick('engine', 0);
    if (!buffer) return; // банк ещё качается — попробуем в следующем кадре
    const ctx = this.engine.ctx;
    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.loop = true;
    const gain = ctx.createGain();
    gain.gain.value = 0;
    source.connect(gain).connect(this.engine.sfxBus);
    source.start();
    this.source = source;
    this.gain = gain;
  }
}
