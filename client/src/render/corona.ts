/**
 * Корона звезды: четыре нарисованных кольца (пачка I, art/space/sun-corona) вокруг диска — хромосфера,
 * основная корона, протуберанцы и далёкий ореол. Они медленно дышат и поворачиваются, каждое в своём
 * темпе, отчего звезда выглядит горячей, а не наклейкой.
 *
 * Кольца ложатся ПОД картинку диска. Снаружи альфа у них гаснет мягко, а вот внутренняя кромка, у
 * дырки, обрывается резко — под непрозрачным диском её не видно, а поверх него сложение нарисовало бы
 * жёсткую светлую окружность поперёк края звезды. Поэтому размер каждого кольца считается от доли его
 * дырки: дырка обязана остаться под диском даже на полном вдохе.
 *
 * Размеры звезды и зона жара от этого не меняются: корона — только картинка, и самый широкий слой
 * укладывается внутрь зоны жара (самое тесное burnRadius/radius в shared/galaxy.json — 2.48).
 * Своего потока отрисовки нет: кадр считается там же, где двигаются планеты, в SystemView.update.
 */

import { Container, Sprite } from 'pixi.js';
import { hasSprite, spriteSize, texture, type SpriteName } from './sprites';

const TAU = Math.PI * 2;

/** Только дробная часть: орбитальное время — unix-секунды, от их величины точность зависеть не должна. */
const frac = (x: number): number => x - Math.floor(x);

/**
 * Доля непрозрачного диска в картинке звезды, от половины длинной стороны. Листы звёзд режутся по
 * своей альфе, и доля у каждой своя: у белой диск почти во всю картинку, у двойной — две звезды
 * рядом, и круг, который наверняка закрыт, заметно меньше. Общая константа оставила бы двойной
 * звезде чёрную щель между диском и короной, а белой — утопила бы внутреннее кольцо.
 */
const DISC: Partial<Record<SpriteName, number>> = {
  'suns-yellow': 0.79,
  'suns-orange': 0.72,
  'suns-blue': 0.81,
  'suns-red': 0.82,
  'suns-white': 0.89,
  'suns-binary': 0.62,
};

/** Незнакомая звезда: берём самый узкий из обычных дисков — лучше перекрыть, чем оставить щель. */
const DISC_DEFAULT = 0.72;

/**
 * Периоды дыхания слоёв, секунды. Взяты взаимно несоизмеримыми: с кратными слои раз за разом
 * сходились бы в одну фазу, и корона читалась бы как один общий удар. Широкому слою — медленнее.
 */
const PERIODS = [13.7, 11.9, 9.7, 7.3];

/** Цвета короны по виду звезды: от яркой сердцевины к холодной кромке. */
const COLORS: Record<string, [number, number]> = {
  yellow: [0xffd98a, 0xff9d3c],
  orange: [0xffb878, 0xff7328],
  blue: [0xa8dcff, 0x4f9dff],
  red: [0xff9a80, 0xd23c28],
  white: [0xffffff, 0xbcd8ff],
  // Двойная в арте жёлто-красная, а не жёлто-голубая: холодный тон ей даёт синюю дымку вокруг пары.
  binary: [0xffe2a6, 0xff9a5c],
};

/** Одно кольцо короны: где оно, как дышит и какого цвета. */
export interface CoronaRing {
  sprite: SpriteName;
  /** Радиус картинки на экране в радиусах диска звезды. */
  size: number;
  /** Дырка и затухание в долях половины картинки — замер по альфе нарезанных webp. */
  hole: number;
  fade: number;
  /** Прозрачность в среднем положении: при сложении это и есть яркость свечения. */
  alpha: number;
  /** На какую долю слой растягивается и сжимается. */
  amplitude: number;
  /** Полный вдох-выдох за столько секунд. */
  period: number;
  /** Сдвиг фазы, доля периода: слои не дышат в такт. */
  phase: number;
  /** Оборотов в секунду; знак — направление вращения. */
  spin: number;
  color: number;
}

/** Каким слой выглядит в этот момент: множитель к его размеру, прозрачность и поворот. */
export interface CoronaFrame {
  scale: number;
  alpha: number;
  rotation: number;
}

/**
 * Слои в порядке рисования, снизу вверх. Размеры подобраны так, чтобы дырка каждого кольца даже на
 * полном вдохе оставалась внутри непрозрачного диска (size * hole * (1 + amplitude) < 0.95), а
 * внешний край самого широкого слоя не вылезал из зоны жара. Вращение несёт больше «живости», чем
 * дыхание, и заметно оно у протуберанцев: языки нарочно разной длины.
 */
