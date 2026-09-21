import { Container, Graphics, Sprite, Text } from 'pixi.js';
import { STATION } from '../game/layout';
import { Corona } from './corona';
import type { PlanetDto, SystemDto } from '../net/protocol';
import { gateLabel } from '../sim/galaxy';
import { WORLD_HALF_SIZE } from '../sim/movement';
import { CENTER, frameRotation, orbitAt, type Point } from '../sim/orbits';
import { hasSprite, spriteSize, texture, type SpriteName } from './sprites';

/** Гиперврата — фиолетовые: ни с чем в системе не путаются. */
export const GATE_COLOR = 0xb58cff;
export const STATION_LABEL_COLOR = 0x8fb4e8;
export const PLANET_COLOR = 0x9fc7a8;
export const PIRATE_BASE_COLOR = 0xff7a6b;
const SUN_LABEL_COLOR = 0xffc46b;
const GATE_RADIUS = 70;
/** Картинка станции в радиусах станции: кольцо с причалами шире круга стыковки. */
const STATION_SCALE = 1.35;
/** Диск звезды занимает около 0.8 картинки: корона и протуберанцы — за ним. */
const SUN_SCALE = 1.3;
/** Картинка планеты в радиусах планеты: у газового гиганта кольца шире диска. */
const PLANET_SCALE = 1.15;
const PIRATE_BASE_RADIUS = 140;
/** Станция по опасности системы, когда своя картинка не задана: безопасная — кольцо, дальше — купол и рудная. */
const STATIONS: SpriteName[] = ['stations-ring', 'stations-habitat', 'stations-mining', 'stations-mining', 'stations-mining', 'stations-mining'];

/** Картинка станции этой системы (M11): из galaxy.json, иначе по опасности. */
function stationSprite(system: { stationSprite?: string | null; danger: number }): SpriteName {
  const own = system.stationSprite ? `stations-${system.stationSprite}` : null;
  if (own && hasSprite(own)) return own;
  return STATIONS[Math.min(STATIONS.length, Math.max(1, system.danger)) - 1];
}

/** Размер врат на экране — для рамки прицела и выбора тапом. */
export const GATE_SIZE = GATE_RADIUS;

/** Планета, как она нарисована сейчас: для выбора прицелом, карточки и миникарты. */
export interface PlanetInfo {
  name: string;
  x: number;
  y: number;
  size: number;
  /** Ключ места поселения (M15); null — планета необитаема, сесть нельзя. */
  place: string | null;
  /** Как зовётся поселение: им подписана карточка посадки. */
  placeName: string | null;
}

interface Body {
  planet: PlanetDto;
  sprite: Sprite;
  label: Text;
  info: PlanetInfo;
}

/**
 * Карта системы (GDD §4–5): звезда в центре с зоной жара, станция и планеты на орбитах, гиперврата у краёв,
 * пиратская база. Станция и планеты двигаются: update() ставит их туда, где они в это орбитальное время.
 * Без сервера (system = null) — станция в центре, как в тестовой системе M1.
 */
export class SystemView {
  readonly view = new Container();
  private readonly station = new Container();
  private readonly stationLabel: Text | null = null;
  private readonly bodies: Body[] = [];
  /** Огненные слои вокруг звезды; null — звезды в системе нет. */
  private readonly corona: Corona | null = null;
  private readonly planetsInfo: PlanetInfo[] = [];
  /** Где станция сейчас: с этим считаются стыковка, прицел и миникарта. */
  readonly stationAt: Point = { x: STATION.x, y: STATION.y };

