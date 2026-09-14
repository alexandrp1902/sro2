import { Container, Graphics, Text } from 'pixi.js';
import type { RemoteShipInfo } from '../net/remoteShips';
import type { AimState } from '../sim/combat';
import type { Camera } from './camera';

const COLOR = 0xffd6a0;
const NPC_COLOR = 0xc3d6c2;
const OUTLINE = 0x05060a;
/** Стрелка к кораблю за краем экрана держится на таком отступе от края, px. */
const EDGE_MARGIN = 26;
/** Подпись у стрелки — ближе к центру экрана на столько, px. */
const ARROW_LABEL_OFFSET = 24;
/** Полоски и ник — на столько выше корпуса, px. */
const LABEL_GAP = 5;
const LABEL_PADDING = 4;
const LOST_SUFFIX = ' · нет связи';
const LOST_LABEL_ALPHA = 0.7;

/** Полоски корпуса и щита над кораблём, px. */
const BAR_WIDTH = 34;
const BAR_HEIGHT = 3;
const BAR_GAP = 1;
const HULL_BAR = 0xe0894a;
const SHIELD_BAR = 0x4a9be0;

/** Рамка цели: уголки на таком отступе от корпуса, px. */
const FRAME_PAD = 8;
const FRAME_CORNER = 8;
const FRAME_COLORS: Record<AimState, number> = {
  ready: 0x4ae07a,
  arc: 0xe0b04a,
  range: 0xe0b04a,
  protected: 0x9fd0ff,
  dead: 0x8a8f99,
};
const BUBBLE_COLOR = 0x9fd0ff;
/** Пузырь защиты шире корпуса на столько, px. */
const BUBBLE_PAD = 9;

interface Marker {
  label: Text;
  arrow: Graphics;
  bars: Graphics;
  bubble: Graphics;
  text: string;
  barsKey: string;
  bubbleRadius: number;
  seen: boolean;
}

/** Выбранная цель и можно ли по ней стрелять — от этого цвет рамки. */
export interface TargetMark {
  id: number;
  state: AimState;
}

/** Свой корабль: пузырь защиты после появления. */
export interface OwnMark {
  x: number;
  y: number;
  size: number;
  protected: boolean;
}

/**
 * Ники, полоски корпуса и щита, рамка цели, пузыри защиты и стрелки у края экрана к кораблям за его пределами.
 * Слой в экранных координатах: размер не зависит от зума.
 */
export class PlayerOverlay {
  readonly view = new Container();
  private readonly markers = new Map<number, Marker>();
  private readonly frame = new Graphics();
  private readonly ownBubble = new Graphics();
  private ownBubbleRadius = 0;

  constructor() {
    this.view.addChild(this.ownBubble, this.frame);
  }

  update(
    ships: Iterable<RemoteShipInfo>,
    camera: Camera,
    width: number,
    height: number,
    target: TargetMark | null,
    own: OwnMark | null,
  ): void {
    for (const marker of this.markers.values()) marker.seen = false;
    const cx = width / 2;
    const cy = height / 2;
    this.frame.visible = false;

    for (const ship of ships) {
      const marker = this.marker(ship);
      marker.seen = true;
      const text = ship.online ? ship.name : ship.name + LOST_SUFFIX;
      if (text !== marker.text) {
        marker.label.text = text;
        marker.text = text;
      }
      const alpha = ship.online ? ship.alpha : LOST_LABEL_ALPHA;
      marker.label.alpha = marker.arrow.alpha = marker.bars.alpha = alpha;

      const sx = cx + (ship.x - camera.x) * camera.zoom;
      const sy = cy + (ship.y - camera.y) * camera.zoom;
      const r = ship.size * camera.zoom;
      const onScreen = sx > -r && sx < width + r && sy > -r && sy < height + r;
      const isTarget = target?.id === ship.id;
      marker.arrow.visible = !onScreen;
      marker.bars.visible = onScreen;
      marker.bubble.visible = onScreen && ship.protected;

      if (onScreen) {
        const barsHeight = this.drawBars(marker, ship);
        const barsTop = sy - r - LABEL_GAP - barsHeight;
        marker.bars.position.set(sx - BAR_WIDTH / 2, barsTop);
        marker.label.anchor.set(0.5, 1);
        marker.label.position.set(sx, barsTop - 2);
        if (ship.protected) this.drawBubble(marker.bubble, sx, sy, r, marker);
        if (isTarget) this.drawFrame(sx, sy, r, FRAME_COLORS[target.state]);
        continue;
      }

      // Точка на рамке экрана по лучу из центра к кораблю.
      const dx = sx - cx;
      const dy = sy - cy;
      const t = Math.min((cx - EDGE_MARGIN) / Math.abs(dx), (cy - EDGE_MARGIN) / Math.abs(dy));
      const ax = cx + dx * t;
      const ay = cy + dy * t;
      marker.arrow.position.set(ax, ay);
      marker.arrow.rotation = Math.atan2(dx, -dy);
      marker.arrow.scale.set(isTarget ? 1.4 : 1); // цель за краем — стрелка крупнее

      const length = Math.hypot(dx, dy);
      const label = marker.label;
      label.anchor.set(0.5);
      label.position.set(
        clamp(ax - (dx / length) * ARROW_LABEL_OFFSET, label.width / 2 + LABEL_PADDING, width - label.width / 2 - LABEL_PADDING),
        clamp(ay - (dy / length) * ARROW_LABEL_OFFSET, label.height / 2 + LABEL_PADDING, height - label.height / 2 - LABEL_PADDING),
      );
    }

    for (const [id, marker] of this.markers) {
      if (marker.seen) continue;
      marker.label.destroy();
      marker.arrow.destroy();
      marker.bars.destroy();
      marker.bubble.destroy();
      this.markers.delete(id);
    }

    this.ownBubble.visible = own?.protected ?? false;
    if (own?.protected) {
      const radius = Math.round(own.size * camera.zoom + BUBBLE_PAD);
      if (radius !== this.ownBubbleRadius) {
        this.ownBubbleRadius = radius;
        bubble(this.ownBubble.clear(), radius);
      }
      this.ownBubble.position.set(cx + (own.x - camera.x) * camera.zoom, cy + (own.y - camera.y) * camera.zoom);
    }
  }

