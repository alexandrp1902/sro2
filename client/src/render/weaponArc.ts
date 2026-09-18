import { Graphics } from 'pixi.js';
import type { WeaponParams } from '../sim/combat';

const READY_COLOR = 0x4ae07a;
const IDLE_COLOR = 0x8fb4e8;
/** Нос корабля при rot = 0 смотрит вверх — в координатах Pixi это угол −π/2. */
const NOSE = -Math.PI / 2;

/**
 * Сектор стрельбы (боевой документ §35) у своего корабля, пока выбрана цель: зелёный — цель в секторе и дальности,
 * голубой — нет. Дуга внутри — оптимальная дальность, дальше неё растёт штраф к шансу.
 * Пушка, бьющая во все стороны (arc 180), не рисуется вовсе: круг дальности загораживал космос,
 * а достаёт ли оружие — видно по дистанции в секторах в карточке цели.
 */
export class WeaponArc {
  readonly view = new Graphics();
  private key = '';

  /** @param weapon null — цели нет, сектор не показываем */
  update(x: number, y: number, rot: number, weapon: WeaponParams | null, ready: boolean): void {
    const allRound = weapon !== null && weapon.arc >= 180 - 1e-6;
    this.view.visible = weapon !== null && !allRound;
    if (!weapon || allRound) return;
    this.view.position.set(x, y);
    this.view.rotation = rot;
    const key = `${weapon.arc}|${weapon.optimalRange}|${weapon.maxRange}|${ready}`;
    if (key === this.key) return;
    this.key = key;
    this.redraw(weapon, ready);
  }

  private redraw(weapon: WeaponParams, ready: boolean): void {
    const color = ready ? READY_COLOR : IDLE_COLOR;
    const half = (weapon.arc * Math.PI) / 180;
    const from = NOSE - half;
    const to = NOSE + half;
    const max = weapon.maxRange;
    const optimal = weapon.optimalRange;
    this.view
      .clear()
      .moveTo(0, 0)
      .arc(0, 0, max, from, to)
      .closePath()
      .fill({ color, alpha: ready ? 0.07 : 0.04 })
      .moveTo(0, 0)
      .lineTo(Math.cos(from) * max, Math.sin(from) * max)
      .arc(0, 0, max, from, to)
      .lineTo(0, 0)
      .stroke({ width: 1.5, color, alpha: 0.35 })
      .moveTo(Math.cos(from) * optimal, Math.sin(from) * optimal)
      .arc(0, 0, optimal, from, to)
      .stroke({ width: 1, color, alpha: 0.25 });
  }
}
