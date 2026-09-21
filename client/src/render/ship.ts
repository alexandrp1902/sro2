import { Container, Graphics, Sprite, Texture } from 'pixi.js';
import { wrapAngle, type HullParams } from '../sim/movement';
import { flameShape, type FlamePalette } from './flame';
import { flameSprite, shipSprite, spriteSize, texture, type SpriteName } from './sprites';

/** Указатель желаемого направления (§26) — на таком расстоянии от центра, в размерах корпуса. */
const POINTER_DISTANCE = 2.6;
/** Дроны — учебные мишени: тот же корабль, но зеленоватый, чтобы не путать с пилотами. */
const DRONE_TINT = 0xa8dca0;
/** Пилот своей группы — чуть салатовый, в цвет группы. */
const ALLY_TINT = 0xd8ffb8;

/**
 * Выхлоп по роли (M15.6): у пиратов и рейнджеров холодный неон, у остальных горячее пламя.
 * Цвет теперь в самой геометрии, а не оттенком поверх рыжего: оттенок давал бурый, а не голубой.
 */
const FLAME_PALETTE: Partial<Record<ShipLook, FlamePalette>> = {
  pirate: 'neon',
  ranger: 'neon',
  wing: 'neon',
};

/**
 * Во сколько раз пламя вытягивается на полной тяге. Нарисованный кадр короткий — его тянем вдвое,
 * как и раньше. Факел из кода уже нарисован в свой рост, и вытягивать его вдвое было слишком:
 * у тяжёлого пирата получался язык длиннее корпуса.
 */
const DRAWN_FULL = 2;
const TORCH_FULL = 1;

/** Чей корабль: от этого картинка (у пиратов своя) и оттенок. */
export type ShipLook = 'own' | 'player' | 'drone' | 'pirate' | 'trader' | 'ranger' | 'convoy' | 'wing';

export class ShipView {
  readonly view = new Container();
  private readonly body = new Sprite();
  private readonly flame = new Sprite();
  /** Пламя, нарисованное кодом (M15.6): у кого есть свой кадр, у того этот узел спрятан. */
  private readonly torch = new Graphics();
  private readonly hull = new Container();
  private readonly pointer = new Graphics();
  private size = 0;
  private sprite: SpriteName | null = null;
  private ally = false;
  /** Пламя этого корабля — нарисованный кадр, а не факел из кода. Ставится на смену корабля. */
  private drawn = false;

  constructor(private readonly look: ShipLook) {
    // Оба пламени — за корпусом; работает всегда ровно одно из них.
    this.hull.addChild(this.torch, this.flame, this.body);
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

    // Пламя растёт из сопел назад: длина и яркость — ступень тяги, чуть дрожит.
    // Узел один из двух: нарисованный кадр или факел из кода (M15.6) — какой именно, решено на смене корабля.
    // Раньше живой узел искали по visible, и погасший факел больше не возвращался: корабли без кадра
    // («Сокол», «Стриж», «Страж», весь NPC-флот) после первой остановки летали без огня совсем.
    const fire = this.drawn ? this.flame : this.torch;
    fire.visible = glow > 0;
    fire.scale.set(1, (this.drawn ? DRAWN_FULL : TORCH_FULL) * glow * (0.9 + Math.random() * 0.2));
    fire.alpha = Math.min(1, 0.45 + glow);

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
    // Свой кадр пламени есть только у трёх старых корпусов; всем остальным — и шести корпусам игрока,
    // и всем NPC — факел рисуется кодом (M15.6). Геометрия строится здесь, один раз на смену корабля.
    const flame = flameSprite(sprite);
    this.drawn = flame !== null;
    this.flame.texture = flame ? texture(flame) : Texture.EMPTY;
    // Пламя масштабируется от линии сопел: там его опорная точка.
    this.flame.anchor.set(0.5, body / h);
    this.flame.position.set(0, body / 2);
    // Лишний узел гасим здесь же: дальше в кадре трогаем только живой.
    this.flame.visible = false;
    this.torch.visible = false;
    this.torch.clear();
    if (!this.drawn) {
      const { w } = spriteSize(sprite);
      const palette = FLAME_PALETTE[this.look] ?? 'hot';
      for (const layer of flameShape(w, body, palette)) this.torch.poly(layer.points).fill({ color: layer.color, alpha: layer.alpha });
      // Та же опорная точка, что у кадра: линия сопел, и растём назад.
      this.torch.position.set(0, body / 2);
    }
    this.hull.scale.set((size * 2) / body);
  }
}
