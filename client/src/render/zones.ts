import { Container, Graphics, Text } from 'pixi.js';
import { STATION } from '../game/layout';
import { lairs, type NpcRules } from '../sim/npcs';

const SHELTER_COLOR = 0x6fa8ff;
const LAIR_COLOR = 0xff6b5a;
/** Кольцо укрытия — пунктир из стольких дуг. */
const DASHES = 72;
const LABEL_GAP = 18;

/**
 * Зоны на карте системы: кольцо укрытия у станции, куда пираты не залетают, и логова пиратов с составом.
 * Слой в мировых координатах, под кораблями; перестраивается, когда сервер присылает новый npcs.json.
 */
export class Zones {
  readonly view = new Container();
  private key = '';

  set(npcs: NpcRules | undefined): void {
    const key = JSON.stringify(npcs ?? null);
    if (key === this.key) return;
    this.key = key;
    for (const child of this.view.removeChildren()) child.destroy();
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