  /** @returns высота полосок, px */
  private drawBars(marker: Marker, ship: RemoteShipInfo): number {
    const hull = share(ship.hp, ship.maxHp);
    const shield = ship.maxSh > 0 ? share(ship.sh, ship.maxSh) : -1;
    const rows = shield < 0 ? 1 : 2;
    const key = `${hull}|${shield}`;
    if (key !== marker.barsKey) {
      marker.barsKey = key;
      const g = marker.bars.clear();
      g.rect(-1, -1, BAR_WIDTH + 2, rows * (BAR_HEIGHT + BAR_GAP) + 1).fill({ color: OUTLINE, alpha: 0.7 });
      g.rect(0, 0, BAR_WIDTH * hull, BAR_HEIGHT).fill(HULL_BAR);
      if (shield >= 0) g.rect(0, BAR_HEIGHT + BAR_GAP, BAR_WIDTH * shield, BAR_HEIGHT).fill(SHIELD_BAR);
    }
    return rows * (BAR_HEIGHT + BAR_GAP);
  }

  private drawBubble(g: Graphics, sx: number, sy: number, r: number, marker: Marker): void {
    const radius = Math.round(r + BUBBLE_PAD);
    if (radius !== marker.bubbleRadius) {
      marker.bubbleRadius = radius;
      bubble(g.clear(), radius);
    }
    g.position.set(sx, sy);
  }

  /** Уголки вокруг цели. */
  private drawFrame(sx: number, sy: number, r: number, color: number): void {
    const d = r + FRAME_PAD;
    const c = FRAME_CORNER;
    const g = this.frame.clear();
    for (const [kx, ky] of [
      [-1, -1],
      [1, -1],
      [1, 1],
      [-1, 1],
    ] as const) {
      const x = sx + kx * d;
      const y = sy + ky * d;
      g.moveTo(x - kx * c, y).lineTo(x, y).lineTo(x, y - ky * c);
    }
    g.stroke({ width: 2.5, color, alpha: 0.95, cap: 'round', join: 'round' });
    this.frame.visible = true;
  }

  private marker(ship: RemoteShipInfo): Marker {
    let marker = this.markers.get(ship.id);
    if (!marker) {
      const color = ship.npc ? NPC_COLOR : COLOR;
      const label = new Text({
        text: '',
        style: {
          fill: color,
          fontSize: 12,
          fontFamily: 'system-ui, -apple-system, "Segoe UI", sans-serif',
          stroke: { color: OUTLINE, width: 3 },
        },
      });
      const arrow = new Graphics().poly([0, -9, 7, 6, -7, 6]).fill(color).stroke({ width: 1.5, color: OUTLINE });
      const bars = new Graphics();
      const bubbleView = new Graphics();
      this.view.addChild(bubbleView, bars, arrow, label);
      marker = { label, arrow, bars, bubble: bubbleView, text: '', barsKey: '', bubbleRadius: 0, seen: true };
      this.markers.set(ship.id, marker);
    }
    return marker;
  }
}

function bubble(g: Graphics, radius: number): void {
  g.circle(0, 0, radius).fill({ color: BUBBLE_COLOR, alpha: 0.08 }).stroke({ width: 2, color: BUBBLE_COLOR, alpha: 0.55 });
}

/** Доля 0…1, округлённая до пикселя полоски — чтобы не перерисовывать полоски каждый кадр. */
function share(value: number, max: number): number {
  if (!(max > 0)) return 0;
  return Math.round(clamp(value / max, 0, 1) * BAR_WIDTH) / BAR_WIDTH;
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}
