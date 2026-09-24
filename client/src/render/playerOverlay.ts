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
  trader: 0xf2d46b,
  ranger: 0x7fe8d0,
  // Корабли заданий (M14) — тех же цветов, что их родня: конвой читается как торговец, звено как рейнджеры.
  convoy: 0xf2d46b,
  wing: 0x7fe8d0,
  // Повстанцы (M20a) — ржавый оранжевый: враждебны, но это не пиратский алый.
  rebel: 0xe08a4a,
  // Корпорация (M20b) — холодная сталь: официальные корабли, и по цвету это видно раньше, чем по имени.
  corp: 0xa9b7d0,
};
const OUTLINE = 0x05060a;
/** Своя группа (GDD §37) — салатовый: не путается ни с пилотами (персик), ни с рейнджерами (бирюза). */
export const PARTY_COLOR = 0xb6ff6a;
/** Выбранная цель: оранжевый контур вокруг её стрелки у края экрана и оранжевая подпись. */
const TARGET_COLOR = 0xffa53a;
/** Выбранный предмет: голубой — не путается ни с целью (оранжевая), ни со своим кораблём. */
const LOOT_COLOR = 0x6fd3ff;
/** Выбранная станция — зелёная, как круг дока. */
const STATION_COLOR = 0x6fe08a;
/** Метеорит: рыжий, как его прожилки. */
const METEOR_COLOR = 0xd9a066;
/** Цель задания или шага обучения — золотая, как трекер цели. */
export const OBJECTIVE_COLOR = 0xffd166;
/** Золотой уголок висит над целью на столько пикселей выше её края — над ником и полосками корабля. */
const OBJECTIVE_LIFT = 40;
/** Пират, который целится в меня: знак перед ником и крупная стрелка у края экрана. */
const THREAT_PREFIX = '! ';
/** Состояние ИИ под ником — при открытой dev-панели, для настройки пиратов на плейтесте. */
const AI_TEXT: Record<AiState, string> = {
  patrol: 'патруль',
  attack: 'атака',
  return: 'домой',
  leave: 'уходит',
};
/** Стрелка к кораблю за краем экрана держится на таком отступе от края, px. */
const EDGE_MARGIN = 26;
/**
 * Стрелка к ресурсу за краем экрана (M15.7): груз и камни показываются все, что попали на радар, —
 * иначе на телефоне, где экран много уже радара, добычу приходится искать вслепую. Чтобы край не
 * превратился в частокол, невыбранная стрелка мельче и бледнее выбранной, и подписи у неё нет.
 */
const RESOURCE_ARROW_SCALE = 0.8;
const RESOURCE_ARROW_ALPHA = 0.75;
/** Больше этого стрелок к грузу не рисуем: после боя его бывает и два десятка. Берём ближние. */
const MAX_LOOT_ARROWS = 12;
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
/**
 * У выбранной цели полоски крупнее (M16c): на телефоне карточки цели больше нет, и здоровье противника
 * читают прямо над ним.
 */
const TARGET_BAR_WIDTH = 56;
const TARGET_BAR_HEIGHT = 5;
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

/** Метеорит: рамка цели, полоска прочности и стрелка у края — только выбранному или побитому. */
export interface MeteorMark {
  id: number;
  x: number;
  y: number;
  size: number;
  name: string;
  hp: number;
  maxHp: number;
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
  /** Весь груз в радаре: каждому за краем экрана — своя мелкая стрелка (M15.7). */
  lootAll: Iterable<LootMark & { id: number }>;
  /** id выбранного предмета: у него стрелка своя, крупная и с подписью; 0 — ничего не выбрано. */
  lootId: number;
  /** Выбранная станция: в неё нельзя стрелять, прицел на ней — чтобы пристыковаться. */
  station: LootMark | null;
  /** Сколько единиц мира в одном секторе: у стрелки за краем экрана пишем дистанцию в них. */
  sectorUnit: number;
  /** Цель задания или обучения: куда лететь; id — если это корабль (у него своя стрелка у края). null — нет. */
  objective?: (LootMark & { id?: number }) | null;
  /** Своя группа: их подписи и стрелки — цветом группы. */
  party?: ReadonlySet<number>;
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
  private readonly lootArrow = arrowTo(LOOT_COLOR);
  /** Мелкие стрелки к остальному грузу за краем экрана, по id предмета. */
  private readonly lootArrows = new Map<number, Graphics>();
  /** id выбранного предмета из последнего кадра — чтобы отдать его тапу по крупной стрелке. */
  private lootId = 0;
  /** Рамка выбранной станции и стрелка к ней за краем экрана. */
  private readonly stationFrame = new Graphics();
  private readonly stationArrow = arrowTo(STATION_COLOR);
  /** Цель задания: уголок над ней на экране и стрелка у края, когда она за экраном. */
  private readonly objectiveMark = new Graphics()
    .poly([-8, -5, 8, -5, 0, 5])
    .fill(OBJECTIVE_COLOR)
    .stroke({ width: 1.5, color: OUTLINE, join: 'round' });
  private readonly objectiveArrow = new Graphics()
    .poly([0, -12, 9, 8, -9, 8])
    .fill(OBJECTIVE_COLOR)
    .stroke({ width: 1.5, color: OUTLINE, join: 'round' });
  /** Корабль-цель за краем: его собственная стрелка в золотом контуре — вторая стрелка легла бы на его подпись. */
  private readonly objectiveOutline = new Graphics()
    .poly([0, -17, 14, 12, -14, 12])
    .stroke({ width: 2.5, color: OBJECTIVE_COLOR, join: 'round' });

