import { describe, expect, it } from 'vitest';
import vectors from '../../../shared/test-vectors/mech.json';
import {
  DEFAULT_RULES,
  MechField,
  blockChance,
  combatOf,
  damageRange,
  direction,
  forecast,
  hitChance,
  moveRange,
  partWeights,
  side,
  type MechBattleView,
  type MechCombatDef,
  type MechPart,
  type MechSide,
  type MechUnitView,
} from './rules';

describe('прогноз мехов совпадает с сервером (shared/test-vectors/mech.json)', () => {
  const c = vectors.combat as MechCombatDef;
  const weapons = vectors.weapons as Record<string, (typeof vectors.weapons)['autocannon']>;

  it('шанс попасть', () => {
    for (const h of vectors.hit) {
      const got = hitChance(c, weapons[h.weapon], h.distance, h.steps, h.moveRange, h.side as MechSide, h.aimed);
      expect(got, JSON.stringify(h)).toBe(h.chance);
    }
  });

  it('сторона цели — четыре сектора', () => {
    for (const s of vectors.side) expect(side(s.ax, s.ay, s.tx, s.ty, s.dir), JSON.stringify(s)).toBe(s.side);
  });

  it('шанс блока щитом', () => {
    for (const b of vectors.block) {
      const got = blockChance(c, vectors.shield, b.shieldArm as MechPart, b.alive, b.side as MechSide);
      expect(got, JSON.stringify(b)).toBe(b.chance);
    }
  });

  it('вилка урона', () => {
    for (const d of vectors.damage) {
      expect(damageRange(c, weapons[d.weapon], d.armors), JSON.stringify(d)).toEqual([d.min, d.max]);
    }
  });

  it('ход с повреждённым шасси', () => {
    for (const m of vectors.move) expect(moveRange(m.base, m.hp, m.max), JSON.stringify(m)).toBe(m.range);
  });

  it('направление на клетку', () => {
    for (const d of vectors.dir) expect(direction(d.dx, d.dy), JSON.stringify(d)).toBe(d.dir);
  });

  it('веса частей', () => {
    for (const w of vectors.weights) {
      expect(partWeights(c, w.side as MechSide, w.hp), JSON.stringify(w)).toEqual(w.weights);
    }
  });

  it('достижимые клетки', () => {
    const field = new MechField(vectors.map);
    for (const r of vectors.reach) {
      const got = [...field.reach(r.x, r.y, r.range, new Set(r.occupied))].sort((a, b) => a[0] - b[0]);
      expect(got, `${r.x},${r.y}`).toEqual(r.cells);
    }
  });

  it('линия огня', () => {
    const field = new MechField(vectors.map);
    for (const l of vectors.line) {
      let got = '';
      for (let i = 0; i < field.width * field.height; i++) {
        got += field.lineOfFire(l.x, l.y, i % field.width, Math.floor(i / field.width)) ? '1' : '0';
      }
      expect(got, `${l.x},${l.y}`).toBe(l.clear);
    }
  });
});

describe('прогноз выстрела', () => {
  const unit = (id: string, sideOf: 'player' | 'enemy', x: number, y: number, dir: number, kind = 'raider'): MechUnitView => ({
    id, side: sideOf, unit: kind, name: id, x, y, dir,
    hp: [2200, 1100, 600, 1100], max: [2200, 1100, 600, 1100], activated: false,
  });
  const state = (units: MechUnitView[], map = Array(12).fill('............')): MechBattleView => ({
    mission: 'firstSortie', map, round: 1, turn: 'player', current: units[0].id, moved: false, steps: 0, moveRange: 4, units,
  });

  it('стоял, фронт, в оптимуме: 82 + 10', () => {
    const me = unit('p1', 'player', 0, 0, 4, 'prototype');
    const foe = unit('e1', 'enemy', 0, 4, 0);
    const f = forecast(DEFAULT_RULES, state([me, foe]), me, foe, null);
    expect(f.ok).toBe(true);
    expect(f.side).toBe('front');
    expect(f.chance).toBe(92);
    // Щит слева у цели: с фронта — базовые 30.
    expect(f.block).toBe(30);
    expect(f.min).toBeLessThan(f.max);
  });

  it('прицельный — минус 25, и вилка — по броне выбранной части', () => {
    const me = unit('p1', 'player', 0, 0, 4, 'prototype');
    const foe = unit('e1', 'enemy', 0, 4, 0);
    const f = forecast(DEFAULT_RULES, state([me, foe]), me, foe, 'chassis');
    expect(f.chance).toBe(67);
    const c = combatOf(DEFAULT_RULES);
    expect([f.min, f.max]).toEqual(damageRange(c, DEFAULT_RULES.weapons!.autocannon, [20, 25]));
  });

  it('стена закрывает, дальше дальности — нельзя', () => {
    const me = unit('p1', 'player', 0, 5, 2, 'prototype');
    const far = unit('e1', 'enemy', 10, 5, 6);
    const map = Array(12).fill('............');
    map[5] = '..w.........';
    expect(forecast(DEFAULT_RULES, state([me, far], map), me, far, null).refusal).toBe('outOfRange');
    const near = unit('e2', 'enemy', 5, 5, 6);
    expect(forecast(DEFAULT_RULES, state([me, near], map), me, near, null).refusal).toBe('noLine');
  });
});
