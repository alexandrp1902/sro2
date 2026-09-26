import { Application, Container, Graphics, Sprite, Text } from 'pixi.js';
import { type FxName, MechRig, fxTextures, mechTexture } from './rig';
import { MechField, PARTS, STEPS, alive, type MechPart, type MechUnitView } from './rules';

/** Клетка поля на экране при зуме 1 (план M21: 72 px). */
export const CELL_PX = 72;
/** Мех чуть больше клетки: иначе на телефоне не видно, куда он смотрит. */
const RIG_PX = CELL_PX * 1.8;
/** Опора меха ниже центра клетки: ноги стоят на клетке, а корпус заходит на ряд выше. */
const FOOT_Y = CELL_PX * 0.3;
const ZOOM_MIN = 0.4;
const ZOOM_MAX = 1.6;
/** Эффекты боя (пачка E): размер в клетках и длительность, мс. Взрыв крупнее и дольше — это конец меха. */
const FX: Record<FxName, { size: number; ms: number }> = {
  hit: { size: 1.1, ms: 360 },
  block: { size: 1.7, ms: 480 },
  explosion: { size: 2.4, ms: 900 },
};
/** Сдвиг пальца больше этого — перетаскивание поля, а не тап. */
const DRAG_SLOP = 8;

const COLOR = {
  player: 0x6f8aa6,
  enemy: 0xe0524a,
  enemyTint: 0xd08a84,
  reach: 0x6f8aa6,
  path: 0xd5dce6,
  target: 0xe0524a,
  selected: 0xd9c08a,
  ok: 0x7fd08f,
  blocked: 0x8b939e,
  text: 0xe4e6ea,
  gain: 0x7fd08f,
  block: 0xd5dce6,
  hit: 0xe0524a,
};

export interface SceneMarks {
  /** Куда можно дойти: клетка → шагов. */
  reach: ReadonlyMap<number, number>;
  /** Предпросмотр пути (индексы клеток без стартовой). */
  path: readonly number[] | null;
  /** Враги, по которым можно выстрелить отсюда. */
  targets: ReadonlySet<string>;
  /** Выбранная цель и видно ли её. */
  selected: string | null;
  line: { from: string; to: string; ok: boolean } | null;
  /** Чей сейчас ход — кольцо под ним. */
  current: string | null;
}

interface UnitView {
  holder: Container;
  base: Graphics;
  rig: MechRig;
  unit: MechUnitView;
}

export class MechScene {
  readonly root = new Container();
  onCell: ((x: number, y: number) => void) | null = null;
  onHover: ((x: number, y: number) => void) | null = null;

  private readonly ground = new Container();
  private readonly marks = new Graphics();
  private readonly objects = new Container();
  private readonly fx = new Container();
  private readonly units = new Map<string, UnitView>();
  private field = new MechField([]);
  private zoom = 1;
  private cx = 0;
  private cy = 0;
  private drag: { id: number; x: number; y: number; cx: number; cy: number; moved: boolean } | null = null;
  private readonly detach: Array<() => void> = [];

  constructor(private readonly app: Application) {
    this.objects.sortableChildren = true;
    this.root.addChild(this.ground, this.marks, this.objects, this.fx);
    app.stage.addChild(this.root);
    this.bindInput();
    const onResize = () => this.applyCamera();
    app.renderer.on('resize', onResize);
    this.detach.push(() => app.renderer.off('resize', onResize));
  }

  destroy(): void {
    for (const off of this.detach) off();
    this.root.destroy({ children: true });
  }

  // ------------------------------------------------------------------ поле

