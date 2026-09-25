// Зеркало server/Sro.Sim/Mech (MechField, MechCombat) — только то, что нужно прогнозу: куда дойти, откуда
// видно, шанс попасть, сторона, шанс блока и вилка урона. Броски делает сервер; сверка — shared/test-vectors/mech.json.

import defaults from '../../../shared/mechs.json';

export interface MechFrame {
  name: string;
  hp: number;
  armor: number;
  move?: number;
}

export interface MechWeapon {
  name: string;
  damage: number;
  accuracy: number;
  minRange: number;
  optimalMin: number;
  optimalMax: number;
  maxRange: number;
  hp: number;
  armor: number;
  sound?: string;
}

export interface MechShield {
  name: string;
  hp: number;
  armor: number;
  block: number;
}

export interface MechUnitDef {
  name: string;
  body: string;
  chassis: string;
  left?: string | null;
  right?: string | null;
}

export interface MechCombatDef {
  rangeStep: number;
  rangeFloor: number;
  stillBonus: number;
  farMovePenalty: number;
  flankBonus: number;
  rearBonus: number;
  aimedPenalty: number;
  hitMin: number;
  hitMax: number;
  rollMin: number;
  rollMax: number;
  partsFront?: number[] | null;
  partsLeft?: number[] | null;
  partsRear?: number[] | null;
  shieldSame: number;
  shieldOpposite: number;
  blockMax: number;
}

export interface MechLines {
  who: string;
  role: string;
  lines: string[];
}

export interface MechMission {
  name: string;
  goal: string;
  map: string[];
  reward?: number;
  intro?: MechLines | null;
  win?: MechLines | null;
  lose?: MechLines | null;
}

export interface MechRules {
  bodies?: Record<string, MechFrame> | null;
  chassis?: Record<string, MechFrame> | null;
  weapons?: Record<string, MechWeapon> | null;
  shields?: Record<string, MechShield> | null;
  units?: Record<string, MechUnitDef> | null;
  combat?: MechCombatDef | null;
  missions?: Record<string, MechMission> | null;
}

/** Части в порядке столбцов таблиц: корпус, левая, правая, шасси. */
export type MechPart = 'body' | 'left' | 'right' | 'chassis';
export const PARTS: MechPart[] = ['body', 'left', 'right', 'chassis'];

export type MechSide = 'front' | 'left' | 'right' | 'rear';

/** Встроенный каталог — пока сервер не прислал свой (он приходит с первым mechState). */
export const DEFAULT_RULES = defaults as unknown as MechRules;

export const DEFAULT_COMBAT: MechCombatDef = {
  rangeStep: 5,
  rangeFloor: 25,
  stillBonus: 10,
  farMovePenalty: 10,
  flankBonus: 10,
  rearBonus: 15,
  aimedPenalty: 25,
  hitMin: 20,
  hitMax: 95,
  rollMin: 0.9,
  rollMax: 1.1,
  shieldSame: 15,
  shieldOpposite: -25,
  blockMax: 85,
};

const FRONT = [45, 20, 20, 15];
const LEFT = [35, 35, 10, 20];
const REAR = [55, 15, 15, 15];

export function combatOf(rules: MechRules): MechCombatDef {
  return { ...DEFAULT_COMBAT, ...(rules.combat ?? {}) };
}

/** Шаг по x и y для 8 направлений: 0 — север, по часовой. */
export const STEPS: ReadonlyArray<readonly [number, number]> = [
  [0, -1], [1, -1], [1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1],
];

export const DIRECTION_NAMES = ['n', 'ne', 'e', 'se', 's', 'sw', 'w', 'nw'] as const;

// ------------------------------------------------------------------ поле

export class MechField {
  readonly width: number;
  readonly height: number;

  constructor(private readonly map: readonly string[]) {
    this.height = map.length;
    this.width = map.length > 0 ? map[0].length : 0;
  }

  index(x: number, y: number): number {
    return y * this.width + x;
  }

  inside(x: number, y: number): boolean {
    return x >= 0 && y >= 0 && x < this.width && y < this.height;
  }