  constructor() {
    this.view.addChild(
      this.ownBubble,
      this.frame,
      this.targetArrow,
      this.lootFrame,
      this.lootArrow,
      this.stationFrame,
      this.stationArrow,
      this.objectiveMark,
      this.objectiveArrow,
      this.objectiveOutline,
    );
  }

  update(frame: OverlayFrame): void {
    const { ships, camera, width, height, target, own, ownId, showAi } = frame;
    for (const marker of this.markers.values()) marker.seen = false;
    const cx = width / 2;
    const cy = height / 2;
    this.frame.visible = false;
    this.targetArrow.visible = false;

    for (const ship of ships) {
      const marker = this.marker(ship.id, frame.party?.has(ship.id) ? PARTY_COLOR : COLORS[ship.kind]);
      marker.seen = true;
      // Целятся в меня: пират, рейнджер (я обидел торговца) или сам торговец, которому я не дал уйти.
      const threat = ship.kind !== 'player' && ship.kind !== 'drone' && ship.targetId === ownId;
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
        const bars = this.drawBars(marker, ship, isTarget);
        const barsTop = sy - r - LABEL_GAP - bars.height;
        marker.bars.position.set(sx - bars.width / 2, barsTop);
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
      this.drop(marker);
      this.markers.delete(id);
    }

    this.drawMark(this.lootFrame, this.lootArrow, frame.loot, LOOT_COLOR, camera, cx, cy, width, height);
    this.drawLootArrows(frame, cx, cy);
    this.drawMark(this.stationFrame, this.stationArrow, frame.station, STATION_COLOR, camera, cx, cy, width, height);
    this.drawObjective(frame.objective ?? null, camera, cx, cy, width, height);

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

  private drop(marker: Marker): void {
    marker.label.destroy();
    marker.arrow.destroy();
    marker.bars.destroy();
    marker.bubble.destroy();
  }

  /** Стрелки у края экрана из последнего update — по ним и по их подписям выбирают цель или предмет. */
  *edgeArrows(): Iterable<EdgeArrow> {
    for (const [id, marker] of this.markers) {
      if (!marker.seen || !marker.arrow.visible) continue;
      const { label, arrow } = marker;
      const rect = marker.label.visible
        ? { x: label.x - label.width / 2, y: label.y - label.height / 2, width: label.width, height: label.height }
        : EMPTY_LABEL;
      yield { id, x: arrow.x, y: arrow.y, kind: 'ship', label: rect };
    }
    // Стрелка к грузу ведёт в выбранный предмет, а не в прицел: тапают по ней ради подбора.
    if (this.lootArrow.visible && this.lootId !== 0) {
      yield { id: this.lootId, x: this.lootArrow.x, y: this.lootArrow.y, kind: 'loot', label: EMPTY_LABEL };
    }
    for (const [id, arrow] of this.lootArrows) {
      if (!arrow.visible) continue;
      yield { id, x: arrow.x, y: arrow.y, kind: 'loot', label: EMPTY_LABEL };
    }
  }

  /**
   * Мелкие стрелки ко всему остальному грузу за краем экрана. Выбранный предмет сюда не попадает —
   * у него своя крупная стрелка с подписью. Дальние отбрасываются: край экрана не резиновый.
   */
  private drawLootArrows(frame: OverlayFrame, cx: number, cy: number): void {
    const { camera, width, height } = frame;
    this.lootId = frame.lootId;
    const offscreen: { id: number; x: number; y: number; distance: number }[] = [];
    for (const mark of frame.lootAll) {
      if (mark.id === frame.lootId) continue;
      const sx = cx + (mark.x - camera.x) * camera.zoom;
      const sy = cy + (mark.y - camera.y) * camera.zoom;
      const r = mark.size * camera.zoom;
      if (sx > -r && sx < width + r && sy > -r && sy < height + r) continue;
      offscreen.push({ id: mark.id, x: sx, y: sy, distance: Math.hypot(sx - cx, sy - cy) });
    }
    if (offscreen.length > MAX_LOOT_ARROWS) {
      offscreen.sort((a, b) => a.distance - b.distance);
      offscreen.length = MAX_LOOT_ARROWS;
    }

    for (const arrow of this.lootArrows.values()) arrow.visible = false;
    for (const item of offscreen) {
      let arrow = this.lootArrows.get(item.id);
      if (!arrow) {
        arrow = arrowTo(LOOT_COLOR);
        arrow.scale.set(RESOURCE_ARROW_SCALE);
        arrow.alpha = RESOURCE_ARROW_ALPHA;
        this.view.addChild(arrow);
        this.lootArrows.set(item.id, arrow);
      }
      const { x, y } = edgePoint(cx, cy, item.x - cx, item.y - cy);
      arrow.position.set(x, y);
      arrow.rotation = Math.atan2(item.x - cx, -(item.y - cy));
      arrow.visible = true;
    }

    // Предмет подобрали или он истёк — стрелка к нему больше не нужна.
    for (const [id, arrow] of this.lootArrows) {
      if (arrow.visible) continue;
      arrow.destroy();
      this.lootArrows.delete(id);
    }
  }

  /**
   * Метеорит. На экране — рамка, если выбран, и полоска, если побит. За краем — мелкая стрелка каждому
   * камню в радаре (M15.7), у выбранного она крупнее и с подписью. О будущем таране никто не
   * предупреждает — смотрите сами, но теперь видно, с какой стороны камень идёт.
   */
  private updateMeteor(meteor: MeteorMark, frame: OverlayFrame, cx: number, cy: number): void {
    const { camera, width, height, target, own } = frame;
    const sx = cx + (meteor.x - camera.x) * camera.zoom;
    const sy = cy + (meteor.y - camera.y) * camera.zoom;
    const r = meteor.size * camera.zoom;
    const onScreen = sx > -r && sx < width + r && sy > -r && sy < height + r;
    const isTarget = target?.id === meteor.id;
    const damaged = meteor.hp < meteor.maxHp;
    // На экране целый и невыбранный камень ничем не помечается: он и так виден.
    if (onScreen && !isTarget && !damaged) return;

    const marker = this.marker(meteor.id, METEOR_COLOR);
    marker.seen = true;
    if (isTarget !== marker.targeted) {
      marker.targeted = isTarget;
      marker.label.style.fill = isTarget ? TARGET_COLOR : marker.color;
    }

    // Подпись только у выбранного: у края экрана камней бывает несколько, и текст забил бы его.
    // Скрытую не пересчитываем — иначе каждый кадр перерисовывался бы текст десятка камней.
    marker.label.visible = isTarget;
    if (isTarget) {
      let text = meteor.name;
      if (!onScreen && own) text += ` · ${formatSectors(Math.hypot(meteor.x - own.x, meteor.y - own.y), frame.sectorUnit)}с`;
      if (text !== marker.text) {
        marker.label.text = text;
        marker.text = text;
      }
    }
    marker.label.alpha = marker.bars.alpha = 1;
    marker.arrow.alpha = isTarget ? 1 : RESOURCE_ARROW_ALPHA;
    marker.arrow.visible = !onScreen;
    marker.bars.visible = onScreen && (isTarget || damaged);
    marker.bubble.visible = false;

    if (onScreen) {
      const bars = marker.bars.visible
        ? this.drawBars(marker, { hp: meteor.hp, maxHp: meteor.maxHp, sh: 0, maxSh: 0 }, isTarget)
        : { width: BAR_WIDTH, height: 0 };
      const barsTop = sy - r - LABEL_GAP - bars.height;
      marker.bars.position.set(sx - bars.width / 2, barsTop);
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
    marker.arrow.scale.set(isTarget ? 1.4 : RESOURCE_ARROW_SCALE);
    if (!isTarget) return;
    this.targetArrow.visible = true;
    this.targetArrow.position.set(ax, ay);
    this.targetArrow.rotation = marker.arrow.rotation;
    this.targetArrow.scale.set(1.4);
    const length = Math.hypot(dx, dy);
    const label = marker.label;
    label.anchor.set(0.5);
    label.position.set(
      clamp(ax - (dx / length) * ARROW_LABEL_OFFSET, label.width / 2 + LABEL_PADDING, width - label.width / 2 - LABEL_PADDING),
      clamp(ay - (dy / length) * ARROW_LABEL_OFFSET, label.height / 2 + LABEL_PADDING, height - label.height / 2 - LABEL_PADDING),
    );
  }

  /**
   * Полоски корпуса и щита над корпусом. У выбранной цели они шире и толще — это единственное место,
   * где на телефоне видно здоровье противника.
   *
   * @returns ширина и высота полосок, px
   */
  private drawBars(marker: Marker, ship: Bars, big = false): { width: number; height: number } {
    const hull = share(ship.hp, ship.maxHp);
    const shield = ship.maxSh > 0 ? share(ship.sh, ship.maxSh) : -1;
    const rows = shield < 0 ? 1 : 2;
    const width = big ? TARGET_BAR_WIDTH : BAR_WIDTH;
    const height = big ? TARGET_BAR_HEIGHT : BAR_HEIGHT;
    const key = `${hull}|${shield}|${big}`;
    if (key !== marker.barsKey) {
      marker.barsKey = key;
      const g = marker.bars.clear();
      g.rect(-1, -1, width + 2, rows * (height + BAR_GAP) + 1).fill({ color: OUTLINE, alpha: 0.7 });
      g.rect(0, 0, width * hull, height).fill(HULL_BAR);
      if (shield >= 0) g.rect(0, height + BAR_GAP, width * shield, height).fill(SHIELD_BAR);
    }
    return { width, height: rows * (height + BAR_GAP) };
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
   * Выбранный предмет или станция: рамка вокруг, а за краем экрана — крупная стрелка с подписью.
   * Остальному грузу мелкие стрелки рисует drawLootArrows.
   */
  private drawMark(
    frameGraphics: Graphics,
    arrow: Graphics,
    mark: LootMark | null,
    color: number,
    camera: Camera,
    cx: number,
    cy: number,
    width: number,
    height: number,
  ): void {
    frameGraphics.visible = false;
    arrow.visible = false;
    if (!mark) return;

    const sx = cx + (mark.x - camera.x) * camera.zoom;
    const sy = cy + (mark.y - camera.y) * camera.zoom;
    const r = mark.size * camera.zoom;
    if (sx > -r && sx < width + r && sy > -r && sy < height + r) {
      this.drawFrame(frameGraphics, sx, sy, r, color);
      return;
    }
    const { x, y } = edgePoint(cx, cy, sx - cx, sy - cy);
    arrow.position.set(x, y);
    arrow.rotation = Math.atan2(sx - cx, -(sy - cy));
    arrow.visible = true;
  }

  /**
   * Цель задания: на экране — золотой уголок над ней (рамки выбора он не заслоняет),
   * за краем — золотая стрелка, крупнее стрелок выбора.
   */
  private drawObjective(
    mark: (LootMark & { id?: number }) | null,
    camera: Camera,
    cx: number,
    cy: number,
    width: number,
    height: number,
  ): void {
    this.objectiveMark.visible = false;
    this.objectiveArrow.visible = false;
    this.objectiveOutline.visible = false;
    if (!mark) return;
    const sx = cx + (mark.x - camera.x) * camera.zoom;
    const sy = cy + (mark.y - camera.y) * camera.zoom;
    const r = mark.size * camera.zoom;
    if (sx > -r && sx < width + r && sy > -r && sy < height + r) {
      // Покачивается, чтобы глаз цеплялся за него среди рамок и подписей.
      const bob = Math.sin(performance.now() / 250) * 3;
      this.objectiveMark.position.set(sx, Math.max(8, sy - r - OBJECTIVE_LIFT + bob));
      this.objectiveMark.visible = true;
      return;
    }
    const ship = mark.id !== undefined ? this.markers.get(mark.id) : undefined;
    if (ship?.seen && ship.arrow.visible) {
      this.objectiveOutline.position.copyFrom(ship.arrow.position);
      this.objectiveOutline.rotation = ship.arrow.rotation;
      this.objectiveOutline.visible = true;
      return;
    }
    const { x, y } = edgePoint(cx, cy, sx - cx, sy - cy);
    this.objectiveArrow.position.set(x, y);
    this.objectiveArrow.rotation = Math.atan2(sx - cx, -(sy - cy));
    this.objectiveArrow.visible = true;
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
    // Цвет сменился (вступил в группу или вышел) — стрелка залита старым: строим заново.
    if (marker && marker.color !== color) {
      this.drop(marker);
      this.markers.delete(id);
      marker = undefined;
    }
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

/** Подпись у стрелки, по которой нельзя попасть: её нет, тапают по самой стрелке. */
const EMPTY_LABEL = { x: 0, y: 0, width: 0, height: 0 } as const;

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

/** Стрелка у края экрана к выбранному предмету или станции. */
function arrowTo(color: number): Graphics {
  return new Graphics().poly([0, -9, 7, 6, -7, 6]).fill(color).stroke({ width: 1.5, color: OUTLINE });
}
