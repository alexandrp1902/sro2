import { describe, expect, it } from 'vitest';
import { Controls } from './controls';
import { KeyboardControls, RELEASE_GRACE_MS } from './keyboard';

function setup() {
  const controls = new Controls();
  return { controls, keyboard: new KeyboardControls(controls) };
}

describe('KeyboardControls', () => {
  it('sets a screen direction and engages cruise throttle', () => {
    const { controls, keyboard } = setup();
    keyboard.keyDown('KeyD');
    expect([controls.dx, controls.dy]).toEqual([1, 0]);
    expect(controls.throttle).toBe(1);
  });

  it('combines keys into diagonals and cancels opposite ones (§21)', () => {
    const { controls, keyboard } = setup();
    keyboard.keyDown('KeyW');
    keyboard.keyDown('KeyD');
    expect(controls.dx).toBeCloseTo(Math.SQRT1_2);
    expect(controls.dy).toBeCloseTo(-Math.SQRT1_2);
    keyboard.keyDown('KeyS');
    expect([controls.dx, controls.dy]).toEqual([1, 0]);
  });

  it('keeps flying after keys are released (§25)', () => {
    const { controls, keyboard } = setup();
    keyboard.keyDown('ArrowLeft');
    keyboard.keyUp('ArrowLeft', 0);
    keyboard.update(1000);
    expect([controls.dx, controls.dy]).toEqual([-1, 0]);
    expect(controls.throttle).toBe(1);
  });

  it('keeps a diagonal when its keys are released a moment apart', () => {
    const { controls, keyboard } = setup();
    keyboard.keyDown('KeyW');
    keyboard.keyDown('KeyD');
    keyboard.keyUp('KeyD', 1000);
    keyboard.update(1000 + RELEASE_GRACE_MS / 2);
    keyboard.keyUp('KeyW', 1000 + RELEASE_GRACE_MS / 2);
    keyboard.update(2000);
    expect(controls.dx).toBeCloseTo(Math.SQRT1_2);
    expect(controls.dy).toBeCloseTo(-Math.SQRT1_2);
  });

  it('switches to the remaining key when it is held on purpose', () => {
    const { controls, keyboard } = setup();
    keyboard.keyDown('KeyW');
    keyboard.keyDown('KeyD');
    keyboard.keyUp('KeyD', 1000);
    keyboard.update(1000 + RELEASE_GRACE_MS);
    expect([controls.dx, controls.dy]).toEqual([0, -1]);
  });

  it('X stops but keeps the direction; the next direction key restores cruise (§24)', () => {
    const { controls, keyboard } = setup();
    keyboard.keyDown('Digit3');
    keyboard.keyDown('KeyS');
    keyboard.keyDown('KeyX');
    expect(controls.throttle).toBe(0);
    expect([controls.dx, controls.dy]).toEqual([0, 1]);
    keyboard.keyUp('KeyS', 0);
    keyboard.keyDown('KeyA');
    expect(controls.throttle).toBe(0.75);
  });

  it('number keys set 25/50/75/100% (§23)', () => {
    const { controls, keyboard } = setup();
    keyboard.keyDown('Digit1');
    expect(controls.throttle).toBe(0.25);
    keyboard.keyDown('Numpad4');
    expect(controls.throttle).toBe(1);
  });

  it('wheel steps throttle by 10% per notch and accumulates trackpad deltas', () => {
    const { controls, keyboard } = setup();
    keyboard.wheel(-100);
    keyboard.wheel(-100);
    expect(controls.throttle).toBeCloseTo(0.2);
    keyboard.wheel(100);
    expect(controls.throttle).toBeCloseTo(0.1);
    for (let i = 0; i < 3; i++) keyboard.wheel(-20);
    expect(controls.throttle).toBeCloseTo(0.2);
    for (let i = 0; i < 5; i++) keyboard.wheel(100);
    expect(controls.throttle).toBe(0);
  });
});
