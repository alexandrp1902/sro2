import { describe, expect, it } from 'vitest';
import { hasSprite, itemSprite, missionSprite, moduleSprite, shipSprite, weaponSprite } from './sprites';

/**
 * Таблицы имён картинок и нарезка живут порознь: имя пишется руками в sprites.ts, а файл появляется из
 * art/ скриптом tools/sprites.py. Опечатка в имени видна только глазами в доке — если туда заглянуть.
 * Здесь она видна сразу.
 */
describe('sprite names', () => {
  it('finds a picture for every module that should have one', () => {
    // Защита M15.6 (арт пачки F), флот M19 (пачка K) и остальные модули со своей иконкой.
    for (const id of [
      'thrusters', 'dustCloud', 'reactiveArmor', 'antiMissile', 'repair', 'cooling', 'cargoPod', 'radarL',
      'grapple', 'deepScanner', 'cloak', 'armorPlate',
    ]) {
      expect(moduleSprite('utility', id), id).not.toBeNull();
    }
    // Тир на картинку не влияет: Mk2 рисуется значком, а не своим спрайтом.
    expect(moduleSprite('utility', 'thrusters_mk2')).toBe(moduleSprite('utility', 'thrusters'));
  });

  it('marks board missions with the drawn kind icons, and leaves the rest bare', () => {
    expect(missionSprite('collect')).toBe('mission-meteor');
    expect(missionSprite('hunt')).toBe('mission-meteor');
    expect(missionSprite('escort')).toBe('mission-escort');
    expect(missionSprite('patrol')).toBe('mission-patrol');
    expect(missionSprite('courier')).toBe('mission-courier');
    expect(missionSprite('ground')).toBe('mission-ground');
    for (const kind of ['kill', 'deliver', 'defend']) expect(missionSprite(kind), kind).toBeNull();
  });

  it('draws the prototype parts as mech parts, not as stand-in goods', () => {
    expect(itemSprite('mechFrame')).toBe('resources-mech-frame');
    expect(itemSprite('driveBlock')).toBe('resources-mech-drive');
    expect(itemSprite('weaponModule')).toBe('resources-mech-weapon');
  });

  it('falls back to the slot, and to nothing for a slot without a picture', () => {
    expect(moduleSprite('engine', 'engineL')).toBe('modules-engine');
    // У вспомогательного слота картинки по слоту нет нарочно: иначе её надел бы каждый новый модуль.
    expect(moduleSprite('utility', 'unknownGizmo')).toBeNull();
  });

  it('finds a picture for every weapon', () => {
    for (const id of [
      'pulse', 'laser', 'cannon', 'heavyLaser', 'railgun', 'ion', 'pointDefense', 'torpedoes', 'missiles',
      'shotgun', 'gauss', 'salvo',
    ]) {
      expect(weaponSprite(id), id).not.toBeNull();
    }
  });

  it('finds a picture for every hull of the fleet', () => {
    // Спрайт корпуса ищется по соглашению ships-<id>, без таблицы: опечатка в id тихо подставила бы «Пчелу».
    for (const id of ['starterTrader', 'needle', 'tug', 'surveyor', 'corsair', 'clipper', 'runner', 'lancer', 'dropship', 'galleon']) {
      expect(shipSprite(id), id).toBe(`ships-${id}`);
    }
  });

  it('knows a missing picture when it sees one', () => {
    expect(hasSprite('modules-thrusters')).toBe(true);
    expect(hasSprite('modules-there-is-no-such-thing')).toBe(false);
  });
});
