// Пламя двигателей для кораблей, которым его не нарисовали (M15.6).
//
// Своего арта пламени нет ни у одного нового спрайта, и в задании на арт его тоже нет: у шести корпусов
// игрока факел был чужой, взятый по размеру, а у всех восьми NPC его не было вовсе. Поэтому пламя рисуется
// кодом. Геометрия здесь чистая — без Pixi: её строит ship.ts один раз на смену корабля, а в кадре остаются
// только масштаб и прозрачность, так что десяток кораблей на экране стоит столько же, сколько раньше.
//
// Здесь же и сила огня: она ступенчатая, и это правило одно на все корабли — и на нарисованные кадры тоже.
import { localVelocity, type HullParams, type ShipState } from '../sim/movement';

/** Слой пламени: полигон в осях картинки корабля — (0, 0) на линии сопел, y растёт назад. */
export interface FlameLayer {
  /** Точки полигона, парами x, y — как их ждёт Graphics.poly. */
  points: number[];
  color: number;
  alpha: number;
}

/**
 * Два выхлопа: горячий — у химических двигателей пилотов и торговцев, холодный неон — у пиратов
 * и рейнджеров. Раньше холодный делался оттенком поверх рыжего и давал не голубой, а бурый цвет.
 */
export type FlamePalette = 'hot' | 'neon';

/** Ядро почти белое, ореол вокруг него цветной: у горячего рыжий, у неона голубой. */
const PALETTES: Record<FlamePalette, { core: number; glow: number }> = {
  hot: { core: 0xfff1c0, glow: 0xff8a3d },
  neon: { core: 0xeafdff, glow: 0x24b8ff },
};

/** Ступени огня: 0, 25, 50, 75, 100 % — между ними пламя не живёт. */
const STEPS = 4;

/**
 * Медленнее этой доли полного хода корабль считается стоящим. Тяга без полёта бывает часто: пират
 * висит на дистанции боя и жмёт газ в развороте — сопло при этом гореть не должно.
 */
const IDLE_SHARE = 0.1;

/**
 * Сила огня ступенями: 0 — не горит, 1 — полный. Ступень берётся от тяги, но только у того,
 * кто действительно летит вперёд.
 */
export function engineGlow(state: ShipState, throttle: number, hull: HullParams): number {
  if (throttle <= 0) return 0;
  const { forward } = localVelocity(state);
  if (forward < hull.maxSpeed * IDLE_SHARE) return 0;
  return Math.round(Math.min(1, throttle) * STEPS) / STEPS;
}

/** Доли ширины картинки и длины корпуса: подобраны под три нарисованных факела «Пчелы», «Странника» и «Молота». */
const GLOW_WIDTH = 0.17;
const GLOW_LENGTH = 0.6;
const CORE_WIDTH = 0.075;
const CORE_LENGTH = 0.4;

/**
 * Конус на пять точек: два плеча у сопел, две точки сужения и остриё. Треугольник читался бы жёстко,
 * а плечи дают факелу узнаваемую форму — широкое основание и вытянутый язык.
 */
function cone(halfWidth: number, length: number): number[] {
  return [
    -halfWidth, 0,
    -halfWidth * 0.55, length * 0.55,
    0, length,
    halfWidth * 0.55, length * 0.55,
    halfWidth, 0,
  ];
}

/**
 * Форма пламени для корабля: ореол и горячее ядро внутри него.
 * @param width ширина картинки корабля — от неё ширина факела
 * @param body длина корпуса без пламени — от неё вылет факела назад
 * @param palette горячий выхлоп или холодный неон
 */
export function flameShape(width: number, body: number, palette: FlamePalette = 'hot'): FlameLayer[] {
  const { core, glow } = PALETTES[palette];
  return [
    { points: cone(width * GLOW_WIDTH, body * GLOW_LENGTH), color: glow, alpha: 0.38 },
    { points: cone(width * CORE_WIDTH, body * CORE_LENGTH), color: core, alpha: 0.95 },
  ];
}
