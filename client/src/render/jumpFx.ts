import { Graphics } from 'pixi.js';
import { GATE_COLOR } from './world';

/** Корабль, который готовит гиперпрыжок. */
export interface Jumper {
  x: number;
  y: number;
  size: number;
  /** Тик, в который корабль уйдёт из системы. */
  jumpAt: number;
}

/**
 * Подготовка гиперпрыжка (GDD §5): вокруг корабля смыкается фиолетовое кольцо — видно и самому пилоту,
 * и тем, кто рядом: через секунды корабль исчезнет. Слой в мировых координатах.
 */
export class JumpFx {
  readonly view = new Graphics();

  /** @param jumpTicks сколько тиков длится подготовка */
  update(jumpers: Iterable<Jumper>, tick: number, jumpTicks: number, now: number): void {
    const g = this.view.clear();
    const pulse = 0.6 + 0.4 * Math.sin(now / 90);
    for (const ship of jumpers) {
      if (ship.jumpAt <= 0) continue;
      const left = Math.max(0, ship.jumpAt - tick);
      const progress = jumpTicks > 0 ? Math.min(1, 1 - left / jumpTicks) : 1;
      const r = ship.size * 2.4;
      g.circle(ship.x, ship.y, r).stroke({ width: 2, color: GATE_COLOR, alpha: 0.25 });
      if (progress > 0) {
        g.moveTo(ship.x, ship.y - r)
          .arc(ship.x, ship.y, r, -Math.PI / 2, -Math.PI / 2 + progress * 2 * Math.PI)
          .stroke({ width: 4, color: GATE_COLOR, alpha: 0.9 });
      }
      g.circle(ship.x, ship.y, r * (1.25 - 0.25 * progress)).stroke({ width: 1.5, color: GATE_COLOR, alpha: 0.4 * pulse });
    }
  }
}
