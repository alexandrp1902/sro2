import type { AudioPrefs } from './settings';

/**
 * Звуковой движок: контекст, шины и громкости. Всё, что знает про Web Audio, начинается здесь.
 *
 * Контекст создаётся сразу, а не по жесту: без жеста он просто спит, но уже позволяет декодировать
 * буферы — к первому выстрелу банк готов. Разбудить его обязан жест пользователя (unlock), иначе
 * браузер не даст звука.
 *
 *   источники → [панорама → громкость] ─┐
 *   слои музыки ─────────────────────── ├→ master → ограничитель → выход
 *   голоса эфира ───────────────────────┘
 *
 * Ограничитель в конце — страховка: взрыв в упор поверх залпа иначе уходит в клиппинг.
 */
export class AudioEngine {
  readonly ctx: AudioContext;
  readonly master: GainNode;
  readonly sfxBus: GainNode;
  readonly musicBus: GainNode;
  readonly radioBus: GainNode;

  private prefs: AudioPrefs;
  private hidden = false;
  /** Контекст усыпили мы сами (звук выключили или вкладка ушла в фон) — только такой и будим обратно. */
  private slept = false;
  /** Музыку и эфир приглушают на время реплики: множитель поверх ползунка. */
  private musicDuck = 1;
  private sfxDuck = 1;

  constructor(prefs: AudioPrefs) {
    this.prefs = prefs;
    const Ctor = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    this.ctx = new Ctor({ latencyHint: 'interactive' });

    const limiter = this.ctx.createDynamicsCompressor();
    limiter.threshold.value = -12;
    limiter.knee.value = 6;
    limiter.ratio.value = 6;
    limiter.attack.value = 0.003;
    limiter.release.value = 0.12;
    limiter.connect(this.ctx.destination);

    this.master = this.ctx.createGain();
    this.master.connect(limiter);
    this.sfxBus = this.ctx.createGain();
    this.musicBus = this.ctx.createGain();
    this.radioBus = this.ctx.createGain();
    for (const bus of [this.sfxBus, this.musicBus, this.radioBus]) bus.connect(this.master);
    this.applyPrefs(prefs);

    // Вкладка в фоне: гасим и усыпляем контекст — иначе музыка играет в кармане и ест батарею.
    document.addEventListener('visibilitychange', () => {
      this.hidden = document.hidden;
      this.applyPrefs(this.prefs);
      if (document.hidden) {
        this.slept = true;
        window.setTimeout(() => void (document.hidden && this.ctx.suspend()), 400);
      } else if (this.slept && this.prefs.master > 0) {
        this.slept = false;
        void this.ctx.resume();
      }
    });
  }

  /** Жест пользователя: вход в игру, тап по стику, нажатие клавиши. Идемпотентно. */
  unlock(): void {
    if (this.ctx.state !== 'running' && this.prefs.master > 0) {
      this.slept = false;
      void this.ctx.resume();
    }
  }

  applyPrefs(prefs: AudioPrefs): void {
    this.prefs = prefs;
    const silent = this.hidden || prefs.master <= 0;
    this.ramp(this.master, silent ? 0 : prefs.master, 0.15);
    this.ramp(this.sfxBus, prefs.sfx * this.sfxDuck, 0.05);
    this.ramp(this.musicBus, prefs.music * this.musicDuck, 0.15);
    this.ramp(this.radioBus, prefs.radio, 0.05);
    // Полностью выключенный звук усыпляет контекст: незачем крутить граф впустую. Будим только то,
    // что усыпили сами: контекст, ни разу не разбуженный жестом, будить бесполезно — браузер не даст.
    if (prefs.master <= 0 && this.ctx.state === 'running') {
      this.slept = true;
      void this.ctx.suspend();
    } else if (prefs.master > 0 && !this.hidden && this.slept) {
      this.slept = false;
      void this.ctx.resume();
    }
  }

  /** Приглушить музыку и звуки на время реплики эфира (M17b). */
  duck(music: number, sfx: number, seconds: number): void {
    this.musicDuck = music;
    this.sfxDuck = sfx;
    this.ramp(this.musicBus, this.prefs.music * music, seconds);
    this.ramp(this.sfxBus, this.prefs.sfx * sfx, seconds);
  }

  ramp(node: GainNode, to: number, seconds: number): void {
    const t = this.ctx.currentTime;
    node.gain.cancelScheduledValues(t);
    node.gain.setValueAtTime(node.gain.value, t);
    // Линейная рампа, а не экспоненциальная: экспоненте нельзя давать ноль, а ноль здесь обычное дело.
    node.gain.linearRampToValueAtTime(Math.max(0, to), t + Math.max(0.01, seconds));
  }
}
