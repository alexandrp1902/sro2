import meta from './sfxMeta.json';
import type { SfxCue } from './cues';

/**
 * Банк звуков: mp3 из client/public/sfx (рисует tools/sfx.py) скачиваются и декодируются один раз при старте.
 *
 * Путь относительный — как у спрайтов (render/sprites.ts): одна сборка работает и на GitHub Pages,
 * и с корня игрового сервера. Если файл не скачался, звук просто молчит: игра из-за звука падать не должна.
 */

const cueUrl = (cue: string, variant: number) => `sfx/${cue}-${variant}.mp3`;

export class SfxBank {
  private readonly buffers = new Map<string, AudioBuffer[]>();
  private loading: Promise<void> | null = null;

  /** Скачать и декодировать весь банк. Повторный вызов отдаёт ту же загрузку. */
  load(ctx: BaseAudioContext): Promise<void> {
    this.loading ??= this.loadAll(ctx);
    return this.loading;
  }

  /** Вариант звука; null — банк ещё не загрузился или файла нет. */
  pick(cue: SfxCue, roll = Math.random()): AudioBuffer | null {
    const list = this.buffers.get(cue);
    if (!list || list.length === 0) return null;
    return list[Math.min(list.length - 1, Math.floor(roll * list.length))];
  }

  get ready(): boolean {
    return this.buffers.size > 0;
  }

  private async loadAll(ctx: BaseAudioContext): Promise<void> {
    const jobs: Promise<void>[] = [];
    for (const [cue, info] of Object.entries(meta.cues)) {
      const variants: AudioBuffer[] = [];
      this.buffers.set(cue, variants);
      for (let i = 1; i <= info.n; i++) {
        jobs.push(
          fetch(cueUrl(cue, i))
            .then((response) => response.arrayBuffer())
            .then((bytes) => ctx.decodeAudioData(bytes))
            .then((buffer) => void variants.push(buffer))
            .catch(() => {
              // Звук не скачался — остальные всё равно нужны.
            }),
        );
      }
    }
    await Promise.all(jobs);
  }
}
