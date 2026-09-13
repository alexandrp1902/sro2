import { Container, Graphics, Text } from 'pixi.js';
import { BUOYS, PARKING, STATION } from '../game/layout';
import { WORLD_HALF_SIZE } from '../sim/movement';

const GRID_STEP = 200;
const GRID_MAJOR_EVERY = 5;

/** Сетка (по ней видны скорость и занос), граница мира и ориентиры для плейтеста. */
export function createWorldView(): Container {
  const view = new Container();
  const h = WORLD_HALF_SIZE;

  const grid = new Graphics();
  const major = new Graphics();
  for (let i = -h / GRID_STEP; i <= h / GRID_STEP; i++) {
    const g = i % GRID_MAJOR_EVERY === 0 ? major : grid;
    const v = i * GRID_STEP;
    g.moveTo(v, -h).lineTo(v, h).moveTo(-h, v).lineTo(h, v);
  }
  grid.stroke({ width: 1, color: 0x2a4468, alpha: 0.35 });
  major.stroke({ width: 1.5, color: 0x3a5a88, alpha: 0.5 });

  const border = new Graphics().rect(-h, -h, h * 2, h * 2).stroke({ width: 6, color: 0xe0524a, alpha: 0.7 });

  const station = new Graphics()
    .circle(STATION.x, STATION.y, STATION.radius)
    .fill({ color: 0x1b2b44 })
    .stroke({ width: 3, color: 0x6fa8ff })
    .circle(STATION.x, STATION.y, STATION.radius * 0.55)
    .stroke({ width: 2, color: 0x6fa8ff, alpha: 0.6 });

  const parking = new Graphics()
    .circle(PARKING.x, PARKING.y, PARKING.radius)
    .fill({ color: 0x4ae07a, alpha: 0.08 })
    .stroke({ width: 2, color: 0x4ae07a, alpha: 0.8 })
    .moveTo(PARKING.x - 8, PARKING.y)
    .lineTo(PARKING.x + 8, PARKING.y)
    .moveTo(PARKING.x, PARKING.y - 8)
    .lineTo(PARKING.x, PARKING.y + 8)
    .stroke({ width: 1.5, color: 0x4ae07a, alpha: 0.8 });

  const buoys = new Graphics();
  for (const b of BUOYS) {
    buoys.circle(b.x, b.y, 30).stroke({ width: 1, color: 0xffb347, alpha: 0.3 });
    buoys.circle(b.x, b.y, 8).fill({ color: 0xffb347 });
  }

  view.addChild(
    grid,
    major,
    border,
    station,
    parking,
    buoys,
    label('Станция', STATION.x, STATION.y),
    label('парковка', PARKING.x, PARKING.y + PARKING.radius + 14),
    label('слалом', BUOYS[0].x, BUOYS[0].y + 50),
  );
  return view;
}

function label(text: string, x: number, y: number): Text {
  const t = new Text({ text, style: { fill: 0x8fb4e8, fontSize: 14, fontFamily: 'system-ui, sans-serif' } });
  t.anchor.set(0.5);
  t.position.set(x, y);
  return t;
}
