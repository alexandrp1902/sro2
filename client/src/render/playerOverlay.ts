import { Container, Graphics, Text } from 'pixi.js';
import type { EdgeArrow } from '../game/targeting';
import type { AiState } from '../net/protocol';
import type { RemoteShipInfo, ShipKind } from '../net/remoteShips';
import { formatSectors, type AimState } from '../sim/combat';
import type { Camera } from './camera';

const COLORS: Record<ShipKind, number> = {
  player: 0xffd6a0,
  drone: 0xc3d6c2,
  pirate: 0xff8a7a,
};
const OUTLINE = 0x05060a;
/** Выбранная цель: оранжевый контур вокруг её стрелки у края экрана и оранжевая подпись. */
const TARGET_COLOR = 0xffa53a;
/** Выбранный предмет: голубой — не путается ни с целью (оранжевая), ни со своим кораблём. */
const LOOT_COLOR = 0x6fd3ff;
/** Метеорит: рыжий, как его прожилки; опасный — красный. */
const METEOR_COLOR = 0xd9a066;
const DANGER_COLOR = 0xff4a4a;
/** Пират, который целится в меня: знак перед ником и крупная стрелка у края экрана. */
const THREAT_PREFIX = '! ';
/** Состояние ИИ под ником — при открытой dev-панели, для настройки пиратов на плейтесте. */
const AI_TEXT: Record<AiState, string> = {
  patrol: 'патруль',
  attack: 'атака',
  return: 'домой',
};
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
  /** Цвет подписи по виду корабля; у выбранной цели подпись оранжевая. */
  color: number;
  /** Метеорит на опасном курсе: стрелка и подпись красные. */
  danger: boolean;
  targeted: boolean;
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

/** Выбранный предмет: рамка вокруг него, а за краем экрана — своя стрелка. */
export interface LootMark {
  x: number;
  y: number;
  size: number;
}

/** Метеорит: рамка цели, полоска прочности и стрелка у края — только выбранному, опасному или побитому. */
export interface MeteorMark {
  id: number;
  x: number;
  y: number;
  size: number;
  name: string;
  hp: number;
  maxHp: number;
  /** Секунд до тарана, если курс опасный; null — разойдёмся. */
  threat: number | null;
}

/** Полоски над объектом: корпус и, если есть, щит. */
interface Bars {
  hp: number;
  maxHp: number;
  sh: number;
  maxSh: number;
}

/** Всё, что оверлей рисует за кадр. Объект, а не список параметров: их уже девять. */
export interface OverlayFrame {
  ships: Iterable<RemoteShipInfo>;
  meteors: Iterable<MeteorMark>;
  camera: Camera;
  width: number;
  height: number;
  target: TargetMark | null;
  own: OwnMark | null;
  ownId: number;
  showAi: boolean;
  loot: LootMark | null;
  /** Сколько единиц мира в одном секторе: у стрелки за краем экрана пишем дистанцию в них. */
  sectorUnit: number;
}

/**
 * Ники, полоски корпуса и щита, рамка цели, пузыри защиты и стрелки у края экрана к кораблям за его пределами.
 * Слой в экранных координатах: размер не зависит от зума.
 */
export class PlayerOverlay {
  readonly view = new Container();
  private readonly markers = new Map<number, Marker>();
  private readonly frame = new Graphics();
  /** Контур под стрелкой выбранной цели: треугольник крупнее стрелки, поэтому виден кольцом вокруг неё. */
  private readonly targetArrow = new Graphics()
    .poly([0, -14, 11, 10, -11, 10])
    .stroke({ width: 3, color: TARGET_COLOR, join: 'round' });
  private readonly ownBubble = new Graphics();
  private ownBubbleRadius = 0;
  /** Рамка выбранного предмета и стрелка к нему, если он ушёл за край экрана. */
  private readonly lootFrame = new Graphics();
  private readonly lootArrow = new Graphics()
    .poly([0, -9, 7, 6, -7, 6])
    .fill(LOOT_COLOR)
    .stroke({ width: 1.5, color: OUTLINE });

  constructor() {
    this.view.addChild(this.ownBubble, this.frame, this.targetArrow, this.lootFrame, this.lootArrow);
  }

