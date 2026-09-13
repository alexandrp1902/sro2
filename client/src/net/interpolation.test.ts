import { describe, expect, it } from 'vitest';
import { MAX_EXTRAPOLATION_MS, RenderClock, SnapshotBuffer, type ShipSample } from './interpolation';
import type { ShipDto, SnapshotMsg } from './protocol';

const TICK_MS = 50;
const FRAME_MS = 1000 / 60;
/** Корабль летит вправо 200 ед/с: 10 единиц за тик. */
const SPEED = 200;

function ship(id: number, x: number, r = 0): ShipDto {
  return { id, x, y: 0, r, vx: SPEED, vy: 0, hull: 'light', th: 1, ack: 0 };
}

function snapshot(tick: number, ...ships: ShipDto[]): SnapshotMsg {
  return { t: 'snapshot', tick, ships: ships.length > 0 ? ships : [ship(7, tick * 10)] };
}

/** Детерминированный ГПСЧ (mulberry32), чтобы тест с джиттером не мигал. */
function random(seed: number): () => number {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

interface Frame {
  now: number;
  renderTick: number;
  latest: number;
  sample: ShipSample;
  delayMs: number;
}

/**
 * Сервер шлёт тик каждые 50 мс, доставка с задержкой delay(tick) без обгона (как TCP),
 * клиент рисует 60 кадров/с.
 */
function run(durationMs: number, delay: (tick: number) => number): Frame[] {
  const clock = new RenderClock();
  const buffer = new SnapshotBuffer();
  const arrivals: { at: number; tick: number }[] = [];
  let lastAt = 0;
  for (let tick = 1; tick * TICK_MS < durationMs; tick++) {
    lastAt = Math.max(lastAt, tick * TICK_MS + delay(tick));
    arrivals.push({ at: lastAt, tick });
  }

  const frames: Frame[] = [];
  let next = 0;
  let latest = 0;
  for (let now = 0; now < durationMs; now += FRAME_MS) {
    while (next < arrivals.length && arrivals[next].at <= now) {
      const { at, tick } = arrivals[next++];
      buffer.push(snapshot(tick));
      clock.onSnapshot(tick, at);
      latest = tick;
    }
    const renderTick = clock.update(now);
    if (Number.isNaN(renderTick)) continue;
    frames.push({ now, renderTick, latest, sample: buffer.sample(7, renderTick)!, delayMs: clock.delayMs });
  }
  return frames;
}

/** Часы рендера идут вперёд со скоростью 0.9–1.1 от реального времени — без рывков и шагов назад. */
function expectSmooth(frames: Frame[]): void {
  for (let i = 1; i < frames.length; i++) {
    const rate = ((frames[i].renderTick - frames[i - 1].renderTick) * TICK_MS) / (frames[i].now - frames[i - 1].now);
    expect(rate).toBeGreaterThanOrEqual(0.9 - 1e-6);
    expect(rate).toBeLessThanOrEqual(1.1 + 1e-6);
  }
}

describe('RenderClock', () => {
  it('keeps the minimal delay on a steady stream and never extrapolates', () => {
    const frames = run(5000, () => 2).filter((f) => f.now > 1000);
    expectSmooth(frames);
    expect(frames.every((f) => !f.sample.extrapolated)).toBe(true);
    expect(frames[frames.length - 1].delayMs).toBe(75);
  });

  it('grows the delay under tunnel-like jitter so snapshots still arrive in time', () => {
    const rand = random(42);
    // RTT 300 мс ±25%, как «лаг 300 мс» в dev-панели.
    const frames = run(15000, () => 150 * (0.75 + 0.5 * rand())).filter((f) => f.now > 3000);
    expectSmooth(frames);
    const extrapolated = frames.filter((f) => f.sample.extrapolated).length;
    expect(extrapolated / frames.length).toBeLessThan(0.005);
    const delay = frames[frames.length - 1].delayMs;
    expect(delay).toBeGreaterThan(75);
    expect(delay).toBeLessThan(160);
  });

  it('extrapolates a short way through a stall and recovers without going back', () => {
    // Снапшоты 100–109 застряли и пришли разом через полсекунды.
    const frames = run(10000, (tick) => (tick >= 100 && tick < 110 ? (110 - tick) * TICK_MS + 2 : 2)).filter(
      (f) => f.now > 1000,
    );
    expectSmooth(frames);

    const extrapolated = frames.filter((f) => f.sample.extrapolated);
    expect(extrapolated.length).toBeGreaterThan(0);
    for (const f of extrapolated) {
      expect(f.sample.x - f.latest * 10).toBeLessThanOrEqual((SPEED * MAX_EXTRAPOLATION_MS) / 1000 + 1e-9);
    }

    const tail = frames.filter((f) => f.now > 8500);
    expect(tail.every((f) => !f.sample.extrapolated)).toBe(true);
    expect(tail[tail.length - 1].delayMs).toBeLessThan(80);
  });
});

describe('SnapshotBuffer', () => {
  it('interpolates between snapshots and turns through ±π', () => {
    const buffer = new SnapshotBuffer();
    buffer.push(snapshot(10, { ...ship(1, 0, 3.1), y: 0 }));
    buffer.push(snapshot(11, { ...ship(1, 10, -3.1), y: 20 }));
    const s = buffer.sample(1, 10.5)!;
    expect(s.x).toBeCloseTo(5);
    expect(s.y).toBeCloseTo(10);
    expect(Math.abs(s.rot)).toBeGreaterThan(3.1);
    expect(s.extrapolated).toBe(false);
    expect(buffer.sample(2, 10.5)).toBeNull();
  });

  it('shows a ship that just joined only when render time reaches it', () => {
    const buffer = new SnapshotBuffer();
    buffer.push(snapshot(10, ship(1, 0)));
    buffer.push(snapshot(11, ship(1, 10), ship(2, 500)));
    expect(buffer.sample(2, 10.5)).toBeNull();
    expect(buffer.sample(2, 11.2)?.x).toBeCloseTo(500 + SPEED * 0.2 * 0.05);
  });

  it('drops late snapshots but starts over when the server restarts', () => {
    const buffer = new SnapshotBuffer();
    expect(buffer.push(snapshot(50))).toBe(true);
    expect(buffer.push(snapshot(49))).toBe(false);
    expect(buffer.push(snapshot(3))).toBe(true);
    expect(buffer.latest?.tick).toBe(3);
  });
});