  setMap(map: readonly string[]): void {
    this.field = new MechField(map);
    this.ground.removeChildren().forEach((c) => c.destroy());
    for (const child of [...this.objects.children]) {
      if (!this.isUnit(child)) child.destroy();
    }
    const buildings = new Set<number>();
    for (let y = 0; y < this.field.height; y++) {
      for (let x = 0; x < this.field.width; x++) {
        const cell = this.field.cell(x, y);
        this.ground.addChild(this.tile(cell === ',' ? 'ground-2' : 'ground-1', x, y, 1));
        if (cell === 'r') this.ground.addChild(this.tile('rough', x, y, 1));
        if (cell === 'c' || cell === 'w') this.objects.addChild(this.tile(cell === 'c' ? 'crate' : 'wall', x, y, 1));
        if (cell === 'b' && !buildings.has(this.field.index(x, y))) {
          for (const [dx, dy] of [[0, 0], [1, 0], [0, 1], [1, 1]]) buildings.add(this.field.index(x + dx, y + dy));
          this.objects.addChild(this.tile('building', x, y, 2));
        }
      }
    }
    // Тень под непроходимым: стенд-ин тайлы стен низкие и не читаются как укрытие. Стена и здание
    // закрывают огонь — тень гуще; ящик и камни только мешают пройти — едва заметная.
    const shade = new Graphics();
    for (let y = 0; y < this.field.height; y++) {
      for (let x = 0; x < this.field.width; x++) {
        const cell = this.field.cell(x, y);
        if (cell === '.' || cell === ',') continue;
        const heavy = cell === 'w' || cell === 'b';
        shade.rect(x * CELL_PX, y * CELL_PX, CELL_PX, CELL_PX).fill({ color: 0x08090b, alpha: heavy ? 0.42 : 0.16 });
      }
    }
    this.ground.addChild(shade);
    // Сетка тонкой линией: на однотонном песке иначе не сосчитать клетки до цели.
    const grid = new Graphics();
    for (let x = 0; x <= this.field.width; x++) grid.moveTo(x * CELL_PX, 0).lineTo(x * CELL_PX, this.field.height * CELL_PX);
    for (let y = 0; y <= this.field.height; y++) grid.moveTo(0, y * CELL_PX).lineTo(this.field.width * CELL_PX, y * CELL_PX);
    grid.stroke({ width: 1, color: 0x08090b, alpha: 0.35 });
    this.ground.addChild(grid);
    this.applyCamera();
  }

  private tile(name: string, x: number, y: number, span: number): Sprite {
    const sprite = new Sprite(mechTexture(`tile-${name}`));
    sprite.position.set(x * CELL_PX, y * CELL_PX);
    sprite.width = CELL_PX * span;
    sprite.height = CELL_PX * span;
    // Препятствие рисуется в порядке рядов вместе с мехами: стоящий ниже его перекрывает.
    sprite.zIndex = (y + span) * CELL_PX - 1;
    return sprite;
  }

  private isUnit(child: Container): boolean {
    for (const u of this.units.values()) if (u.holder === child) return true;
    return false;
  }

  // ------------------------------------------------------------------ мехи

  setUnits(units: readonly MechUnitView[]): void {
    for (const unit of units) {
      let view = this.units.get(unit.id);
      if (!view) {
        const holder = new Container();
        const base = new Graphics();
        const rig = new MechRig(RIG_PX, unit.side === 'enemy' ? COLOR.enemyTint : 0xffffff);
        rig.view.position.set(0, FOOT_Y);
        holder.addChild(base, rig.view);
        this.objects.addChild(holder);
        view = { holder, base, rig, unit };
        this.units.set(unit.id, view);
      }
      view.unit = unit;
      this.place(view, unit.x, unit.y);
      this.pose(view, unit.dir);
      const dead = !alive(unit);
      view.holder.alpha = dead ? 0.35 : 1;
      view.base.visible = !dead;
    }
  }

  private place(view: UnitView, x: number, y: number): void {
    view.holder.position.set((x + 0.5) * CELL_PX, (y + 0.5) * CELL_PX);
    view.holder.zIndex = (y + 0.5) * CELL_PX + FOOT_Y;
  }

  private pose(view: UnitView, dir: number): void {
    const broken = new Set<MechPart>(PARTS.filter((_, i) => i > 0 && view.unit.max[i] > 0 && view.unit.hp[i] <= 0));
    view.rig.set(dir, broken);
    // Кольцо под мехом и стрелка взгляда: направление должно читаться и без картинки.
    const color = view.unit.side === 'enemy' ? COLOR.enemy : COLOR.player;
    const [fx, fy] = STEPS[dir];
    const len = Math.hypot(fx, fy);
    const ux = fx / len;
    const uy = fy / len;
    const r = CELL_PX * 0.36;
    const tip = r + 12;
    view.base
      .clear()
      .ellipse(0, FOOT_Y - 4, r, r * 0.55)
      .stroke({ width: 2, color, alpha: 0.9 })
      .poly([ux * tip, FOOT_Y - 4 + uy * tip * 0.55, ux * r - uy * 7, FOOT_Y - 4 + (uy * r + ux * 7) * 0.55, ux * r + uy * 7, FOOT_Y - 4 + (uy * r - ux * 7) * 0.55])
      .fill({ color, alpha: 0.95 });
  }

