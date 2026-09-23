import { describe, expect, it } from 'vitest';
import { DEFAULT_PREFS, parsePrefs, serializePrefs } from './settings';

describe('настройки звука', () => {
  it('пусто и мусор в хранилище не оставляют игрока без звука', () => {
    expect(parsePrefs(null)).toEqual(DEFAULT_PREFS);
    expect(parsePrefs('{{')).toEqual(DEFAULT_PREFS);
    expect(parsePrefs('"строка"')).toEqual(DEFAULT_PREFS);
  });

  it('музыка по умолчанию тише звуков: саундтрек не должен перекрывать выстрел по мне', () => {
    expect(DEFAULT_PREFS.music).toBeLessThan(DEFAULT_PREFS.sfx);
    expect(DEFAULT_PREFS.master).toBeGreaterThan(0);
  });

  it('значения за границами подрезаются, чужие поля не ломают разбор', () => {
    const prefs = parsePrefs(JSON.stringify({ master: 5, music: -2, sfx: 'громко', вкус: 'солёный' }));
    expect(prefs.master).toBe(1);
    expect(prefs.music).toBe(0);
    expect(prefs.sfx).toBe(DEFAULT_PREFS.sfx);
  });

  it('незнакомая боевая тема сводится к первой: слоёв под неё в манифесте нет', () => {
    expect(parsePrefs(JSON.stringify({ combat: 'organ' })).combat).toBe('taiko');
    expect(parsePrefs(JSON.stringify({ combat: 'chase' })).combat).toBe('chase');
  });

  it('запись и чтение сходятся', () => {
    const prefs = { master: 0.3, music: 0.15, sfx: 0.65, radio: 0.4, subtitles: false, combat: 'duel' as const };
    expect(parsePrefs(serializePrefs(prefs))).toEqual(prefs);
  });
});
