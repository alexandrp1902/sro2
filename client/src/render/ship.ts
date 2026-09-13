import { Container, Graphics } from 'pixi.js';
import { localVelocity, wrapAngle, type HullParams, type ShipState } from '../sim/movement';

/** Силуэт носом вверх; «размер» корпуса — половина длины (18 у исходной формы). */
const SHAPE = [0, -18, 12, 14, 0, 8, -12, 14];
const SHAPE_SIZE = 18;
/** Указатель желаемого направления (§26) — на таком расстоянии от центра, в размерах корпуса. */
const POINTER_DISTANCE = 2.6;

/** Яркость пламени: разгон — полное, круиз — вполсилы, торможение и стоп — нет. */
export function engineGlow(state: ShipState, throttle: number, hull: HullParams): number {
  if (throttle <= 0) return 0;
  const { forward } = localVelocity(state);
  const accelerating = forward < hull.maxSpeed * throttle - 1;
  return accelerating ? 0.6 + 0.4 * throttle : 0.25 + 0.35 * throttle;
}

export class ShipView {
  readonly view = new Container();
  private readonly body = new Graphics();
  private readonly flame = new Graphics();
  private readonly hull = new Container();
  private readonly pointer = new Graphics();
  private size = 0;

  constructor(private readonly color: number) {
    this.hull.addChild(this.flame, this.body);
    this.view.addChild(this.pointer, this.hull);
    this.pointer
      .poly([-7, 5, 0, -3, 7, 5], false)
      .stroke({ width: 2.5, color: 0xcfe3ff, alpha: 0.9, cap: 'round', join: 'round' });
  }

  /** @param desired угол желаемого направления или null, если тяги нет */
  update(x: number, y: number, rot: number, hull: HullParams, glow: number, desired: number | null): void {
    if (hull.size !== this.size) this.redraw(hull.size);
    this.view.position.set(x, y);
    this.hull.rotation = rot;

    this.flame.visible = glow > 0;
    this.flame.scale.set(1, glow * (0.85 + Math.random() * 0.3));

    this.pointer.visible = desired !== null;
    if (desired !== null) {
      const distance = this.size * POINTER_DISTANCE;
      this.pointer.position.set(Math.sin(desired) * distance, -Math.cos(desired) * distance);
      this.pointer.rotation = desired;
      // Ярче, пока корабль ещё разворачивается — особенно заметно у тяжёлых.
      this.pointer.alpha = 0.25 + 0.55 * Math.min(1, Math.abs(wrapAngle(desired - rot)) / (Math.PI / 2));
    }
  }

  private redraw(size: number): void {
    this.size = size;
    const k = size / SHAPE_SIZE;
    this.body
      .clear()
      .poly(SHAPE.map((v) => v * k))
      .fill(this.color)
      .stroke({ width: 1.5, color: 0xffffff, alpha: 0.6 });
    // Пламя растёт из кормы вниз; масштаб по y — сила тяги.
    this.flame
      .clear()
      .poly([-5 * k, 0, 5 * k, 0, 0, 22 * k])
      .fill({ color: 0xffb347, alpha: 0.9 });
    this.flame.position.set(0, 9 * k);
  }
}