  // ------------------------------------------------------------------ подсветка

  setMarks(m: SceneMarks): void {
    const g = this.marks.clear();
    const w = this.field.width;
    const cellRect = (i: number, inset = 3) => g.rect((i % w) * CELL_PX + inset, Math.floor(i / w) * CELL_PX + inset, CELL_PX - 2 * inset, CELL_PX - 2 * inset);
    for (const cell of m.reach.keys()) cellRect(cell).fill({ color: COLOR.reach, alpha: 0.22 });
    if (m.reach.size > 0) for (const cell of m.reach.keys()) cellRect(cell).stroke({ width: 1, color: COLOR.reach, alpha: 0.55 });
    if (m.path) {
      for (const cell of m.path) {
        g.circle(((cell % w) + 0.5) * CELL_PX, (Math.floor(cell / w) + 0.5) * CELL_PX, 6).fill({ color: COLOR.path, alpha: 0.9 });
      }
      const last = m.path[m.path.length - 1];
      if (last !== undefined) cellRect(last, 2).stroke({ width: 3, color: COLOR.path, alpha: 0.95 });
    }
    for (const [id, view] of this.units) {
      if (!alive(view.unit)) continue;
      const i = this.field.index(view.unit.x, view.unit.y);
      if (id === m.selected) this.brackets(g, i, COLOR.selected, 4);
      else if (m.targets.has(id)) this.brackets(g, i, COLOR.target, 2);
      if (id === m.current) cellRect(i, 1).stroke({ width: 2, color: COLOR.selected, alpha: 0.7 });
    }
    if (m.line) {
      const a = this.units.get(m.line.from)?.unit;
      const b = this.units.get(m.line.to)?.unit;
      if (a && b) this.dashed(g, a, b, m.line.ok ? COLOR.selected : COLOR.blocked);
    }
  }

  private brackets(g: Graphics, cell: number, color: number, width: number): void {
    const w = this.field.width;
    const x = (cell % w) * CELL_PX;
    const y = Math.floor(cell / w) * CELL_PX;
    const s = CELL_PX;
    const k = 16;
    g.moveTo(x, y + k).lineTo(x, y).lineTo(x + k, y)
      .moveTo(x + s - k, y).lineTo(x + s, y).lineTo(x + s, y + k)
      .moveTo(x + s, y + s - k).lineTo(x + s, y + s).lineTo(x + s - k, y + s)
      .moveTo(x + k, y + s).lineTo(x, y + s).lineTo(x, y + s - k)
      .stroke({ width, color });
  }

  private dashed(g: Graphics, a: MechUnitView, b: MechUnitView, color: number): void {
    const ax = (a.x + 0.5) * CELL_PX;
    const ay = (a.y + 0.5) * CELL_PX;
    const bx = (b.x + 0.5) * CELL_PX;
    const by = (b.y + 0.5) * CELL_PX;
    const len = Math.hypot(bx - ax, by - ay);
    const steps = Math.floor(len / 14);
    for (let i = 0; i < steps; i += 2) {
      const t0 = i / steps;
      const t1 = Math.min(1, (i + 1) / steps);
      g.moveTo(ax + (bx - ax) * t0, ay + (by - ay) * t0).lineTo(ax + (bx - ax) * t1, ay + (by - ay) * t1);
    }
    g.stroke({ width: 2, color, alpha: 0.9 });
  }

  // ------------------------------------------------------------------ анимация

  private tween(ms: number, step: (k: number) => void): Promise<void> {
    return new Promise((resolve) => {
      let t = 0;
      const tick = () => {
        t += this.app.ticker.deltaMS;
        const k = Math.min(1, t / ms);
        step(k);
        if (k >= 1) {
          this.app.ticker.remove(tick);
          resolve();
        }
      };
      this.app.ticker.add(tick);
    });
  }

  /** Шаг за шагом по пути, поворачиваясь по ходу. */
  async walk(id: string, path: readonly number[], finalDir: number): Promise<void> {
    const view = this.units.get(id);
    if (!view) return;
    const w = this.field.width;
    let x = view.unit.x;
    let y = view.unit.y;
    for (const cell of path) {
      const nx = cell % w;
      const ny = Math.floor(cell / w);
      const dir = STEPS.findIndex(([dx, dy]) => dx === Math.sign(nx - x) && dy === Math.sign(ny - y));
      if (dir >= 0) this.pose(view, dir);
      const fx = x;
      const fy = y;
      await this.tween(150, (k) => this.place(view, fx + (nx - fx) * k, fy + (ny - fy) * k));
      x = nx;
      y = ny;
    }
    view.unit = { ...view.unit, x, y, dir: finalDir };
    this.pose(view, finalDir);
  }

