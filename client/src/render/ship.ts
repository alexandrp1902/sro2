import { Container, Graphics, Sprite } from 'pixi.js';
import { localVelocity, wrapAngle, type HullParams, type ShipState } from '../sim/movement';
import { shipSprite, spriteSize, texture, type SpriteName } from './sprites';

/** Указатель желаемого направления (§26) — на таком расстоянии от центра, в размерах корпуса. */
const POINTER_DISTANCE = 2.6;
/** Дроны — учебные мишени: тот же корабль, но зеленоватый, чтобы не путать с пилотами. */
const DRONE_TINT = 0xa8dca0;

/** Чей корабль: от этого картинка (у пиратов своя) и оттенок. */
export type ShipLook = 'own' | 'player' | 'drone' | 'pirate';

/** Яркость пламени: разгон — полное, круиз — вполсилы, торможение и стоп — нет. */
export function engineGlow(state: ShipState, throttle: number, hull: HullParams): number {
  if (throttle <= 0) return 0;
  const { forward } = localVelocity(state);
  const accelerating = forward < hull.maxSpeed * throttle - 1;
  return accelerating ? 0.6 + 0.4 * throttle : 0.25 + 0.35 * throttle;
}

export class ShipView {
  readonly view = new Container();
  private readonly body = new Sprite();
  private readonly flame = new Sprite();
  private readonly hull = new Container();
  private readonly pointer = new Graphics();
  private size = 0;
  private sprite: SpriteName | null = null;

  constructor(private readonly look: ShipLook) {
    this.hull.addChild(this.flame, this.body);
    this.view.addChild(this.pointer, this.hull);
    if (look === 'drone') this.body.tint = DRONE_TINT;
    this.pointer
      .poly([-7, 5, 0, -3, 7, 5], false)
      .stroke({ width: 2.5, color: 0xcfe3ff, alpha: 0.9, cap: 'round', join: 'round' });
  }

  /**
   * @param hullId корпус: от него картинка корабля
   * @param desired угол желаемого направления или null, если тяги нет
   */
  update(x: number, y: number, rot: number, hullId: string, hull: HullParams, glow: number, desired: number | null): void {
    const sprite = shipSprite(hullId, this.look === 'pirate');
    if (sprite !== this.sprite || hull.size !== this.size) this.redraw(sprite, hull.size);
    this.view.position.set(x, y);
    this.hull.rotation = rot;

    // Пламя растёт из сопел назад: длина и яркость — сила тяги, чуть дрожит.
    this.flame.visible = glow > 0;
    this.flame.scale.set(1, (0.5 + 1.5 * glow) * (0.9 + Math.random() * 0.2));
    this.flame.alpha = Math.min(1, 0.45 + glow);

    this.pointer.visible = desired !== null;
    if (desired !== null) {
      const distance = this.size * POINTER_DISTANCE;
      this.pointer.position.set(Math.sin(desired) * distance, -Math.cos(desired) * distance);
      this.pointer.rotation = desired;
      // Ярче, пока корабль ещё разворачивается — особенно заметно у тяжёлых.
      this.pointer.alpha = 0.25 + 0.55 * Math.min(1, Math.abs(wrapAngle(desired - rot)) / (Math.PI / 2));
    }
  }

  /** Картинка носом вверх; длина корпуса без пламени — два «размера» корпуса, центр — середина корпуса. */
  private redraw(sprite: SpriteName, size: number): void {
    this.sprite = sprite;
    this.size = size;
    const { h, body = h } = spriteSize(sprite);
    this.body.texture = texture(sprite);
    this.body.anchor.set(0.5, body / 2 / h);
    this.flame.texture = texture(`${sprite}-flame`);
    // Пламя масштабируется от линии сопел: там его опорная точка.
    this.flame.anchor.set(0.5, body / h);
    this.flame.position.set(0, body / 2);
    this.hull.scale.set((size * 2) / body);
  }
}