  update(frame: OverlayFrame): void {
    const { ships, camera, width, height, target, own, ownId, showAi } = frame;
    for (const marker of this.markers.values()) marker.seen = false;
    const cx = width / 2;
    const cy = height / 2;
    this.frame.visible = false;
    this.targetArrow.visible = false;

    for (const ship of ships) {
      const marker = this.marker(ship.id, COLORS[ship.kind]);
      marker.seen = true;
      const threat = ship.kind === 'pirate' && ship.targetId === ownId;
      const alpha = ship.online ? ship.alpha : LOST_LABEL_ALPHA;
      marker.label.alpha = marker.arrow.alpha = marker.bars.alpha = alpha;

      const sx = cx + (ship.x - camera.x) * camera.zoom;
      const sy = cy + (ship.y - camera.y) * camera.zoom;
      const r = ship.size * camera.zoom;
      const onScreen = sx > -r && sx < width + r && sy > -r && sy < height + r;
      const isTarget = target?.id === ship.id;

      let text = (threat ? THREAT_PREFIX : '') + (ship.online ? ship.name : ship.name + LOST_SUFFIX);
      if (showAi && ship.ai) text += ` · ${AI_TEXT[ship.ai]}`;
      // За краем экрана корабль не виден — значит нужна дистанция до него, иначе непонятно, далеко ли он.
      if (!onScreen && own) {
        text += ` · ${formatSectors(Math.hypot(ship.x - own.x, ship.y - own.y), frame.sectorUnit)}с`;
      }
      if (text !== marker.text) {
        marker.label.text = text;
        marker.text = text;
      }
      if (isTarget !== marker.targeted) {
        marker.targeted = isTarget;
        marker.label.style.fill = isTarget ? TARGET_COLOR : marker.color;
      }
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
        if (isTarget) this.drawFrame(this.frame, sx, sy, r, FRAME_COLORS[target.state]);
        continue;
      }

      // Точка на рамке экрана по лучу из центра к кораблю.
      const dx = sx - cx;
      const dy = sy - cy;
      const { x: ax, y: ay } = edgePoint(cx, cy, dx, dy);
      marker.arrow.position.set(ax, ay);
      marker.arrow.rotation = Math.atan2(dx, -dy);
      marker.arrow.scale.set(isTarget || threat ? 1.4 : 1); // цель или пират, который целится в меня, — стрелка крупнее
      if (isTarget) {
        this.targetArrow.visible = true;
        this.targetArrow.position.set(ax, ay);
        this.targetArrow.rotation = marker.arrow.rotation;
        this.targetArrow.scale.set(1.4);
      }

      const length = Math.hypot(dx, dy);
      const label = marker.label;
      label.anchor.set(0.5);
      label.position.set(
        clamp(ax - (dx / length) * ARROW_LABEL_OFFSET, label.width / 2 + LABEL_PADDING, width - label.width / 2 - LABEL_PADDING),
        clamp(ay - (dy / length) * ARROW_LABEL_OFFSET, label.height / 2 + LABEL_PADDING, height - label.height / 2 - LABEL_PADDING),
      );
    }

    for (const meteor of frame.meteors) this.updateMeteor(meteor, frame, cx, cy);

    for (const [id, marker] of this.markers) {
      if (marker.seen) continue;
      marker.label.destroy();
      marker.arrow.destroy();
      marker.bars.destroy();
      marker.bubble.destroy();
      this.markers.delete(id);
    }

    this.drawLoot(frame.loot, camera, cx, cy, width, height);

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

  /** Стрелки у края экрана из последнего update — по ним и по их подписям можно выбрать цель. */
  *edgeArrows(): Iterable<EdgeArrow> {
    for (const [id, marker] of this.markers) {
      if (!marker.seen || !marker.arrow.visible) continue;
      const { label, arrow } = marker;
      yield {
        id,
        x: arrow.x,
        y: arrow.y,
        label: { x: label.x - label.width / 2, y: label.y - label.height / 2, width: label.width, height: label.height },
      };
    }
  }

