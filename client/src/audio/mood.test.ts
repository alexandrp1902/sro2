import { describe, expect, it } from 'vitest';
import { COMBAT_HOLD_MS, nextMood, type MoodSignals } from './mood';

const quiet = (over: Partial<MoodSignals> = {}): MoodSignals => ({
  now: 100000,
  lastHostileShot: 0,
  lastOwnShot: 0,
  threats: 0,
  incoming: 0,
  docked: false,
  dead: false,
  ...over,
});

describe('настроение музыки', () => {
  it('в пустом космосе — покой', () => {
    expect(nextMood(quiet())).toBe('calm');
  });

  it('бой начинается с первого же признака', () => {
    expect(nextMood(quiet({ lastHostileShot: 100000 }))).toBe('combat');
    expect(nextMood(quiet({ lastOwnShot: 100000 }))).toBe('combat');
    expect(nextMood(quiet({ threats: 1 }))).toBe('combat');
    expect(nextMood(quiet({ incoming: 1 }))).toBe('combat');
  });

  it('пират, взявший меня целью, держит бой и без выстрелов', () => {
    // Он уже развернулся и идёт на сближение — музыка не должна ждать первого попадания.
    expect(nextMood(quiet({ threats: 2, lastHostileShot: 0, lastOwnShot: 0 }))).toBe('combat');
  });

  it('между залпами музыка не дёргается: бой отпускает только через семь секунд тишины', () => {
    const shotAt = 50000;
    expect(nextMood(quiet({ now: shotAt + COMBAT_HOLD_MS - 100, lastHostileShot: shotAt }))).toBe('combat');
    expect(nextMood(quiet({ now: shotAt + COMBAT_HOLD_MS + 100, lastHostileShot: shotAt }))).toBe('calm');
  });

  it('стрельба по метеоритам боем не считается', () => {
    // Сигнал о своём выстреле ставится только по кораблю (main.ts), поэтому добыча минералов
    // приходит сюда как тишина — иначе боевая тема играла бы весь майнинг.
    expect(nextMood(quiet({ lastOwnShot: 0, lastHostileShot: 0 }))).toBe('calm');
  });

  it('док и гибель перебивают бой', () => {
    expect(nextMood(quiet({ docked: true, threats: 3, lastHostileShot: 100000 }))).toBe('dock');
    expect(nextMood(quiet({ dead: true, docked: true, threats: 3 }))).toBe('dead');
  });
});
