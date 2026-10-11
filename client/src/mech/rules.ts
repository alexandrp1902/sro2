// Зеркало server/Sro.Sim/Mech (MechField, MechCombat) — только то, что нужно прогнозу: куда дойти, откуда
// видно, шанс попасть, сторона, шанс блока и вилка урона. Броски делает сервер; сверка — shared/test-vectors/mech.json.

import defaults from '../../../shared/mechs.json';

export interface MechFrame {
  name: string;
  hp: number;
  armor: number;
  move?: number;
  weight?: number;
  maxLoad?: number;
  art?: string;
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
  weight?: number;
  art?: string | null;
}

export interface MechShield {
  name: string;
  hp: number;
  armor: number;
  block: number;
  weight?: number;
  art?: string | null;
}

export interface MechUnitDef {
  name: string;
  body: string;
  chassis: string;
  left?: string | null;
  right?: string | null;
  backpack?: string | null;
  level?: number;
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
  player?: Array<{unit: string; x: number; y: number; dir: number}>;
  rewardParts?: string[];
  objectiveXp?: number;
  objective?: {kind: string; rounds?: number; route?: Array<{x: number; y: number}>; zone?: Array<{x: number; y: number}>; firingCells?: Array<{x: number; y: number}>; evacuation?: Array<{x:number;y:number}>};
  biome?: string | null;
}

export interface MechRules {
  trainingPlace?: string | null;
  bodies?: Record<string, MechFrame> | null;
  chassis?: Record<string, MechFrame> | null;
  weapons?: Record<string, MechWeapon> | null;
  shields?: Record<string, MechShield> | null;
  units?: Record<string, MechUnitDef> | null;
  combat?: MechCombatDef | null;
  missions?: Record<string, MechMission> | null;
  backpacks?: Record<string, {name: string; effect: string; weight: number; value: number; art: string}> | null;
  recipes?: Record<string, {category: string; part: string; credits: number; resources: Record<string, number>;workshopLevel?:number}> | null;
  skills?: Record<string, {name: string; bonus: number; blockBonus?: number; accuracyPenalty?: number; requires?: string}> | null;
  workshop?: {place: string; campaign: string; prototypeMission: string; prototypeUnit: string; starterParts: string[]; missionOrder: string[]} | null;
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
  const penalty = 2 * chassisHp > chassisMax ? 0 : 4 * chassisHp > chassisMax ? 1 : 2;
  return Math.max(1, Math.ceil(Math.max(1, base - penalty) / 2));
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
  c: MechCombatDef, w: MechWeapon, dist: number, steps: number, range: number, _from: MechSide, aimed: boolean,
): number {
  const r = Math.max(0, dist - 1) * c.rangeStep;
  const move = steps === 0 ? c.stillBonus : 2 * steps <= range ? 0 : -c.farMovePenalty;
  const chance = w.accuracy - r + move - (aimed ? c.aimedPenalty : 0);
  return Math.min(90, c.hitMax, Math.max(c.hitMin, chance));
}

export function blockChance(
  c: MechCombatDef, shield: MechShield | null, _shieldArm: MechPart, alive: boolean, _from: MechSide,
): number {
  if (!shield || !alive) return 0;
  return Math.min(c.blockMax, Math.max(0, shield.block));
}

export function partWeights(c: MechCombatDef, _from: MechSide, hp: readonly number[]): number[] {
  const table = c.partsFront ?? FRONT;
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
  return round(2 * raw * (1 - armor / (armor + 100)));
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
  control?: 'player' | 'ai' | 'objective';
  owner?: string | null;
  accountOwner?: string | null;
  assembly?: MechUnitDef | null;
  skills?: string[];
  level?: number;
  repaired?: boolean;
  stance?: boolean;
  accuracyPenalty?: number;
  dashRange?: number;
  jumpRange?: number;
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
  goal?: string;
  progress?: number;
  required?: number;
  objectiveUnit?: string | null;
  reason?: string | null;
  actions?: string[];
  ready?: string[];
  biome?: string | null;
  interacted?: boolean;
  evacuation?: Array<{x:number;y:number}> | null;
  route?: Array<{x:number;y:number}> | null;
}

export function alive(u: MechUnitView): boolean {
  return u.hp[0] > 0;
}

export function unitDef(rules: MechRules, u: MechUnitView): MechUnitDef | null {
  return u.assembly ?? rules.units?.[u.unit] ?? null;
}

