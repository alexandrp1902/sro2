/**
 * Корона звезды (M16a): несколько полупрозрачных огненных слоёв вокруг диска. Они медленно дышат —
 * каждый в своём темпе и со своей фазой, — отчего звезда выглядит горячей и живой, а не наклейкой.
 *
 * Слои нарочно не круги: каждый чуть сплюснут, повёрнут на свой угол и тянется по осям вразнобой, —
 * так их пересечение читается как плазма, а не как циркульные окружности. Размер самой звезды и её
 * зона жара от этого не меняются: корона — только картинка, механика вокруг звезды прежняя.
 *
 * Считается это всё одним синусом на слой в уже существующем SystemView.update — ни своего тикера,
 * ни воркера, ни перестроения геометрии: слои строятся один раз, а каждый кадр им меняют только
 * масштаб, поворот и прозрачность.
 */

import { Container, Graphics } from 'pixi.js';

const TAU = Math.PI * 2;

/** Из скольких залитых кругов собран один слой: чем больше, тем мягче кромка — и тем дороже. */
const STEPS = 7;

/** Сколько слоёв у короны. */
const LAYERS = 4;

/**
 * Периоды дыхания слоёв, секунды. Взяты взаимно несоизмеримыми: с кратными слои раз за разом
 * сходились бы в одну фазу, и пульсация читалась бы как один общий удар.
 */
const PERIODS = [7.3, 9.7, 11.9, 13.7];

/** Цвета короны по виду звезды: близкие к её картинке, от яркой сердцевины к тёмной кромке. */
const COLORS: Record<string, [number, number]> = {
  yellow: [0xffd98a, 0xff9d3c],
  orange: [0xffb878, 0xff7328],
  blue: [0xa8dcff, 0x4f9dff],
  red: [0xff9a80, 0xd23c28],
  white: [0xffffff, 0xbcd8ff],
  binary: [0xffe2a6, 0x9dc4ff],
};

/** Один слой короны: где он, как дышит и какого цвета. */
export interface CoronaLayer {
  /** Радиус слоя в радиусах звезды: внешние слои шире диска. */
  radius: number;
  /** На какую долю слой растягивается и сжимается. */
  amplitude: number;
  /** Полный вдох-выдох за столько секунд. */
  period: number;
  /** Сдвиг фазы, доля периода: слои не дышат в такт. */
  phase: number;
  /** Оборотов в секунду; знак — направление вращения. */
  spin: number;
  /** Сплюснутость по вертикали; 1 — круг. */
  squash: number;
  /** Прозрачность в среднем положении. */
  alpha: number;
  color: number;
}

/** Каким слой выглядит в этот момент: множители к его собственному размеру и прозрачность. */
export interface CoronaFrame {
  sx: number;
  sy: number;
  alpha: number;
  rotation: number;
}

/** Слои короны звезды такого вида. Чистая: её и проверяют тесты. */
export function coronaLayers(kind: string): CoronaLayer[] {
  const [hot, cool] = COLORS[kind] ?? COLORS.yellow;
  const layers: CoronaLayer[] = [];
  for (let i = 0; i < LAYERS; i++) {
    const far = i / (LAYERS - 1); // 0 — у самого диска, 1 — внешний край
    layers.push({
      radius: 1.05 + far * 0.85,
      amplitude: 0.05 + far * 0.07,
      period: PERIODS[i % PERIODS.length],
      // Фазы врозь и не кратны друг другу: так ни один кадр не показывает все слои в одном положении.
      phase: (i * 0.37) % 1,
      spin: (i % 2 === 0 ? 1 : -1) * (0.004 + far * 0.006),
      squash: 1 - 0.1 * ((i % 3) - 1),
      alpha: 0.3 - far * 0.17,
      color: far < 0.5 ? hot : cool,
    });
  }
  return layers;
}

/**
 * Как выглядит слой в момент seconds. Оси дышат со сдвигом на четверть периода — слой всё время
 * чуть овальный и всё время в другую сторону, и ни в один момент не становится ровным кругом.
 */
export function coronaFrame(layer: CoronaLayer, seconds: number): CoronaFrame {
  const t = (seconds / layer.period + layer.phase) * TAU;
  const pulse = Math.sin(t);
  return {
    sx: 1 + layer.amplitude * pulse,
    sy: layer.squash * (1 + layer.amplitude * Math.sin(t + Math.PI / 2)),
    alpha: layer.alpha * (0.72 + 0.28 * pulse),
    rotation: seconds * layer.spin * TAU,
  };
}

/** Спокойный кадр: корона стоит развёрнутой, но не шевелится — для тех, кому движение мешает. */
export function coronaStill(layer: CoronaLayer): CoronaFrame {
  return { sx: 1, sy: layer.squash, alpha: layer.alpha, rotation: 0 };
}

/**
 * Корона вокруг звезды в центре системы. Кладётся под её картинку: диск должен остаться диском,
 * а свечение — уходить за его край.
 */
export class Corona {
  readonly view = new Container();
  private readonly nodes: { node: Graphics; layer: CoronaLayer }[] = [];

  /**
   * @param kind Вид звезды из galaxy.json: yellow, orange, blue, red, white, binary.
   * @param radius Радиус диска звезды на экране.
   * @param still true — не анимировать (запрос «меньше движения»).
   */
  constructor(kind: string, radius: number, private readonly still = false) {
    for (const layer of coronaLayers(kind)) {
      const node = new Graphics();
      const outer = radius * layer.radius;
      // Стопка залитых кругов от внешнего к внутреннему: у каждого своя прозрачность, и вместе
      // они дают мягкую кромку без отдельной картинки и без фильтра размытия.
      for (let i = STEPS; i >= 1; i--) {
        node.circle(0, 0, (outer * i) / STEPS).fill({ color: layer.color, alpha: 1 / STEPS });
      }
      // Слои складываются, а не перекрывают друг друга: наложение и даёт жар.
      node.blendMode = 'add';
      this.view.addChild(node);
      this.nodes.push({ node, layer });
    }
    this.apply(0);
  }

  /** Корона в этот момент орбитального времени; вызывается из SystemView.update. */
  update(seconds: number): void {
    if (this.still) return;
    this.apply(seconds);
  }

  private apply(seconds: number): void {
    for (const { node, layer } of this.nodes) {
      const frame = this.still ? coronaStill(layer) : coronaFrame(layer, seconds);
      node.scale.set(frame.sx, frame.sy);
      node.alpha = frame.alpha;
      node.rotation = frame.rotation;
    }
  }
}
