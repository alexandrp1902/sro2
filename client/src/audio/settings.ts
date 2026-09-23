import { storage } from '../util/storage';

/**
 * Громкости и их хранение на устройстве. Разбор и сборка — чистые функции: их и проверяют тесты,
 * localStorage для этого не нужен. Класс поверх них устроен как раскладка клавиш (input/keymap.ts):
 * читает при создании, пишет при каждой правке, оповещает подписчиков.
 */

/**
 * Боевая тема: «орган» — барабаны и церковный орган по заданию (docs/SRO - Задание на боевую музыку.md),
 * «марш» — медь, малый барабан и струнные в духе космической оперы. Оба набора в одной тональности
 * и темпе, так что переключение — тот же кроссфейд, что и вход в бой.
 */
export const COMBAT_THEMES = ['organ', 'march'] as const;
export type CombatTheme = (typeof COMBAT_THEMES)[number];

export interface AudioPrefs {
  /** Все громкости 0..1. */
  master: number;
  music: number;
  sfx: number;
  /** Голоса в эфире (M17b). */
  radio: number;
  /** Субтитры эфира в ленте — отдельно от голоса: звук можно выключить, а текст читать. */
  subtitles: boolean;
  combat: CombatTheme;
}

/**
 * Музыка тише звуков нарочно: саундтрек не должен перекрывать выстрел по мне — это боевая информация,
 * а не украшение.
 */
export const DEFAULT_PREFS: AudioPrefs = { master: 0.8, music: 0.5, sfx: 0.8, radio: 0.9, subtitles: true, combat: 'organ' };

export const AUDIO_KEY = 'sro.audio';

const clamp01 = (v: unknown, fallback: number): number =>
  typeof v === 'number' && Number.isFinite(v) ? Math.min(1, Math.max(0, v)) : fallback;

/** Мусор в хранилище и настройки от будущих версий не должны оставлять игрока без звука. */
export function parsePrefs(raw: string | null): AudioPrefs {
  if (!raw) return { ...DEFAULT_PREFS };
  try {
    const json = JSON.parse(raw) as Partial<AudioPrefs>;
    return {
      master: clamp01(json.master, DEFAULT_PREFS.master),
      music: clamp01(json.music, DEFAULT_PREFS.music),
      sfx: clamp01(json.sfx, DEFAULT_PREFS.sfx),
      radio: clamp01(json.radio, DEFAULT_PREFS.radio),
      subtitles: typeof json.subtitles === 'boolean' ? json.subtitles : DEFAULT_PREFS.subtitles,
      combat: (COMBAT_THEMES as readonly string[]).includes(json.combat as string) ? (json.combat as CombatTheme) : DEFAULT_PREFS.combat,
    };
  } catch {
    return { ...DEFAULT_PREFS };
  }
}

export function serializePrefs(prefs: AudioPrefs): string {
  return JSON.stringify(prefs);
}

/** То, что нужно окну «Звук»: оно работает и в витрине ?demo=, где звукового движка нет вовсе. */
export interface AudioPrefsPort {
  readonly prefs: AudioPrefs;
  set(patch: Partial<AudioPrefs>): void;
  onChange(listener: (prefs: AudioPrefs) => void): void;
}

export class AudioSettings implements AudioPrefsPort {
  private current: AudioPrefs;
  private readonly listeners: ((prefs: AudioPrefs) => void)[] = [];

  constructor(private readonly key = AUDIO_KEY) {
    this.current = parsePrefs(storage.get(key));
  }

  get prefs(): AudioPrefs {
    return this.current;
  }

  set(patch: Partial<AudioPrefs>): void {
    this.current = parsePrefs(serializePrefs({ ...this.current, ...patch }));
    storage.set(this.key, serializePrefs(this.current));
    for (const listener of this.listeners) listener(this.current);
  }

  onChange(listener: (prefs: AudioPrefs) => void): void {
    this.listeners.push(listener);
  }
}
