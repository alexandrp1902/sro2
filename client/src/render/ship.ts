import { Container, Graphics, Sprite } from 'pixi.js';
import { wrapAngle, type HullParams } from '../sim/movement';
import { neonExhaust } from './exhaust';
import { shipNozzles } from './shipNozzles';
import { shipSprite, spriteSize, texture, type SpriteName } from './sprites';

/** Указатель желаемого направления (§26) — на таком расстоянии от центра, в размерах корпуса. */
const POINTER_DISTANCE = 2.6;
/** Дроны — учебные мишени: тот же корабль, но зеленоватый, чтобы не путать с пилотами. */
const DRONE_TINT = 0xa8dca0;
/** Пилот своей группы — чуть салатовый, в цвет группы. */
const ALLY_TINT = 0xd8ffb8;

/** Чей корабль: от этого картинка (у пиратов своя) и оттенок. */
export type ShipLook = 'own' | 'player' | 'drone' | 'pirate' | 'trader' | 'ranger' | 'convoy' | 'wing' | 'rebel' | 'corp';

export class ShipView {
  readonly view = new Container();
  private readonly body = new Sprite();
  private readonly torch = new Container();
  private readonly hull = new Container();
  private readonly pointer = new Graphics();
  private size = 0;
  private sprite: SpriteName | null = null;
  private ally = false;
  constructor(private readonly look: ShipLook) {
    // Мягкий голубой свет за корпусом у всего флота.
    this.hull.addChild(this.torch, this.body);
    this.view.addChild(this.pointer, this.hull);
    // С M11 у дрона, торговца и рейнджера свои корабли — оттенок только подчёркивает их роль.
    if (look === 'drone') this.body.tint = DRONE_TINT;
    this.pointer
      .poly([-7, 5, 0, -3, 7, 5], false)
      .stroke({ width: 2.5, color: 0xcfe3ff, alpha: 0.9, cap: 'round', join: 'round' });
  }

  /** Пилот вступил в свою группу или вышел из неё: оттенок корабля — в цвет группы. */
  setAlly(ally: boolean): void {
    if (ally === this.ally || this.look !== 'player') return;
    this.ally = ally;
    this.body.tint = ally ? ALLY_TINT : 0xffffff;
  }

  /**
   * @param hullId корпус: от него картинка корабля
   * @param desired угол желаемого направления или null, если тяги нет
   */
  update(x: number, y: number, rot: number, hullId: string, hull: HullParams, glow: number, desired: number | null): void {
    const sprite = shipSprite(hullId, this.look === 'own' || this.look === 'player' ? null : this.look);
    if (sprite !== this.sprite || hull.size !== this.size) this.redraw(sprite, hull.size);
    this.view.position.set(x, y);
    this.hull.rotation = rot;

    const level = Math.max(0, Math.min(1, glow));
    this.torch.visible = level > 0;
    const pulse = 1 + 0.035 * Math.sin(performance.now() * 0.012 + x * 0.01);
    // Масштабируем огни вокруг собственных сопел, чтобы при смене тяги они не съезжали к центру.
    for (const nozzle of this.torch.children) {
      nozzle.scale.set(0.85 + level * 0.15, (0.35 + level * 0.65) * pulse);
    }
    this.torch.alpha = 0.4 + level * 0.6;

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
    for (const child of this.torch.removeChildren()) child.destroy({ children: true });
    const { w } = spriteSize(sprite);
    for (const [x, y, width] of shipNozzles(sprite)) {
      const nozzle = new Container();
      nozzle.position.set(x - w / 2, y - body / 2);
      const fire = neonExhaust(width * 1.65, Math.min(body * 0.6, width * 4));
      fire.anchor.y = 0.32;
      nozzle.addChild(fire);
      this.torch.addChild(nozzle);
    }
    this.torch.visible = false;
    this.hull.scale.set((size * 2) / body);
  }
}