  /**
   * Метеорит. На экране — рамка, если выбран, полоска, если побит, и подпись с отсчётом, если летит в нас.
   * За краем — стрелка только выбранному и опасному: камней до десяти, стрелки ко всем забили бы края.
   */
  private updateMeteor(meteor: MeteorMark, frame: OverlayFrame, cx: number, cy: number): void {
    const { camera, width, height, target, own } = frame;
    const sx = cx + (meteor.x - camera.x) * camera.zoom;
    const sy = cy + (meteor.y - camera.y) * camera.zoom;
    const r = meteor.size * camera.zoom;
    const onScreen = sx > -r && sx < width + r && sy > -r && sy < height + r;
    const isTarget = target?.id === meteor.id;
    const danger = meteor.threat !== null;
    const damaged = meteor.hp < meteor.maxHp;
    if (onScreen ? !isTarget && !danger && !damaged : !isTarget && !danger) return;

    const marker = this.marker(meteor.id, METEOR_COLOR);
    marker.seen = true;
    if (danger !== marker.danger || isTarget !== marker.targeted) {
      marker.danger = danger;
      marker.targeted = isTarget;
      marker.color = danger ? DANGER_COLOR : METEOR_COLOR;
      marker.arrow.clear().poly([0, -9, 7, 6, -7, 6]).fill(marker.color).stroke({ width: 1.5, color: OUTLINE });
      marker.label.style.fill = isTarget && !danger ? TARGET_COLOR : marker.color;
    }

    let text = danger ? `${meteor.name.toUpperCase()} · ${meteor.threat!.toFixed(1)} с` : meteor.name;
    if (!onScreen && !danger && own) text += ` · ${formatSectors(Math.hypot(meteor.x - own.x, meteor.y - own.y), frame.sectorUnit)}с`;
    if (text !== marker.text) {
      marker.label.text = text;
      marker.text = text;
    }
    marker.label.alpha = marker.arrow.alpha = marker.bars.alpha = 1;
    marker.arrow.visible = !onScreen;
    marker.bars.visible = onScreen && (isTarget || damaged);
    marker.label.visible = !onScreen || isTarget || danger;
    marker.bubble.visible = false;

    if (onScreen) {
      const barsHeight = marker.bars.visible ? this.drawBars(marker, { hp: meteor.hp, maxHp: meteor.maxHp, sh: 0, maxSh: 0 }) : 0;
      const barsTop = sy - r - LABEL_GAP - barsHeight;
      marker.bars.position.set(sx - BAR_WIDTH / 2, barsTop);
      marker.label.anchor.set(0.5, 1);
      marker.label.position.set(sx, barsTop - 2);
      if (isTarget && target) this.drawFrame(this.frame, sx, sy, r, FRAME_COLORS[target.state]);
      return;
    }

    const dx = sx - cx;
    const dy = sy - cy;
    const { x: ax, y: ay } = edgePoint(cx, cy, dx, dy);
    marker.arrow.position.set(ax, ay);
    marker.arrow.rotation = Math.atan2(dx, -dy);
    marker.arrow.scale.set(1.4);
    if (isTarget) {
      this.targetArrow.visible = true;
      this.targetArrow.position.set(ax, ay);
      this.targetArrow.rotation = marker.arrow.rotation;
      this.targetArrow.scale.set(1.4);
    }
    const length = Math.hypot(dx, dy);
    const label = marker.label;
    label.anchor.set(0.5);
    label.position.set(
      clamp(ax - (dx / length) * ARROW_LABEL_OFFSET, label.width / 2 + LABEL_PADDING, width - label.width / 2 - LABEL_PADDING),
      clamp(ay - (dy / length) * ARROW_LABEL_OFFSET, label.height / 2 + LABEL_PADDING, height - label.height / 2 - LABEL_PADDING),
    );
  }

  /** @returns высота полосок, px */
  private drawBars(marker: Marker, ship: Bars): number {
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

  /**
   * Выбранный предмет: рамка вокруг него, а за краем экрана — одна стрелка.
   * Стрелок ко всем предметам нарочно нет: после боя их 5–10, они забили бы края и мешали выбору цели.
   */
  private drawLoot(loot: LootMark | null, camera: Camera, cx: number, cy: number, width: number, height: number): void {
    this.lootFrame.visible = false;
    this.lootArrow.visible = false;
    if (!loot) return;

    const sx = cx + (loot.x - camera.x) * camera.zoom;
    const sy = cy + (loot.y - camera.y) * camera.zoom;
    const r = loot.size * camera.zoom;
    if (sx > -r && sx < width + r && sy > -r && sy < height + r) {
      this.drawFrame(this.lootFrame, sx, sy, r, LOOT_COLOR);
      return;
    }
    const { x, y } = edgePoint(cx, cy, sx - cx, sy - cy);
    this.lootArrow.position.set(x, y);
    this.lootArrow.rotation = Math.atan2(sx - cx, -(sy - cy));
    this.lootArrow.visible = true;
  }

  /** Уголки вокруг цели. */
  private drawFrame(target: Graphics, sx: number, sy: number, r: number, color: number): void {
    const d = r + FRAME_PAD;
    const c = FRAME_CORNER;
    const g = target.clear();
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
    target.visible = true;
  }

  private marker(id: number, color: number): Marker {
    let marker = this.markers.get(id);
    if (!marker) {
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
      marker = {
        label,
        arrow,
        bars,
        bubble: bubbleView,
        color,
        danger: false,
        targeted: false,
        text: '',
        barsKey: '',
        bubbleRadius: 0,
        seen: true,
      };
      this.markers.set(id, marker);
    }
    return marker;
  }
}

/** Точка на рамке экрана по лучу из центра — там рисуется стрелка к тому, что за краем. */
function edgePoint(cx: number, cy: number, dx: number, dy: number): { x: number; y: number } {
  const t = Math.min((cx - EDGE_MARGIN) / Math.abs(dx), (cy - EDGE_MARGIN) / Math.abs(dy));
  return { x: cx + dx * t, y: cy + dy * t };
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