/** Рука с живым оружием: правая раньше левой. */
export function gunOf(rules: MechRules, u: MechUnitView, hand?: string | null): { arm: MechPart; id: string; weapon: MechWeapon } | null {
  const def = unitDef(rules, u);
  if (!def) return null;
  for (const [arm, i, id] of [['right', 2, def.right], ['left', 1, def.left]] as const) {
    if (hand && arm !== hand) continue;
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
  const base = part === 'body' ? rules.bodies?.[def.body]?.armor ?? 0
    : part === 'chassis' ? rules.chassis?.[def.chassis]?.armor ?? 0
    : armItem(rules, part === 'left' ? def.left : def.right)?.armor ?? 0;
  return base + packEffect(rules, u, 'armor') + (u.stance ? rules.skills?.defensiveStance?.bonus ?? 0 : 0);
}

export function packEffect(rules: MechRules, u: MechUnitView, effect: string): number {
  const id = unitDef(rules, u)?.backpack;
  const pack = id ? rules.backpacks?.[id] : null;
  return pack?.effect === effect ? pack.value : 0;
}

export function assemblyWeight(rules: MechRules, def: MechUnitDef): number {
  return (rules.bodies?.[def.body]?.weight ?? 0) + (armItem(rules, def.left)?.weight ?? 0)
    + (armItem(rules, def.right)?.weight ?? 0) + (def.backpack ? rules.backpacks?.[def.backpack]?.weight ?? 0 : 0);
}

export function xpThreshold(level:number):number {return 100+75*(level-1)+25*(level-1)**2;}

export function skillCompatible(rules:MechRules,def:MechUnitDef,id:string):boolean {
  const skill=rules.skills?.[id];if(!skill)return false;
  const weapons=Number(!!(def.left && rules.weapons?.[def.left]))+Number(!!(def.right && rules.weapons?.[def.right]));
  switch(skill.requires) {
    case 'twoWeapons':return weapons===2;
    case 'weapon':return weapons>0;
    case 'shield':return !!((def.left && rules.shields?.[def.left]) || (def.right && rules.shields?.[def.right]));
    case 'biped':return def.chassis !== 'tracked';
    default:return true;
  }
}

export const SKILL_DESCRIPTION:Readonly<Record<string,string>>={
  doubleAttack:'Иногда стреляет второй исправной оружейной рукой.',counterAttack:'Иногда отвечает на обычную атаку исправным оружием.',
  jump:'Иногда перепрыгивает препятствие на выбранную свободную клетку.',dash:'Иногда увеличивает дальность движения, снижая точность этой активации.',
  evade:'Иногда полностью избегает попавшей атаки.',defensiveStance:'При завершении хода иногда усиливает броню и щит до следующей активации.',
  targetingExpert:'Иногда снижает штраф прицельной атаки.',shieldMastery:'Иногда усиливает блок исправного щита.',
};

export function occupied(state: MechBattleView, except?: MechUnitView): Set<number> {
  const set = new Set<number>();
  const width = state.map[0]?.length ?? 0;
  for (const u of state.units) if (alive(u) && u !== except) set.add(u.y * width + u.x);
  return set;
}

/** Куда может дойти текущий мех игрока; уже ходил — никуда. */
export function reachOf(state: MechBattleView, automatic = false): Map<number, number> {
  const me = state.units.find((u) => u.id === state.current);
  if (!me || state.moved || state.turn !== 'player') return new Map();
  const field=new MechField(state.map), taken=occupied(state,me);
  const reach=field.reach(me.x,me.y,state.moveRange,taken);
  if (!automatic || state.moveRange===0) return reach;
  if (me.dashRange) for(const [cell,steps] of field.reach(me.x,me.y,state.moveRange+me.dashRange,taken)) reach.set(cell,steps);
  if (me.jumpRange) for(let y=0;y<field.height;y++) for(let x=0;x<field.width;x++) {
    const cell=field.index(x,y);
    if (!reach.has(cell) && field.passable(x,y) && !taken.has(cell) && distance(me.x,me.y,x,y)<=me.jumpRange && (x!==me.x || y!==me.y)) reach.set(cell,1);
  }
  return reach;
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
  rules: MechRules, state: MechBattleView, shooter: MechUnitView, target: MechUnitView, aimed: MechPart | null, hand?: string | null,
): Forecast {
  const c = combatOf(rules);
  const from = side(shooter.x, shooter.y, target.x, target.y, target.dir);
  const dist = distance(shooter.x, shooter.y, target.x, target.y);
  const shield = shieldOf(rules, target);
  const ordinaryBlock = shield ? blockChance(c, shield.shield, shield.arm, target.hp[PARTS.indexOf(shield.arm)] > 0, from) : 0;
  const block = ordinaryBlock > 0 && target.stance ? Math.min(c.blockMax, ordinaryBlock + (rules.skills?.defensiveStance?.blockBonus ?? 0)) : ordinaryBlock;
  const gun = gunOf(rules, shooter, hand);
  const base = { side: from, distance: dist, block, chance: 0, min: 0, max: 0 };
  if (!gun) return { ...base, ok: false, refusal: 'noGun' };
  const steps = shooter.id === state.current ? state.steps : 0;
  const range = shooter.id === state.current ? state.moveRange : 0;
  const weapon = {...gun.weapon, accuracy: gun.weapon.accuracy + packEffect(rules, shooter, 'accuracy') - (shooter.accuracyPenalty ?? 0),
    damage: round(gun.weapon.damage * (packEffect(rules, shooter, 'damage') || 1))};
  const chance = hitChance(c, weapon, dist, steps, range, from, aimed !== null);
  // Куда может прийтись урон: выбранная часть или все живые с ненулевым весом; блок — в щит.
  const weights = partWeights(c, from, target.hp);
  const parts = aimed ? [aimed] : PARTS.filter((_, i) => weights[i] > 0);
  const armors = parts.map((p) => armor(rules, target, p));
  if (shield && block > 0 && aimed !== shield.arm) armors.push(armor(rules, target, shield.arm));
  const [min, max] = damageRange(c, weapon, armors);
  const field = new MechField(state.map);
  const firingCells=rules.missions?.[state.mission]?.objective?.firingCells;
  const outsideFiringCells=target.id===state.objectiveUnit && firingCells?.length && !firingCells.some(p=>p.x===shooter.x && p.y===shooter.y);
  const refusal: Refusal | undefined = dist < 1 || dist > gun.weapon.maxRange || outsideFiringCells ? 'outOfRange'
    : !field.lineOfFire(shooter.x, shooter.y, target.x, target.y) ? 'noLine'
    : undefined;
  return { ...base, ok: !refusal, refusal, chance, min, max, weapon };
}
