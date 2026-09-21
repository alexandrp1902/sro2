import { describe, expect, it } from 'vitest';
import { hasSprite, moduleSprite, weaponSprite } from './sprites';

/**
 * Таблицы имён картинок и нарезка живут порознь: имя пишется руками в sprites.ts, а файл появляется из
 * art/ скриптом tools/sprites.py. Опечатка в имени видна только глазами в доке — если туда заглянуть.
 * Здесь она видна сразу.
 */
describe('sprite names', () => {
  it('finds a picture for every module that should have one', () => {
    // Защита M15.6 (арт пачки F) и остальные модули со своей иконкой.
    for (const id of ['thrusters', 'dustCloud', 'reactiveArmor', 'antiMissile', 'repair', 'cooling', 'cargoPod', 'radarL']) {
      expect(moduleSprite('utility', id), id).not.toBeNull();
    }
    // Тир на картинку не влияет: Mk2 рисуется значком, а не своим спрайтом.
    expect(moduleSprite('utility', 'thrusters_mk2')).toBe(moduleSprite('utility', 'thrusters'));
  });

  it('falls back to the slot, and to nothing for a slot without a picture', () => {
    expect(moduleSprite('engine', 'engineL')).toBe('modules-engine');
    // У вспомогательного слота картинки по слоту нет нарочно: иначе её надел бы каждый новый модуль.
    expect(moduleSprite('utility', 'unknownGizmo')).toBeNull();
  });

  it('finds a picture for every weapon', () => {
    for (const id of ['pulse', 'laser', 'cannon', 'heavyLaser', 'railgun', 'ion', 'pointDefense', 'torpedoes', 'missiles']) {
      expect(weaponSprite(id), id).not.toBeNull();
    }
  });

  it('knows a missing picture when it sees one', () => {
    expect(hasSprite('modules-thrusters')).toBe(true);
    expect(hasSprite('modules-there-is-no-such-thing')).toBe(false);
  });
});
