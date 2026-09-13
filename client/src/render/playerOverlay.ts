import { Container, Graphics, Text } from 'pixi.js';
import type { RemoteShipInfo } from '../net/remoteShips';
import type { Camera } from './camera';

const COLOR = 0xffd6a0;
const OUTLINE = 0x05060a;
/** Стрелка к кораблю за краем экрана держится на таком отступе от края, px. */
const EDGE_MARGIN = 26;
/** Подпись у стрелки — ближе к центру экрана на столько, px. */
const ARROW_LABEL_OFFSET = 24;
/** Ник над кораблём — на столько выше корпуса, px. */
const LABEL_GAP = 6;
const LABEL_PADDING = 4;
const LOST_SUFFIX = ' · нет связи';
const LOST_LABEL_ALPHA = 0.7;

interface Marker {
  label: Text;
  arrow: Graphics;
  text: string;
  seen: boolean;
}

/**
 * Ники над чужими кораблями и стрелки у края экрана к тем, кто за его пределами.
 * Слой в экранных координатах: размер текста не зависит от зума.
 */
export class PlayerOverlay {
  readonly view = new Container();
  private readonly markers = new Map<number, Marker>();

  update(ships: Iterable<RemoteShipInfo>, camera: Camera, width: number, height: number): void {
    for (const marker of this.markers.values()) marker.seen = false;
    const cx = width / 2;
    const cy = height / 2;

    for (const ship of ships) {
      const marker = this.marker(ship.id);
      marker.seen = true;
      const text = ship.online ? ship.name : ship.name + LOST_SUFFIX;
      if (text !== marker.text) {
        marker.label.text = text;
        marker.text = text;
      }
      const alpha = ship.online ? ship.alpha : LOST_LABEL_ALPHA;
      marker.label.alpha = marker.arrow.alpha = alpha;

      const sx = cx + (ship.x - camera.x) * camera.zoom;
      const sy = cy + (ship.y - camera.y) * camera.zoom;
      const r = ship.size * camera.zoom;
      const onScreen = sx > -r && sx < width + r && sy > -r && sy < height + r;
      marker.arrow.visible = !onScreen;

      if (onScreen) {
        marker.label.anchor.set(0.5, 1);
        marker.label.position.set(sx, sy - r - LABEL_GAP);
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
      this.markers.delete(id);
    }
  }

  private marker(id: number): Marker {
    let marker = this.markers.get(id);
    if (!marker) {
      const label = new Text({
        text: '',
        style: {
          fill: COLOR,
          fontSize: 12,
          fontFamily: 'system-ui, -apple-system, "Segoe UI", sans-serif',
          stroke: { color: OUTLINE, width: 3 },
        },
      });
      const arrow = new Graphics().poly([0, -9, 7, 6, -7, 6]).fill(COLOR).stroke({ width: 1.5, color: OUTLINE });
      this.view.addChild(arrow, label);
      marker = { label, arrow, text: '', seen: true };
      this.markers.set(id, marker);
    }
    return marker;
  }
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}