  face(id: string, dir: number): void {
    const view = this.units.get(id);
    if (!view) return;
    view.unit = { ...view.unit, dir };
    this.pose(view, dir);
  }

  /** Трассер от дула к цели; miss — уходит мимо. */
  async tracer(from: string, to: string, miss: boolean): Promise<void> {
    const a = this.units.get(from);
    const b = this.units.get(to);
    if (!a || !b) return;
    const [mx, my] = a.rig.muzzle(a.unit.dir);
    const sx = a.holder.x + mx;
    const sy = a.holder.y + FOOT_Y + my;
    let tx = b.holder.x;
    let ty = b.holder.y - CELL_PX * 0.2;
    if (miss) {
      tx += (Math.random() - 0.5) * CELL_PX;
      ty += (Math.random() - 0.5) * CELL_PX;
    }
    const line = new Graphics();
    this.fx.addChild(line);
    await this.tween(160, (k) => {
      const hx = sx + (tx - sx) * k;
      const hy = sy + (ty - sy) * k;
      const tail = Math.max(0, k - 0.35);
      line.clear()
        .moveTo(sx + (tx - sx) * tail, sy + (ty - sy) * tail)
        .lineTo(hx, hy)
        .stroke({ width: 3, color: 0xffd58a, alpha: 0.95 });
    });
    line.destroy();
  }

  /** Всплывающая надпись над мехом: урон, блок, промах. */
  float(id: string, text: string, kind: 'hit' | 'block' | 'miss' | 'info'): Promise<void> {
    const view = this.units.get(id);
    if (!view) return Promise.resolve();
    const color = kind === 'hit' ? COLOR.hit : kind === 'block' ? COLOR.block : kind === 'miss' ? COLOR.blocked : COLOR.text;
    const label = new Text({
      text,
      style: { fontFamily: 'Manrope Variable, system-ui, sans-serif', fontSize: 18, fontWeight: '800', fill: color, stroke: { color: 0x08090b, width: 4 } },
    });
    label.anchor.set(0.5, 1);
    // Попадание и блок видно и картинкой: вспышка на корпусе, щит — сотами вокруг меха.
    if (kind === 'hit' || kind === 'block') void this.burst(view, kind);
    const x = view.holder.x;
    const y = view.holder.y - CELL_PX * 0.9;
    label.position.set(x, y);
    this.fx.addChild(label);
    return this.tween(900, (k) => {
      label.y = y - 26 * k;
      label.alpha = k < 0.7 ? 1 : 1 - (k - 0.7) / 0.3;
    }).then(() => label.destroy());
  }

  /** Обновить части меха (спрятать разбитую руку) без перестройки всего поля. */
  wound(id: string, part: MechPart, hp: number): void {
    const view = this.units.get(id);
    if (!view) return;
    const i = PARTS.indexOf(part);
    const next = [...view.unit.hp];
    next[i] = hp;
    view.unit = { ...view.unit, hp: next };
    this.pose(view, view.unit.dir);
    if (part === 'body' && hp <= 0) {
      view.base.visible = false;
      void this.burst(view, 'explosion');
      void this.tween(400, (k) => (view.holder.alpha = 1 - 0.65 * k));
    }
  }

  /** Проиграть эффект поверх меха кадр за кадром; кадров нет — ничего не рисуем. */
  private burst(view: UnitView, name: FxName): Promise<void> {
    const frames = fxTextures(name);
    if (frames.length === 0) return Promise.resolve();
    const { size, ms } = FX[name];
    const sprite = new Sprite(frames[0]);
    sprite.anchor.set(0.5);
    sprite.width = CELL_PX * size;
    sprite.height = CELL_PX * size;
    // Центр — на корпусе, а не на клетке: мех стоит ногами ниже центра, а корпус заходит выше.
    sprite.position.set(view.holder.x, view.holder.y - CELL_PX * 0.2);
    this.fx.addChild(sprite);
    return this.tween(ms, (k) => {
      sprite.texture = frames[Math.min(frames.length - 1, Math.floor(k * frames.length))];
    }).then(() => sprite.destroy());
  }

