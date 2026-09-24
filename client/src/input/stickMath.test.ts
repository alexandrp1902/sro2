import { describe, expect, it } from 'vitest';
import {
  DEAD_ZONE,
  DOUBLE_TAP_MS,
  DOUBLE_TAP_PX,
  DoubleTapDetector,
  StickGrab,
  clampToCircle,
  rescaleKnob,
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

describe('StickGrab', () => {
  const grab = () => new StickGrab();

  it('стик держит один палец: второй его не перебивает', () => {
    const g = grab();
    expect(g.down(1, 0, 0, 0, true)).toBe('grabbed');
    expect(g.down(2, 50, 50, 10, true)).toBe('ignored');
    expect(g.move(2, 80, 80)).toBe('ignored');
    expect(g.id).toBe(1);
  });

  it('тянет только после TAP_SLOP_PX, чтобы тап не дёргал корабль', () => {
    const g = grab();
    g.down(1, 100, 100, 0, true);
    expect(g.move(1, 104, 100)).toBe('ignored');
    expect(g.move(1, 120, 100)).toBe('dragging');
  });

  it('двойной тап по стику — стоп, одиночный — нет', () => {
    const g = grab();
    g.down(1, 10, 10, 0, true);
    expect(g.up(1, 'pointerup', 50, 10, 10)).toBe('released');
    g.down(1, 10, 10, 100, true);
    expect(g.up(1, 'pointerup', 150, 10, 10)).toBe('stop');
  });

  it('pointercancel отпускает палец, но тапом не считается', () => {
    const g = grab();
    g.down(1, 10, 10, 0, true);
    expect(g.up(1, 'pointercancel', 50, 10, 10)).toBe('released');
    expect(g.held).toBe(false);
  });

  it('перетаскивание отпусканием тапа не становится', () => {
    const g = grab();
    g.down(1, 10, 10, 0, true);
    g.move(1, 60, 10);
    expect(g.up(1, 'pointerup', 50, 60, 10)).toBe('released');
  });

  it('release возвращает стик, если отпускание потерялось: та самая «задипавшая» ручка', () => {
    const g = grab();
    g.down(1, 10, 10, 0, true);
    // Свернули приложение, пришёл звонок — pointerup не пришёл вовсе.
    expect(g.down(2, 20, 20, 10, true)).toBe('ignored');
    g.release();
    expect(g.down(2, 20, 20, 20, true)).toBe('grabbed');
    expect(g.move(2, 80, 20)).toBe('dragging');
  });

  it('чужое отпускание стик не трогает', () => {
    const g = grab();
    g.down(1, 10, 10, 0, true);
    expect(g.up(7, 'pointerup', 20, 10, 10)).toBe('ignored');
    expect(g.held).toBe(true);
  });

  it('помнит, удалось ли забрать указатель: без захвата отпускание придёт мимо стика', () => {
    const g = grab();
    g.down(1, 10, 10, 0, false);
    expect(g.capturedPointer).toBe(false);
  });
});

describe('rescaleKnob', () => {
  it('сохраняет долю радиуса: наклон и уже отправленная тяга не разъезжаются', () => {
    const [x, y] = rescaleKnob(30, 40, 62.4, 56);
    expect(Math.hypot(x, y) / 56).toBeCloseTo(Math.hypot(30, 40) / 62.4);
  });

  it('без прежнего радиуса ничего не считает', () => {
    expect(rescaleKnob(3, 4, 0, 50)).toEqual([3, 4]);
    expect(rescaleKnob(3, 4, 50, 0)).toEqual([3, 4]);
  });
});