  cell(x: number, y: number): string {
    return this.map[y][x];
  }

  passable(x: number, y: number): boolean {
    if (!this.inside(x, y)) return false;
    const c = this.cell(x, y);
    return c === '.' || c === ',';
  }

  blocks(x: number, y: number): boolean {
    const c = this.cell(x, y);
    return c === 'w' || c === 'b';
  }

  /** Куда дойти за range шагов: индекс клетки → шагов. Своя клетка не входит. */
  reach(x: number, y: number, range: number, occupied: ReadonlySet<number>): Map<number, number> {
    const { dist } = this.flood(x, y, range, occupied);
    dist.delete(this.index(x, y));
    return dist;
  }

  /** Путь без стартовой клетки; null — не дойти. */
  path(x: number, y: number, tx: number, ty: number, range: number, occupied: ReadonlySet<number>): number[] | null {
    const { dist, parent } = this.flood(x, y, range, occupied);
    const start = this.index(x, y);
    const target = this.index(tx, ty);
    if (!dist.has(target) || target === start) return null;
    const path: number[] = [];
    for (let at = target; at !== start; at = parent.get(at)!) path.push(at);
    return path.reverse();
  }

  private flood(x: number, y: number, range: number, occupied: ReadonlySet<number>) {
    const start = this.index(x, y);
    const dist = new Map<number, number>([[start, 0]]);
    const parent = new Map<number, number>();
    const queue = [start];
    for (let head = 0; head < queue.length; head++) {
      const at = queue[head];
      const d = dist.get(at)!;
      if (d >= range) continue;
      const cx = at % this.width;
      const cy = Math.floor(at / this.width);
      for (const [dx, dy] of STEPS) {
        const nx = cx + dx;
        const ny = cy + dy;
        if (!this.passable(nx, ny)) continue;
        if (dx !== 0 && dy !== 0 && (!this.passable(cx + dx, cy) || !this.passable(cx, cy + dy))) continue;
        const next = this.index(nx, ny);
        if (occupied.has(next) || dist.has(next)) continue;
        dist.set(next, d + 1);
        parent.set(next, at);
        queue.push(next);
      }
    }
    return { dist, parent };
  }

  /** Линия огня: симметрична, концы не проверяются, закрывают только стена и здание. */
  lineOfFire(ax: number, ay: number, bx: number, by: number): boolean {
    const n = distance(ax, ay, bx, by);
    for (let i = 1; i < n; i++) {
      const [x0, x1] = axis(ax, bx - ax, i, n);
      const [y0, y1] = axis(ay, by - ay, i, n);
      if (this.blocks(x0, y0) && this.blocks(x1, y0) && this.blocks(x0, y1) && this.blocks(x1, y1)) return false;
    }
    return true;
  }
}

function axis(a: number, d: number, i: number, n: number): [number, number] {
  const t = a * n + d * i;
  const cell = Math.floor(t / n);
  const rest = t - cell * n;
  if (2 * rest === n) return [cell, cell + 1];
  return 2 * rest < n ? [cell, cell] : [cell + 1, cell + 1];
}

export function distance(ax: number, ay: number, bx: number, by: number): number {
  return Math.max(Math.abs(ax - bx), Math.abs(ay - by));
}

/** Ближайшее из 8 направлений на (dx, dy) — в целых, как на сервере. */
export function direction(dx: number, dy: number): number {
  if (dx === 0 && dy === 0) return 0;
  const ax = Math.abs(dx);
  const ay = Math.abs(dy);
  if (29 * ax < 12 * ay) return dy < 0 ? 0 : 4;
  if (29 * ay < 12 * ax) return dx > 0 ? 2 : 6;
  return dx > 0 ? (dy < 0 ? 1 : 3) : (dy < 0 ? 7 : 5);
}

// ------------------------------------------------------------------ формулы

export function moveRange(base: number, chassisHp: number, chassisMax: number): number {
  if (chassisHp <= 0) return 0;
  if (2 * chassisHp > chassisMax) return base;
  if (4 * chassisHp > chassisMax) return Math.max(1, base - 1);
  return Math.max(1, base - 2);
}

