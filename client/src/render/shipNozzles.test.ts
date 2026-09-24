import { describe, expect, it } from 'vitest';
import { SHIP_NOZZLES, shipNozzles } from './shipNozzles';
import meta from './spriteMeta.json';
import { shipSprite, spriteSize, type SpriteName } from './sprites';

const ships = (Object.keys(meta) as SpriteName[]).filter((name) => name.startsWith('ships-'));

/**
 * Точки сопел сняты с картинок (см. tools/nozzles.py — он же сверяет их с альфой спрайтов и рисует
 * контактный лист). Здесь — только то, что можно проверить без самих картинок: корабль без огня
 * и сопло за краем кадра.
 */
describe('SHIP_NOZZLES', () => {
  it('огонь есть у каждого корабля: и у корпусов пилота, и у NPC', () => {
    for (const name of ships) expect(shipNozzles(name), name).not.toHaveLength(0);
    expect(Object.keys(SHIP_NOZZLES).sort()).toEqual([...ships].sort());
  });

  it('сопла лежат внутри кадра спрайта', () => {
    for (const name of ships) {
      const { w, h } = spriteSize(name);
      for (const [x, y, width] of shipNozzles(name)) {
        expect(x, `${name} x`).toBeGreaterThanOrEqual(0);
        expect(x, `${name} x`).toBeLessThan(w);
        expect(y, `${name} y`).toBeGreaterThanOrEqual(0);
        expect(y, `${name} y`).toBeLessThan(h);
        // Ширина сопла задаёт и ширину языка, и его длину: ноль дал бы невидимый огонь.
        expect(width, `${name} width`).toBeGreaterThan(0);
        expect(x - width / 2, `${name} left`).toBeGreaterThanOrEqual(0);
        expect(x + width / 2, `${name} right`).toBeLessThanOrEqual(w);
      }
    }
  });

  it('сопла в кормовой половине картинки: корабли нарисованы носом вверх', () => {
    for (const name of ships) {
      const { h } = spriteSize(name);
      for (const [, y] of shipNozzles(name)) expect(y, name).toBeGreaterThan(h / 2);
    }
  });

  it('у чужого имени сопел нет, а не исключение: спрайт мог прийти с сервера', () => {
    expect(shipNozzles('station' as SpriteName)).toHaveLength(0);
  });

  it('каждый корпус игры получает огонь через shipSprite', () => {
    for (const hull of ['light', 'lancer', 'cruiser', 'interceptor', 'heavy']) {
      expect(shipNozzles(shipSprite(hull)), hull).not.toHaveLength(0);
    }
  });
});
