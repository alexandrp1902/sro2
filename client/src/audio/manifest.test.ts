import { describe, expect, it } from 'vitest';
import meta from './sfxMeta.json';
import music from './musicMeta.json';

/**
 * Манифесты и сами файлы — результат работы tools/sfx.py и tools/music.py, и оба лежат в git.
 * Разъехаться они могут незаметно: манифест обновился, а mp3 не докоммитили — звук просто пропадёт.
 *
 * Файлы ищутся так же, как их потом ищет сборщик, — поэтому здесь import.meta.glob, а не чтение с диска.
 */

const sfxFiles = new Set(Object.keys(import.meta.glob('../../public/sfx/*.mp3')).map(nameOf));
const musicFiles = new Set(Object.keys(import.meta.glob('../../public/music/*.mp3')).map(nameOf));

function nameOf(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1, -'.mp3'.length);
}

describe('банк звуков', () => {
  it('у каждого звука манифеста на месте все варианты и ни одного лишнего', () => {
    const expected = new Set<string>();
    for (const [cue, info] of Object.entries(meta.cues)) {
      for (let i = 1; i <= info.n; i++) {
        expect(sfxFiles.has(`${cue}-${i}`), `нет файла ${cue}-${i}.mp3`).toBe(true);
        expected.add(`${cue}-${i}`);
      }
    }
    const extra = [...sfxFiles].filter((name) => !expected.has(name));
    expect(extra, 'файлы без записи в манифесте').toEqual([]);
  });

  it('у каждого звука есть длина и громкость', () => {
    for (const [cue, info] of Object.entries(meta.cues)) {
      expect(info.ms, cue).toBeGreaterThan(0);
      expect(info.gain, cue).toBeGreaterThan(0);
      expect(info.gain, cue).toBeLessThanOrEqual(1);
    }
  });
});

describe('слои музыки', () => {
  it('все слои манифеста лежат в public/music', () => {
    for (const name of Object.keys(music.stems)) expect(musicFiles.has(name), name).toBe(true);
  });

  it('есть и покой, и бой: без одного из них переход не из чего делать', () => {
    const moods = new Set(Object.values(music.stems));
    expect(moods.has('calm')).toBe(true);
    expect(moods.has('combat')).toBe(true);
  });
});