export function side(ax: number, ay: number, tx: number, ty: number, targetDir: number): MechSide {
  const [fx, fy] = STEPS[targetDir];
  const rx = ax - tx;
  const ry = ay - ty;
  const f = rx * fx + ry * fy;
  const s = rx * -fy + ry * fx;
  if (f > Math.abs(s)) return 'front';
  if (-f > Math.abs(s)) return 'rear';
  return s > 0 ? 'right' : 'left';
}

export function hitChance(
  c: MechCombatDef, w: MechWeapon, dist: number, steps: number, range: number, from: MechSide, aimed: boolean,
): number {
  const r = dist < w.optimalMin ? (w.optimalMin - dist) * c.rangeStep
    : dist > w.optimalMax ? (dist - w.optimalMax) * c.rangeStep
    : 0;
  const move = steps === 0 ? c.stillBonus : 2 * steps <= range ? 0 : -c.farMovePenalty;
  const flank = from === 'rear' ? c.rearBonus : from === 'front' ? 0 : c.flankBonus;
  const chance = w.accuracy - Math.min(r, c.rangeFloor) + move + flank - (aimed ? c.aimedPenalty : 0);
  return Math.min(c.hitMax, Math.max(c.hitMin, chance));
}

export function blockChance(
  c: MechCombatDef, shield: MechShield | null, shieldArm: MechPart, alive: boolean, from: MechSide,
): number {
  if (!shield || !alive || from === 'rear') return 0;
  const mod = from === 'front' ? 0 : (from === 'left') === (shieldArm === 'left') ? c.shieldSame : c.shieldOpposite;
  return Math.min(c.blockMax, Math.max(0, shield.block + mod));
}

export function partWeights(c: MechCombatDef, from: MechSide, hp: readonly number[]): number[] {
  const left = c.partsLeft ?? LEFT;
  const table = from === 'front' ? (c.partsFront ?? FRONT)
    : from === 'rear' ? (c.partsRear ?? REAR)
    : from === 'left' ? left
    : [left[0], left[2], left[1], left[3]];
  const weights = [...table];
  for (let i = 1; i < 4; i++) {
    if (hp[i] > 0) continue;
    weights[0] += weights[i];
    weights[i] = 0;
  }
  return weights;
}

export function round(x: number): number {
  return Math.floor(x + 0.5);
}

export function damage(raw: number, armor: number): number {
  return round(raw * (1 - armor / (armor + 100)));
}

export function damageRange(c: MechCombatDef, w: MechWeapon, armors: readonly number[]): [number, number] {
  if (armors.length === 0) return [0, 0];
  return [damage(w.damage * c.rollMin, Math.max(...armors)), damage(w.damage * c.rollMax, Math.min(...armors))];
}

// ------------------------------------------------------------------ бой глазами клиента

/** Мех в mechState. hp и max — по частям в порядке PARTS. */
export interface MechUnitView {
  id: string;
  side: 'player' | 'enemy';
  unit: string;
  name: string;
  x: number;
  y: number;
  dir: number;
  hp: number[];
  max: number[];
  activated: boolean;
}

export interface MechBattleView {
  mission: string;
  map: string[];
  round: number;
  turn: 'player' | 'enemy';
  current?: string | null;
  moved: boolean;
  steps: number;
  moveRange: number;
  units: MechUnitView[];
  winner?: 'player' | 'enemy' | null;
}

export function alive(u: MechUnitView): boolean {
  return u.hp[0] > 0;
}

export function unitDef(rules: MechRules, u: MechUnitView): MechUnitDef | null {
  return rules.units?.[u.unit] ?? null;
}

/** Рука с живым оружием: правая раньше левой. */
export function gunOf(rules: MechRules, u: MechUnitView): { arm: MechPart; id: string; weapon: MechWeapon } | null {
  const def = unitDef(rules, u);
  if (!def) return null;
  for (const [arm, i, id] of [['right', 2, def.right], ['left', 1, def.left]] as const) {
    const weapon = id ? rules.weapons?.[id] : undefined;
    if (weapon && u.hp[i] > 0) return { arm, id: id!, weapon };
  }
  return null;
}