  unitOf(id: string): MechUnitView | null {
    return this.units.get(id)?.unit ?? null;
  }

  // ------------------------------------------------------------------ камера и ввод

  /**
   * Показать точку поля (в клетках, можно дробную) на высоте yFrac экрана: 0.5 — по центру. Телефону нужна
   * точка повыше — низ экрана занимает лист цели.
   */
  centerOn(x: number, y: number, yFrac = 0.5): void {
    this.cx = (x + 0.5) * CELL_PX;
    this.cy = (y + 0.5) * CELL_PX + (0.5 - yFrac) * this.app.screen.height / this.zoom;
    this.applyCamera();
  }

  /** Зум так, чтобы поле влезло, но клетка не мельче minCell px. */
  fit(minCell = 44): void {
    const w = this.field.width * CELL_PX;
    const h = this.field.height * CELL_PX;
    const fit = Math.min(this.app.screen.width / w, this.app.screen.height / h) * 0.94;
    this.zoom = clamp(Math.max(fit, minCell / CELL_PX), ZOOM_MIN, ZOOM_MAX);
    this.cx = w / 2;
    this.cy = h / 2;
    this.applyCamera();
  }

  zoomBy(factor: number): void {
    this.zoom = clamp(this.zoom * factor, ZOOM_MIN, ZOOM_MAX);
    this.applyCamera();
  }

  private applyCamera(): void {
    const w = this.field.width * CELL_PX;
    const h = this.field.height * CELL_PX;
    // Поле не уезжает за край дальше половины экрана: потерять его перетаскиванием нельзя.
    const hw = this.app.screen.width / 2 / this.zoom;
    const hh = this.app.screen.height / 2 / this.zoom;
    this.cx = w > 2 * hw ? clamp(this.cx, hw * 0.5, w - hw * 0.5) : w / 2;
    this.cy = h > 2 * hh ? clamp(this.cy, hh * 0.5, h - hh * 0.5) : h / 2;
    this.root.scale.set(this.zoom);
    this.root.position.set(this.app.screen.width / 2 - this.cx * this.zoom, this.app.screen.height / 2 - this.cy * this.zoom);
  }

  private cellAt(sx: number, sy: number): [number, number] | null {
    const x = Math.floor((sx - this.root.x) / this.zoom / CELL_PX);
    const y = Math.floor((sy - this.root.y) / this.zoom / CELL_PX);
    return this.field.inside(x, y) ? [x, y] : null;
  }

  private bindInput(): void {
    const canvas = this.app.canvas;
    const local = (e: PointerEvent | WheelEvent): [number, number] => {
      const r = canvas.getBoundingClientRect();
      return [e.clientX - r.left, e.clientY - r.top];
    };
    const down = (e: PointerEvent) => {
      if (this.drag) return;
      const [x, y] = local(e);
      this.drag = { id: e.pointerId, x, y, cx: this.cx, cy: this.cy, moved: false };
    };
    const move = (e: PointerEvent) => {
      const [x, y] = local(e);
      if (!this.drag || this.drag.id !== e.pointerId) {
        if (e.pointerType === 'mouse') {
          const cell = this.cellAt(x, y);
          if (cell) this.onHover?.(cell[0], cell[1]);
        }
        return;
      }
      const dx = x - this.drag.x;
      const dy = y - this.drag.y;
      if (!this.drag.moved && Math.hypot(dx, dy) < DRAG_SLOP) return;
      this.drag.moved = true;
      this.cx = this.drag.cx - dx / this.zoom;
      this.cy = this.drag.cy - dy / this.zoom;
      this.applyCamera();
    };
    const up = (e: PointerEvent) => {
      if (!this.drag || this.drag.id !== e.pointerId) return;
      const moved = this.drag.moved;
      this.drag = null;
      if (moved || e.type === 'pointercancel') return;
      const [x, y] = local(e);
      const cell = this.cellAt(x, y);
      if (cell) this.onCell?.(cell[0], cell[1]);
    };
    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      this.zoomBy(e.deltaY < 0 ? 1.12 : 1 / 1.12);
    };
    canvas.addEventListener('pointerdown', down);
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', up);
    canvas.addEventListener('wheel', wheel, { passive: false });
    this.detach.push(() => {
      canvas.removeEventListener('pointerdown', down);
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', up);
      canvas.removeEventListener('wheel', wheel);
    });
  }
}

function clamp(v: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, v));
}