const RINGS: Omit<CoronaRing, 'period' | 'phase' | 'color'>[] = [
  { sprite: 'suns-corona-outer', size: 2.0, hole: 0.35, fade: 0.84, alpha: 0.14, amplitude: 0.07, spin: 0.0008 },
  { sprite: 'suns-corona-mid', size: 1.7, hole: 0.46, fade: 0.84, alpha: 0.26, amplitude: 0.06, spin: -0.0016 },
  { sprite: 'suns-corona-plume', size: 1.6, hole: 0.52, fade: 0.83, alpha: 0.26, amplitude: 0.05, spin: 0.0042 },
  { sprite: 'suns-corona-inner', size: 1.25, hole: 0.71, fade: 0.86, alpha: 0.4, amplitude: 0.04, spin: -0.001 },
];

/** Радиус непрозрачного диска звезды на экране; spriteRadius — половина длинной стороны картинки. */
export function discRadius(sprite: SpriteName, spriteRadius: number): number {
  return (DISC[sprite] ?? DISC_DEFAULT) * spriteRadius;
}

/** Кольца короны звезды такого вида, от широкого к узкому. Чистая: её и проверяют тесты. */
export function coronaRings(kind: string): CoronaRing[] {
  const [hot, cool] = COLORS[kind] ?? COLORS.yellow;
  return RINGS.map((ring, i) => ({
    ...ring,
    period: PERIODS[i % PERIODS.length],
    // Фазы врозь и не кратны друг другу: ни один кадр не показывает все слои в одном положении.
    phase: (i * 0.37) % 1,
    // Дальние слои холодные, ближние к диску — горячие: так корона остывает к краю.
    color: i < RINGS.length / 2 ? cool : hot,
  }));
}

/** Внешний край слоя в радиусах диска — в самый широкий момент дыхания. */
export function coronaExtent(ring: CoronaRing): number {
  return ring.size * ring.fade * (1 + ring.amplitude);
}

/** Как выглядит слой в момент seconds орбитального времени. */
export function coronaFrame(ring: CoronaRing, seconds: number): CoronaFrame {
  const pulse = Math.sin(TAU * frac(seconds / ring.period + ring.phase));
  return {
    scale: 1 + ring.amplitude * pulse,
    alpha: ring.alpha * (0.75 + 0.25 * pulse),
    rotation: frac(seconds * ring.spin) * TAU,
  };
}

/** Спокойный кадр: корона стоит развёрнутой, но не шевелится — для тех, кому движение мешает. */
export function coronaStill(ring: CoronaRing): CoronaFrame {
  return { scale: 1, alpha: ring.alpha, rotation: 0 };
}

/** Корона вокруг звезды в центре системы. */
export class Corona {
  readonly view = new Container();
  /** Где корона кончается на экране: дальше ставится подпись системы. */
  readonly outerRadius: number;
  private readonly nodes: { node: Sprite; base: number; ring: CoronaRing }[] = [];

  /**
   * @param kind Вид звезды из galaxy.json: yellow, orange, blue, red, white, binary.
   * @param disc Радиус непрозрачного диска звезды на экране (discRadius).
   * @param still true — не анимировать (запрос «меньше движения»).
   */
  constructor(kind: string, disc: number, private readonly still = false) {
    let outer = 0;
    for (const ring of coronaRings(kind)) {
      // Старая нарезка без колец: слой просто пропускается, звезда рисуется как рисовалась.
      if (!hasSprite(ring.sprite)) continue;
      const node = new Sprite(texture(ring.sprite));
      const { w, h } = spriteSize(ring.sprite);
      node.anchor.set(0.5);
      node.tint = ring.color;
      // Слои складываются, а не перекрывают друг друга: наложение и даёт жар.
      node.blendMode = 'add';
      const base = (ring.size * disc * 2) / Math.max(w, h);
      this.view.addChild(node);
      this.nodes.push({ node, base, ring });
      outer = Math.max(outer, coronaExtent(ring) * disc);
    }
    this.outerRadius = outer;
    this.apply(0);
  }

  /** Корона в этот момент орбитального времени; вызывается из SystemView.update. */
  update(seconds: number): void {
    if (this.still) return;
    this.apply(seconds);
  }

  private apply(seconds: number): void {
    for (const { node, base, ring } of this.nodes) {
      const frame = this.still ? coronaStill(ring) : coronaFrame(ring, seconds);
      node.scale.set(base * frame.scale);
      node.alpha = frame.alpha;
      node.rotation = frame.rotation;
    }
  }
}