  constructor(private readonly system: SystemDto | null) {
    const view = this.view;
    const h = WORLD_HALF_SIZE;
    view.addChild(new Graphics().rect(-h, -h, h * 2, h * 2).stroke({ width: 6, color: 0xe0524a, alpha: 0.7 }));

    // Ни круга жара, ни линий орбит: звезда и тела на орбитах говорят сами за себя.
    const sun = system?.sun;
    if (sun) {
      // Корона — под картинкой звезды: свечение уходит за край диска, а сам диск остаётся резким (M16a).
      this.corona = new Corona(sun.kind, sun.radius * SUN_SCALE, quietMotion());
      view.addChild(this.corona.view);
      view.addChild(centred(sunSprite(sun.kind), 0, 0, sun.radius * SUN_SCALE));
      view.addChild(label(system.name, 0, sun.radius * SUN_SCALE + 26, SUN_LABEL_COLOR, 16));
    }

    for (const planet of system?.planets ?? []) {
      const sprite = centred(planetSprite(planet.kind), 0, 0, planet.size * PLANET_SCALE);
      const settled = planet.settlement && planet.id ? planet : null;
      // У обитаемой планеты на экране стоит имя поселения: к нему и садятся.
      const text = label(settled?.settlement?.name ?? planet.name, 0, 0, PLANET_COLOR, 15);
      const info = {
        name: planet.name,
        x: 0,
        y: 0,
        size: planet.size,
        place: settled ? `pl:${settled.id}` : null,
        placeName: settled ? (settled.settlement?.name ?? planet.name) : null,
      };
      view.addChild(sprite, text);
      this.bodies.push({ planet, sprite, label: text, info });
      this.planetsInfo.push(info);
    }

    const base = system?.pirateBase;
    if (base) {
      const fort = centred('stations-fortress', base.x, base.y, PIRATE_BASE_RADIUS);
      fort.tint = 0xffb0a4;
      view.addChild(fort, label(base.name, base.x, base.y - PIRATE_BASE_RADIUS - 22, PIRATE_BASE_COLOR, 16));
    }

    if (!system || system.station) {
      // Станция и всё, что у неё, — в её осях: +y — прочь от звезды. Контейнер поворачивается вместе с ней.
      this.station.addChild(centred(stationSprite(system ?? { danger: 1 }), 0, 0, STATION.radius * STATION_SCALE));
      this.stationLabel = label(system ? `Станция ${system.name}` : 'Станция', 0, 0, STATION_LABEL_COLOR);
      view.addChild(this.station, this.stationLabel);
    }

    for (const gate of system?.gates ?? []) {
      const g = new Graphics()
        // Зона прыжка: в этом круге врата принимают корабль.
        .circle(gate.x, gate.y, system!.gateRange)
        .fill({ color: GATE_COLOR, alpha: 0.05 })
        .stroke({ width: 2, color: GATE_COLOR, alpha: 0.3 })
        .circle(gate.x, gate.y, GATE_RADIUS)
        .stroke({ width: 6, color: GATE_COLOR, alpha: 0.9 })
        .circle(gate.x, gate.y, GATE_RADIUS * 0.62)
        .stroke({ width: 2, color: GATE_COLOR, alpha: 0.6 })
        .circle(gate.x, gate.y, GATE_RADIUS * 0.3)
        .fill({ color: GATE_COLOR, alpha: 0.35 });
      view.addChild(g, label(gateLabel(gate), gate.x, gate.y - system!.gateRange - 18, GATE_COLOR, 18));
    }
    this.update(0);
  }

  /** Планеты в этот кадр. */
  get planets(): readonly PlanetInfo[] {
    return this.planetsInfo;
  }

  /** Станция и планеты — туда, где они в орбитальное время seconds; по тем же часам дышит и звезда. */
  update(seconds: number): void {
    this.corona?.update(seconds);
    const orbit = this.system?.stationOrbit ?? CENTER;
    const at = orbitAt(orbit, seconds);
    this.stationAt.x = at.x;
    this.stationAt.y = at.y;
    this.station.position.set(at.x, at.y);
    this.station.rotation = frameRotation(orbit, seconds);
    this.stationLabel?.position.set(at.x, at.y - STATION.radius * 1.5);

    for (const body of this.bodies) {
      const p = orbitAt(body.planet.orbit, seconds);
      body.info.x = p.x;
      body.info.y = p.y;
      body.sprite.position.set(p.x, p.y);
      body.label.position.set(p.x, p.y + body.planet.size * PLANET_SCALE + 18);
    }
  }
}

/** Просили меньше движения: корона встанет развёрнутой, но пульсировать не будет. */
function quietMotion(): boolean {
  return typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
}

function sunSprite(kind: string): SpriteName {
  return known(`suns-${kind}`, 'suns-yellow');
}

function planetSprite(kind: string): SpriteName {
  return known(`planets-${kind}`, 'planets-terran');
}

/** Картинка с листа по имени из данных сервера; незнакомая — запасная. */
function known(name: string, fallback: SpriteName): SpriteName {
  return hasSprite(name) ? name : fallback;
}

/** Картинка по центру точки; radius — половина длинной стороны. */
function centred(name: SpriteName, x: number, y: number, radius: number): Sprite {
  const s = new Sprite(texture(name));
  const { w, h } = spriteSize(name);
  s.anchor.set(0.5);
  s.position.set(x, y);
  s.scale.set((radius * 2) / Math.max(w, h));
  return s;
}

function label(text: string, x: number, y: number, color: number, size = 14): Text {
  const t = new Text({ text, style: { fill: color, fontSize: size, fontFamily: 'system-ui, sans-serif' } });
  t.anchor.set(0.5);
  t.position.set(x, y);
  return t;
}
