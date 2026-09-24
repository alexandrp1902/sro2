import { afterEach, describe, expect, it, vi } from 'vitest';
import { Controls } from './controls';
import { Stick } from './stick';

/** Минимальная замена DOM: цель событий, которую можно дёрнуть руками (как в mouseButtons.test.ts). */
function target() {
  const handlers = new Map<string, ((e: unknown) => void)[]>();
  return {
    addEventListener(type: string, fn: (e: unknown) => void) {
      handlers.set(type, [...(handlers.get(type) ?? []), fn]);
    },
    fire(type: string, event: Record<string, unknown>) {
      for (const fn of handlers.get(type) ?? []) fn({ preventDefault: () => {}, type, ...event });
    },
  };
}

/** Основание и ручка: основание 200×200 в углу экрана, значит его центр — (100, 100). */
function node() {
  return {
    ...target(),
    style: { transform: '', setProperty: vi.fn() },
    getBoundingClientRect: () => ({ left: 0, top: 0, width: 200, height: 200 }),
  };
}

function setup() {
  const base = node();
  const handle = node();
  /** Кого стик забрал себе: этим же набором браузер отвечает на hasPointerCapture. */
  const captured = new Set<number>();
  const root = {
    ...target(),
    hidden: true,
    style: { setProperty: vi.fn() },
    querySelector: (selector: string) => (selector === '.stick-base' ? base : handle),
    setPointerCapture: (id: number) => void captured.add(id),
    hasPointerCapture: (id: number) => captured.has(id),
  };
  const win = { ...target(), innerWidth: 390, innerHeight: 844 };
  const doc = { ...target(), visibilityState: 'visible', documentElement: { style: { setProperty: vi.fn() } } };
  vi.stubGlobal('window', win);
  vi.stubGlobal('document', doc);
  vi.stubGlobal('matchMedia', () => ({ matches: true }));
  vi.stubGlobal('performance', { now: () => 0 });
  vi.stubGlobal('requestAnimationFrame', () => 0);
  const controls = new Controls();
  const stick = new Stick(root as unknown as HTMLElement, controls);
  return { root, win, doc, handle, captured, controls, stick };
}

/** Взять ручку и утянуть её вверх на 40 px — примерно две трети тяги. */
function pull(kit: ReturnType<typeof setup>, pointerId = 1): void {
  kit.root.fire('pointerdown', { pointerId, clientX: 100, clientY: 100, timeStamp: 0 });
  kit.win.fire('pointermove', { pointerId, clientX: 100, clientY: 60, timeStamp: 10 });
}

const knobY = (kit: ReturnType<typeof setup>): number =>
  Number(/translate\([^,]+, (-?[\d.]+)px\)/.exec(kit.handle.style.transform)![1]);

afterEach(() => vi.unstubAllGlobals());

describe('Stick', () => {
  it('отпускание над HUD ловится на окне — и стик берётся снова', () => {
    const kit = setup();
    pull(kit);
    const thrown = kit.controls.throttle;
    expect(thrown).toBeGreaterThan(0);
    // До M20c отпускание слушалось на самом стике: такое событие терялось, палец оставался зажатым,
    // и следующее касание ничего не давало — корабль улетал с этой тягой до перезагрузки.
    kit.win.fire('pointerup', { pointerId: 1, clientX: 100, clientY: 60, timeStamp: 20 });
    expect(kit.controls.throttle).toBe(thrown); // стик залипающий: тяга держится и без пальца
    kit.root.fire('pointerdown', { pointerId: 2, clientX: 100, clientY: 100, timeStamp: 30 });
    kit.win.fire('pointermove', { pointerId: 2, clientX: 100, clientY: 90, timeStamp: 40 });
    expect(kit.controls.throttle).toBeLessThan(thrown);
  });

  it('вкладка в фоне и потеря фокуса освобождают палец, но тягу не трогают', () => {
    for (const lose of [
      (kit: ReturnType<typeof setup>) => kit.win.fire('blur', {}),
      (kit: ReturnType<typeof setup>) => {
        kit.doc.visibilityState = 'hidden';
        kit.doc.fire('visibilitychange', {});
      },
    ]) {
      const kit = setup();
      pull(kit);
      const thrown = kit.controls.throttle;
      lose(kit);
      expect(kit.controls.throttle).toBe(thrown);
      kit.root.fire('pointerdown', { pointerId: 2, clientX: 100, clientY: 100, timeStamp: 30 });
      kit.win.fire('pointermove', { pointerId: 2, clientX: 100, clientY: 90, timeStamp: 40 });
      expect(kit.controls.throttle).toBeLessThan(thrown);
      vi.unstubAllGlobals();
    }
  });

  it('потерянный захват — признак, что пальца нет: стик отдаётся новому касанию', () => {
    const kit = setup();
    pull(kit);
    kit.captured.delete(1); // указатель завершился, захват браузер отпустил сам
    kit.root.fire('pointerdown', { pointerId: 2, clientX: 100, clientY: 100, timeStamp: 100 });
    kit.win.fire('pointermove', { pointerId: 2, clientX: 140, clientY: 100, timeStamp: 110 });
    expect(kit.controls.throttle).toBeGreaterThan(0);
    expect(kit.controls.dx).toBeCloseTo(1); // тянут вправо
  });

  it('смена размера экрана сохраняет долю наклона и не меняет отправленную тягу', () => {
    const kit = setup();
    pull(kit);
    const thrown = kit.controls.throttle;
    const before = knobY(kit);
    kit.win.innerWidth = 320;
    kit.win.innerHeight = 568;
    kit.win.fire('resize', {});
    expect(kit.controls.throttle).toBe(thrown);
    expect(knobY(kit)).toBeCloseTo((before * 56) / 62.4); // радиус 62.4 → 56
  });
});
