import { describe, expect, it } from 'vitest';
import { DT, step, type HullParams, type ShipState } from '../sim/movement';
import { Controls } from './controls';
import { KeyboardControls } from './keyboard';

const LIGHT: HullParams = {
  name: 'Лёгкий',
  maxSpeed: 330,
  acceleration: 180,
  brakeAcceleration: 220,
  turnRate: 150,
  lateralDampTime: 0.65,
  lateralToForward: 0,
  size: 16,
  hp: 1500,
  shield: 500,
  shieldRegen: 20,
  evasion: 25,
  moveEvasion: 8,
  cargo: 20,
};

function setup() {
  const controls = new Controls();
  const keyboard = new KeyboardControls(controls);
  const ship: ShipState = { x: 0, y: 0, rot: 0, vx: 0, vy: 0 };
  /** Шаг игры: клавиатура выставляет управление, модель летит. */
  const tick = (steps = 1) => {
    for (let i = 0; i < steps; i++) {
      keyboard.apply(ship, LIGHT);
      step(ship, controls.input(), LIGHT, DT);
    }
  };
  return { controls, keyboard, ship, tick };
}

const speed = (s: ShipState) => Math.hypot(s.vx, s.vy);

describe('KeyboardControls (W — газ, S — тормоз, A/D — поворот)', () => {
  it('W accelerates along the nose', () => {
    const { controls, keyboard, ship, tick } = setup();
    keyboard.keyDown('KeyW');
    tick(20);
    expect(controls.throttle).toBe(1);
    expect(speed(ship)).toBeCloseTo(180, 6); // 1 с разгона
    expect(ship.vy).toBeLessThan(0); // нос вверх — летим вверх
  });

  it('releasing W keeps the reached speed (cruise)', () => {
    const { keyboard, ship, tick } = setup();
    keyboard.keyDown('ArrowUp');
    tick(20);
    keyboard.keyUp('ArrowUp');
    tick(40);
    expect(speed(ship)).toBeCloseTo(180, 6);
  });

  it('S brakes, and releasing it keeps the lower speed', () => {
    const { keyboard, ship, tick } = setup();
    keyboard.keyDown('KeyW');
    tick(20);
    keyboard.keyUp('KeyW');
    keyboard.keyDown('KeyS');
    tick(5); // 5 шагов тормоза: −220·0.25 = −55
    keyboard.keyUp('KeyS');
    tick(40);
    expect(speed(ship)).toBeCloseTo(125, 6);

    keyboard.keyDown('ArrowDown');
    tick(40);
    expect(speed(ship)).toBe(0);
  });

  it('D and A turn the hull at the full turn rate, even when standing still', () => {
    const { keyboard, ship, tick } = setup();
    keyboard.keyDown('KeyD');
    tick(4);
    expect(ship.rot).toBeCloseTo((4 * 150 * DT * Math.PI) / 180, 9);
    keyboard.keyUp('KeyD');
    keyboard.keyDown('ArrowLeft');
    tick(4);
    expect(ship.rot).toBeCloseTo(0, 9);
    expect(speed(ship)).toBe(0);
  });

  it('releasing a turn key freezes the heading', () => {
    const { keyboard, ship, tick } = setup();
    keyboard.keyDown('KeyD');
    tick(6);
    keyboard.keyUp('KeyD');
    tick(1);
    const heading = ship.rot;
    keyboard.keyDown('KeyW');
    tick(60);
    expect(ship.rot).toBeCloseTo(heading, 9);
    // Летим туда, куда смотрит нос.
    expect(Math.atan2(ship.vx, -ship.vy)).toBeCloseTo(heading, 6);
  });

  it('A and D together cancel out', () => {
    const { keyboard, ship, tick } = setup();
    keyboard.keyDown('KeyA');
    keyboard.keyDown('KeyD');
    tick(10);
    expect(ship.rot).toBe(0);
  });

  it('number keys, wheel and X set the throttle directly', () => {
    const { controls, keyboard } = setup();
    keyboard.keyDown('Digit2');
    expect(controls.throttle).toBe(0.5);
    keyboard.keyDown('Numpad4');
    expect(controls.throttle).toBe(1);
    keyboard.keyDown('KeyX');
    expect(controls.throttle).toBe(0);
    keyboard.wheel(-100);
    keyboard.wheel(-100);
    expect(controls.throttle).toBeCloseTo(0.2);
    for (let i = 0; i < 3; i++) keyboard.wheel(-20); // тачпад: мелкие дельты копятся
    expect(controls.throttle).toBeCloseTo(0.3);
  });

  it('does not override the stick while no movement key is touched', () => {
    const { controls, keyboard, ship } = setup();
    controls.setDirection(1, 0);
    controls.setThrottle(0.4);
    controls.source = 'stick';
    keyboard.apply(ship, LIGHT);
    expect(controls.input()).toEqual({ dx: 1, dy: 0, throttle: 0.4 });
    expect(controls.source).toBe('stick');
  });

  it('blur releases held keys', () => {
    const { controls, keyboard, tick } = setup();
    keyboard.keyDown('KeyW');
    tick(10);
    keyboard.blur();
    tick(1);
    expect(controls.throttle).toBeLessThan(1); // газ отпущен — держим скорость
  });
});
