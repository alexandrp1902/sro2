import { describe, expect, it } from 'vitest';
import chatter from '../../../shared/chatter.json';
import meta from './sfxMeta.json';
import music from './musicMeta.json';
import radio from './radioMeta.json';

/**
 * Манифесты и сами файлы — результат работы tools/sfx.py и tools/music.py, и оба лежат в git.
 * Разъехаться они могут незаметно: манифест обновился, а mp3 не докоммитили — звук просто пропадёт.
 *
 * Файлы ищутся так же, как их потом ищет сборщик, — поэтому здесь import.meta.glob, а не чтение с диска.
 */

const sfxFiles = new Set(Object.keys(import.meta.glob('../../public/sfx/*.mp3')).map(nameOf));
const musicFiles = new Set(Object.keys(import.meta.glob('../../public/music/*.mp3')).map(nameOf));
const radioFiles = new Set(Object.keys(import.meta.glob('../../public/radio/*.mp3')).map(nameOf));

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

describe('радиоэфир', () => {
  it('у каждой реплики банка есть файл и запись в манифесте с длительностью', () => {
    const lines = radio.lines as Record<string, { ms: number; category: string }>;
    for (const [category, block] of Object.entries(chatter.categories)) {
      for (const line of block.lines) {
        const key = `${category}-${line.id}`;
        expect(radioFiles.has(key), `нет файла ${key}.mp3`).toBe(true);
        expect(lines[key]?.ms, key).toBeGreaterThan(300);
        expect(lines[key]?.category, key).toBe(category);
      }
    }
  });

  it('файлов и записей без реплики в банке нет', () => {
    const known = new Set<string>();
    for (const [category, block] of Object.entries(chatter.categories)) for (const line of block.lines) known.add(`${category}-${line.id}`);
    expect([...radioFiles].filter((name) => !known.has(name))).toEqual([]);
    expect(Object.keys(radio.lines).filter((name) => !known.has(name))).toEqual([]);
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

  it('каждый боевой слой принадлежит ровно одной теме, и обе темы не пусты', () => {
    const stems = music.stems as Record<string, string>;
    const owners = new Map<string, string>();
    for (const [theme, names] of Object.entries(music.themes)) {
      expect(names.length, theme).toBeGreaterThan(0);
      for (const name of names) {
        expect(stems[name], `${theme}: ${name} нет в манифесте`).toBe('combat');
        expect(owners.has(name), `${name} сразу в двух темах`).toBe(false);
        owners.set(name, theme);
      }
    }
    for (const [name, mood] of Object.entries(stems)) {
      if (mood === 'combat') expect(owners.has(name), `${name} вне тем`).toBe(true);
    }
  });

  it('файлов без записи в манифесте нет: старые слои не должны лежать мёртвым грузом', () => {
    const extra = [...musicFiles].filter((name) => !(name in music.stems));
    expect(extra).toEqual([]);
  });
});