export function shieldOf(rules: MechRules, u: MechUnitView): { arm: MechPart; shield: MechShield } | null {
  const def = unitDef(rules, u);
  if (!def) return null;
  const left = def.left ? rules.shields?.[def.left] : undefined;
  if (left) return { arm: 'left', shield: left };
  const right = def.right ? rules.shields?.[def.right] : undefined;
  return right ? { arm: 'right', shield: right } : null;
}

/** Что стоит в руке: оружие, щит или ничего. */
export function armItem(rules: MechRules, id: string | null | undefined): MechWeapon | MechShield | null {
  if (!id) return null;
  return rules.weapons?.[id] ?? rules.shields?.[id] ?? null;
}

export function armor(rules: MechRules, u: MechUnitView, part: MechPart): number {
  const def = unitDef(rules, u);
  if (!def) return 0;
  switch (part) {
    case 'body': return rules.bodies?.[def.body]?.armor ?? 0;
    case 'chassis': return rules.chassis?.[def.chassis]?.armor ?? 0;
    case 'left': return armItem(rules, def.left)?.armor ?? 0;
    default: return armItem(rules, def.right)?.armor ?? 0;
  }
}

export function occupied(state: MechBattleView, except?: MechUnitView): Set<number> {
  const set = new Set<number>();
  const width = state.map[0]?.length ?? 0;
  for (const u of state.units) if (alive(u) && u !== except) set.add(u.y * width + u.x);
  return set;
}

/** Куда может дойти текущий мех игрока; уже ходил — никуда. */
export function reachOf(state: MechBattleView): Map<number, number> {
  const me = state.units.find((u) => u.id === state.current);
  if (!me || state.moved || state.turn !== 'player') return new Map();
  return new MechField(state.map).reach(me.x, me.y, state.moveRange, occupied(state, me));
}

export type Refusal = 'noGun' | 'outOfRange' | 'noLine';

/** Прогноз выстрела shooter по target — ровно те числа, что посчитает сервер. */
export interface Forecast {
  ok: boolean;
  refusal?: Refusal;
  chance: number;
  side: MechSide;
  distance: number;
  block: number;
  min: number;
  max: number;
  weapon?: MechWeapon;
}

export function forecast(
  rules: MechRules, state: MechBattleView, shooter: MechUnitView, target: MechUnitView, aimed: MechPart | null,
): Forecast {
  const c = combatOf(rules);
  const from = side(shooter.x, shooter.y, target.x, target.y, target.dir);
  const dist = distance(shooter.x, shooter.y, target.x, target.y);
  const shield = shieldOf(rules, target);
  const block = shield ? blockChance(c, shield.shield, shield.arm, target.hp[PARTS.indexOf(shield.arm)] > 0, from) : 0;
  const gun = gunOf(rules, shooter);
  const base = { side: from, distance: dist, block, chance: 0, min: 0, max: 0 };
  if (!gun) return { ...base, ok: false, refusal: 'noGun' };
  const steps = shooter.id === state.current ? state.steps : 0;
  const range = shooter.id === state.current ? state.moveRange : 0;
  const chance = hitChance(c, gun.weapon, dist, steps, range, from, aimed !== null);
  // Куда может прийтись урон: выбранная часть или все живые с ненулевым весом; блок — в щит.
  const weights = partWeights(c, from, target.hp);
  const parts = aimed ? [aimed] : PARTS.filter((_, i) => weights[i] > 0);
  const armors = parts.map((p) => armor(rules, target, p));
  if (shield && block > 0 && aimed !== shield.arm) armors.push(shield.shield.armor);
  const [min, max] = damageRange(c, gun.weapon, armors);
  const field = new MechField(state.map);
  const refusal: Refusal | undefined = dist < gun.weapon.minRange || dist > gun.weapon.maxRange ? 'outOfRange'
    : !field.lineOfFire(shooter.x, shooter.y, target.x, target.y) ? 'noLine'
    : undefined;
  return { ...base, ok: !refusal, refusal, chance, min, max, weapon: gun.weapon };
}
