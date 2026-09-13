import { describe, expect, it } from 'vitest';
import { Hulls } from '../sim/hulls';
import { DT, step, type MoveInput, type ShipState } from '../sim/movement';
import { Prediction } from './prediction';
import type { ShipDto } from './protocol';

const SPAWN = { x: 0, y: 600 };

/** Сервер без сети: шаг на каждый вход, снапшот = состояние после входа ack. */
class FakeServer {
  readonly state: ShipState = { x: SPAWN.x, y: SPAWN.y, rot: 0, vx: 0, vy: 0 };
  ack = 0;

  constructor(private readonly hulls: Hulls) {}

  apply(seq: number, input: MoveInput): void {
    step(this.state, input, this.hulls.get('light'), DT);
    this.ack = seq;
  }

  snapshot(): ShipDto {
    const s = this.state;
    return { id: 1, x: s.x, y: s.y, r: s.rot, vx: s.vx, vy: s.vy, hull: 'light', th: 0, ack: this.ack };
  }
}

function setup() {
  const hulls = new Hulls();
  const prediction = new Prediction(hulls, 'light', SPAWN);
  const server = new FakeServer(hulls);
  const inFlight: { seq: number; input: MoveInput }[] = [];
  const send = (seq: number, input: MoveInput) => inFlight.push({ seq, input });
  return { prediction, server, inFlight, send };
}

describe('Prediction', () => {
  it('needs no correction when the server runs the same model, even with inputs in flight', () => {
    const { prediction, server, inFlight, send } = setup();
    prediction.reconcile(server.snapshot()); // первый снапшот принимается как есть
    for (let tick = 0; tick < 120; tick++) {
      const input = { dx: Math.sin(tick / 10), dy: -Math.cos(tick / 10), throttle: tick < 80 ? 1 : 0 };
      prediction.step(input, send);
      // Задержка сети 3 тика: сервер видит входы с опозданием.
      if (inFlight.length > 3) {
        const { seq, input: delayed } = inFlight.shift()!;
        server.apply(seq, delayed);
        prediction.reconcile(server.snapshot());
        expect(prediction.lastCorrection).toBeLessThan(1e-9);
      }
    }
    expect(prediction.snaps).toBe(0);
    expect(prediction.pendingCount).toBe(3);
  });

  it('smoothly corrects a small divergence and snaps a large one', () => {
    const { prediction, server, send } = setup();
    prediction.reconcile(server.snapshot());
    prediction.step({ dx: 0, dy: -1, throttle: 1 }, send);

    server.apply(1, { dx: 0, dy: -1, throttle: 1 });
    server.state.x += 5; // сервер знает то, чего не знал клиент
    prediction.reconcile(server.snapshot());
    expect(prediction.lastCorrection).toBeCloseTo(5);
    expect(prediction.snaps).toBe(0);
    // Картинка сразу не прыгает: смещение гасится со временем.
    expect(prediction.render(1, 0).x).toBeCloseTo(prediction.curr.x - 5);
    expect(prediction.render(1, 1).x).toBeCloseTo(prediction.curr.x, 3);

    server.state.x += 500;
    prediction.reconcile(server.snapshot());
    expect(prediction.snaps).toBe(1);
  });

  it('accepts the server state after a reconnect', () => {
    const { prediction, server, send } = setup();
    prediction.step({ dx: 1, dy: 0, throttle: 1 }, null); // локальный полёт без сервера
    prediction.resetNet();
    server.state.x = 1234;
    prediction.reconcile(server.snapshot());
    expect(prediction.curr.x).toBe(1234);
    expect(prediction.snaps).toBe(0);
    prediction.step({ dx: 1, dy: 0, throttle: 1 }, send);
    expect(prediction.pendingCount).toBe(1);
  });
});
