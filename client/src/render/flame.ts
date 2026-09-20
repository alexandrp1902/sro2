// Пламя двигателей для кораблей, которым его не нарисовали (M15.6).
//
// Своего арта пламени нет ни у одного нового спрайта, и в задании на арт его тоже нет: у шести корпусов
// игрока факел был чужой, взятый по размеру, а у всех восьми NPC его не было вовсе. Поэтому пламя рисуется
// кодом. Геометрия здесь чистая — без Pixi: её строит ship.ts один раз на смену корабля, а в кадре остаются
// только масштаб и прозрачность, так что десяток кораблей на экране стоит столько же, сколько раньше.

/** Слой пламени: полигон в осях картинки корабля — (0, 0) на линии сопел, y растёт назад. */
export interface FlameLayer {
  /** Точки полигона, парами x, y — как их ждёт Graphics.poly. */
  points: number[];
  color: number;
  alpha: number;
}

/** Горячее ядро — почти белое. */
const CORE_COLOR = 0xfff1c0;
/** Ореол вокруг него — рыжий. */
const GLOW_COLOR = 0xff8a3d;

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
 */
export function flameShape(width: number, body: number): FlameLayer[] {
  return [
    { points: cone(width * GLOW_WIDTH, body * GLOW_LENGTH), color: GLOW_COLOR, alpha: 0.38 },
    { points: cone(width * CORE_WIDTH, body * CORE_LENGTH), color: CORE_COLOR, alpha: 0.95 },
  ];
}
