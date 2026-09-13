import { describe, expect, it } from 'vitest';
import vectors from '../../../shared/test-vectors/movement.json';
import { DT, step, type HullParams, type ShipState } from './movement';

interface Scenario {
  name: string;
  hull: HullParams;
  start: number[];
  inputs: number[][];
  states: number[][];
}

/** sin/cos/exp/atan2 в V8 и .NET могут расходиться в последнем бите. */
const TOLERANCE = 1e-6;

// Эталоны генерирует C# (server/Sro.Sim.Tests/MovementVectorTests.cs): предсказание клиента обязано совпадать с сервером.
describe('movement matches the server (shared/test-vectors/movement.json)', () => {
  it('uses the same tick length', () => {
    expect(vectors.dt).toBe(DT);
  });

  for (const scenario of vectors.scenarios as Scenario[]) {
    it(scenario.name, () => {
      const [x, y, rot, vx, vy] = scenario.start;
      const s: ShipState = { x, y, rot, vx, vy };
      scenario.inputs.forEach(([dx, dy, throttle], i) => {
        step(s, { dx, dy, throttle }, scenario.hull, DT);
        const actual = [s.x, s.y, s.rot, s.vx, s.vy];
        scenario.states[i].forEach((expected, k) => {
          if (Math.abs(actual[k] - expected) > TOLERANCE) {
            throw new Error(`step ${i}, component ${k}: expected ${expected}, got ${actual[k]}`);
          }
        });
      });
    });
  }
});
