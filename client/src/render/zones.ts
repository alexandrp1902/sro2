import { Container, Graphics, Text } from 'pixi.js';
import { STATION } from '../game/layout';
import type { LootRules } from '../sim/loot';
import { lairs, type NpcRules } from '../sim/npcs';

const SHELTER_COLOR = 0x6fa8ff;
const LAIR_COLOR = 0xff6b5a;
/** Круг сдачи груза у станции (GDD §21): зелёный — там с грузом делают хорошее. */
const UNLOAD_COLOR = 0x6fe08a;
/** Кольцо укрытия — пунктир из стольких дуг. */
const DASHES = 72;
const LABEL_GAP = 18;

/**
 * Зоны на карте системы: круг сдачи груза и кольцо укрытия у станции, логова пиратов с составом.
 * Слой в мировых координатах, под кораблями; перестраивается, когда сервер присылает новый баланс.
 */
export class Zones {
  readonly view = new Container();
  private key = '';

  set(npcs: NpcRules | undefined, loot?: LootRules): void {
    const key = JSON.stringify([npcs ?? null, loot?.stationUnload ? loot.stationRange : null]);
    if (key === this.key) return;
    this.key = key;
    for (const child of this.view.removeChildren()) child.destroy();

    // Круг сдачи меньше укрытия и рисуется сплошным: его ни с чем не спутать.
    if (loot?.stationUnload && loot.stationRange > 0) {
      const ur = loot.stationRange;
      const unload = new Graphics()
        .circle(STATION.x, STATION.y, ur)
        .fill({ color: UNLOAD_COLOR, alpha: 0.06 })
        .stroke({ width: 2, color: UNLOAD_COLOR, alpha: 0.45 });
      this.view.addChild(unload, label('сдача груза', STATION.x, STATION.y + ur + LABEL_GAP, UNLOAD_COLOR));
    }
    if (!npcs) return;

    const shelter = new Graphics();
    const r = npcs.stationSafeRadius;
    const step = (2 * Math.PI) / DASHES;
    for (let i = 0; i < DASHES; i++) {
      const a = i * step;
      shelter.moveTo(STATION.x + r * Math.cos(a), STATION.y + r * Math.sin(a)).arc(STATION.x, STATION.y, r, a, a + step * 0.55);
    }
    shelter.stroke({ width: 2, color: SHELTER_COLOR, alpha: 0.35 });
    this.view.addChild(shelter, label('укрытие: пираты не залетают', STATION.x, STATION.y - r - LABEL_GAP, SHELTER_COLOR));

    for (const lair of lairs(npcs)) {
      const g = new Graphics()
        .circle(lair.x, lair.y, lair.radius)
        .fill({ color: LAIR_COLOR, alpha: 0.05 })
        .stroke({ width: 2, color: LAIR_COLOR, alpha: 0.35 });
      this.view.addChild(g, label(lair.label, lair.x, lair.y - lair.radius - LABEL_GAP, LAIR_COLOR));
    }
  }
}

function label(text: string, x: number, y: number, color: number): Text {
  const t = new Text({ text, style: { fill: color, fontSize: 16, fontFamily: 'system-ui, sans-serif' } });
  t.alpha = 0.8;
  t.anchor.set(0.5);
  t.position.set(x, y);
  return t;
}
