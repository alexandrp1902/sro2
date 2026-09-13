import { describe, expect, it } from 'vitest';
import {
  DEAD_ZONE,
  DOUBLE_TAP_MS,
  DOUBLE_TAP_PX,
  DoubleTapDetector,
  clampToCircle,
  stickRadius,
  stickThrottle,
} from './stickMath';

describe('stickThrottle', () => {
  it('is zero inside the dead zone', () => {
    expect(stickThrottle(0)).toBe(0);
    expect(stickThrottle(DEAD_ZONE)).toBe(0);
  });

  it('reaches full throttle at the rim and never exceeds it', () => {
    expect(stickThrottle(1)).toBe(1);
    expect(stickThrottle(1.4)).toBe(1);
  });

  it('is gentler than linear near the centre (§29)', () => {
    expect(stickThrottle(0.4)).toBeLessThan(0.3);
    expect(stickThrottle(0.55)).toBeCloseTo(0.5 ** 1.5, 10);
  });

  it('grows monotonically', () => {
    let previous = -1;
    for (let d = 0; d <= 1; d += 0.05) {
      const t = stickThrottle(d);
      expect(t).toBeGreaterThanOrEqual(previous);
      previous = t;
    }
  });
});

describe('stickRadius', () => {
  it('stays within 56–72 CSS px (§27)', () => {
    expect(stickRadius(390, 844)).toBeCloseTo(62.4);
    expect(stickRadius(320, 568)).toBe(56);
    expect(stickRadius(1920, 1080)).toBe(72);
  });
});

describe('clampToCircle', () => {
  it('keeps points inside and projects outside ones onto the rim', () => {
    expect(clampToCircle(3, 4, 10)).toEqual([3, 4]);
    const [x, y] = clampToCircle(30, 40, 10);
    expect(x).toBeCloseTo(6);
    expect(y).toBeCloseTo(8);
  });
});

describe('DoubleTapDetector', () => {
  it('detects two close taps in quick succession', () => {
    const taps = new DoubleTapDetector();
    expect(taps.tap(1000, 50, 50)).toBe(false);
    expect(taps.tap(1000 + DOUBLE_TAP_MS - 1, 50 + DOUBLE_TAP_PX - 1, 50)).toBe(true);
  });

  it('ignores slow taps and taps far apart (§31)', () => {
    const taps = new DoubleTapDetector();
    taps.tap(1000, 50, 50);
    expect(taps.tap(1000 + DOUBLE_TAP_MS + 1, 50, 50)).toBe(false);
    expect(taps.tap(1000 + DOUBLE_TAP_MS + 50, 50 + DOUBLE_TAP_PX + 5, 50)).toBe(false);
  });

  it('does not count a third tap as another double tap', () => {
    const taps = new DoubleTapDetector();
    taps.tap(0, 0, 0);
    expect(taps.tap(100, 0, 0)).toBe(true);
    expect(taps.tap(200, 0, 0)).toBe(false);
  });
});
