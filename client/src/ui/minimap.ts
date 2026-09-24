import type { GateDto, PirateBaseDto } from '../net/protocol';
import { dangerColor, gateLetters, gateNumber } from '../sim/galaxy';
import { BUOY_RADIUS } from '../sim/missions';
import { WORLD_HALF_SIZE } from '../sim/movement';
import { color } from './cargoHud';
import type { PartyMark } from './party';

const RENDER_INTERVAL_MS = 100;

/**
 * Цвета данных — как у маркеров в мире (render/world.ts, render/playerOverlay.ts): роль корабля, врата,
 * планета, звезда. Всё остальное — линии, радар, станция, цель, свой корабль — берётся из токенов (palette()).
 */
const DATA = {
  star: '#ffc46b',
  planet: '#9fc7a8',
  gate: '#b58cff',
  player: '#ffb45a',
  pirate: '#ff6b5a',
  drone: '#9ccf9a',
  trader: '#e8c95a',
  ranger: '#6fe0c8',
  convoy: '#e8c95a',
  wing: '#6fe0c8',
  rebel: '#e08a4a',
  party: '#b6ff6a',
};

/** Токены дизайн-системы, которые холст читает из CSS; запасные значения — те же, что в tokens.css. */
interface Palette {
  steel: string;
  steelLine: string;
  lineSoft: string;
  textMuted: string;
  textStrong: string;
  bgWindow: string;
  warn: string;
  danger: string;
}

const FALLBACK: Palette = {
  steel: '#d5dce6',
  steelLine: 'rgba(200,210,222,0.35)',
  lineSoft: 'rgba(255,255,255,0.07)',
  textMuted: '#8b939e',
  textStrong: '#f6f7f9',
  bgWindow: 'rgba(20,22,26,0.97)',
  warn: '#d9c08a',
  danger: '#e0524a',
};

const TOKEN: Record<keyof Palette, string> = {
  steel: '--steel',
  steelLine: '--steel-line',
  lineSoft: '--line-soft',
  textMuted: '--text-muted',
  textStrong: '--text-strong',
  bgWindow: '--bg-window',
  warn: '--warn',
  danger: '--danger',
};

/** Диск радара — стекло чуть светлее панели: то, что внутри, корабль видит. */
const RADAR_FILL = 'rgba(200, 210, 222, 0.07)';

/** Полупериод мигания SOS, мс. */
const SOS_BLINK_MS = 400;

/** Уже этого (CSS px) подпись системы в углу не рисуется — телефонная миникарта 96 px. */
const CAPTION_MIN_WIDTH = 110;

/** Точка на миникарте: корабль в радаре. */
export interface MinimapShip {
  id: number;
  x: number;
  y: number;
  kind: 'player' | 'pirate' | 'drone' | 'trader' | 'ranger' | 'party' | 'convoy' | 'wing' | 'rebel';
  dead: boolean;
}

export interface MinimapFrame {
  /** Имя системы и её опасность — подпись в углу холста, как на карте галактики. */
  name: string;
  danger: number;
  /** Есть ли звезда в центре (без сервера — нет). */
  sun: boolean;
  /** Радиус жара звезды; null — звезда не жжёт или её нет. */
  burnRadius: number | null;
  /** Радиусы орбит станции и планет: по ним видно, где тела окажутся потом. */
  orbits: readonly number[];
  /** Где сейчас станция на орбите; null — станции нет. */
  station: { x: number; y: number } | null;
  /** Планеты; settled — есть поселение, туда можно сесть. */
  planets: readonly { x: number; y: number; settled: boolean }[];
  pirateBase: PirateBaseDto | null;
  gates: readonly GateDto[];
  own: { x: number; y: number; rot: number } | null;
  /** Радиус радара: всё, что сервер присылает, — внутри него (GDD §10). */
  radar: number;
  ships: Iterable<MinimapShip>;
  targetId: number;
  /** Цель задания или обучения — золотое кольцо; null — нет. */
  objective?: { x: number; y: number } | null;
  /** Учебный буй (M18) — пунктирная зона остановки в масштабе карты; null — шаг не тот. */
  buoy?: { x: number; y: number } | null;
  /** Ракеты в полёте. */
  missiles?: readonly { x: number; y: number }[];
  /** Торговцы, которые зовут на помощь: мигающее красное кольцо — и за радаром. */
  sos?: readonly { x: number; y: number }[];
  /** Точка сбора вторжения пиратов — мигающий красный крест в кольце; null — нет. */
  invasion?: { x: number; y: number } | null;
  /**
   * Участники группы в этой системе (M16b) — ромб с номером. Приходят не из снапшота, а из состояния
   * группы, поэтому радар им не предел: товарищ виден через всю систему.
   */
  party?: readonly PartyMark[];
  /** Номер врат, через которые лежит курс (M16b); null — курса нет. */
  routeGate?: number | null;
}

/**
 * Миникарта (GDD §43): вся система целиком — звезда с зоной жара, орбиты, станция и планеты, пиратская база,
 * врата, круг радара и корабли в нём, пунктир курса до нужных врат.
 * Врата видны всегда: это карта, а не радар. Тап открывает карту галактики.
 */
export class Minimap {
  private readonly ctx: CanvasRenderingContext2D;
  private lastRender = 0;
  private size = 0;
  private palette: Palette = FALLBACK;

  constructor(
    private readonly canvas: HTMLCanvasElement,
    onTap: () => void,
  ) {
    this.ctx = canvas.getContext('2d')!;
    canvas.addEventListener('click', onTap);
  }

  set hidden(hidden: boolean) {
    this.canvas.hidden = hidden;
  }

  update(frame: MinimapFrame, now: number): void {
    if (this.canvas.hidden || now - this.lastRender < RENDER_INTERVAL_MS) return;
    this.lastRender = now;
    this.resize();

    const { ctx, size, palette } = this;
    const scale = size / (WORLD_HALF_SIZE * 2);
    const px = (v: number) => size / 2 + v * scale;
    const dpr = size / this.canvas.clientWidth || 1;
    const blink = Math.floor(now / SOS_BLINK_MS) % 2 === 0;

    ctx.clearRect(0, 0, size, size);
    ctx.save();
    roundRect(ctx, 0, 0, size, size, 12 * dpr); // тот же радиус, что --radius-md у панели
    ctx.clip();
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';

    // Орбиты — тонкие линии под всем: по ним видно, куда уйдёт станция, пока летишь.
    if (frame.sun) {
      ctx.strokeStyle = palette.lineSoft;
      ctx.lineWidth = dpr;
      for (const radius of frame.orbits) {
        if (radius <= 0) continue;
        ctx.beginPath();
        ctx.arc(px(0), px(0), radius * scale, 0, 2 * Math.PI);
        ctx.stroke();
      }
      // Зона жара — единственное красное поле на карте: сюда лететь нельзя.
      if (frame.burnRadius) {
        ctx.beginPath();
        ctx.arc(px(0), px(0), frame.burnRadius * scale, 0, 2 * Math.PI);
        ctx.globalAlpha = 0.1;
        ctx.fillStyle = palette.danger;
        ctx.fill();
        ctx.globalAlpha = 0.3;
        ctx.strokeStyle = palette.danger;
        ctx.stroke();
        ctx.globalAlpha = 1;
      }
    }

    if (frame.own) {
      ctx.beginPath();
      ctx.arc(px(frame.own.x), px(frame.own.y), frame.radar * scale, 0, 2 * Math.PI);
      ctx.fillStyle = RADAR_FILL;
      ctx.fill();
      ctx.strokeStyle = palette.steelLine;
      ctx.lineWidth = dpr;
      ctx.stroke();
    }

    if (frame.sun) {
      ctx.beginPath();
      ctx.arc(px(0), px(0), 3.5 * dpr, 0, 2 * Math.PI);
      ctx.fillStyle = DATA.star;
      ctx.fill();
    }

    for (const planet of frame.planets) {
      ctx.beginPath();
      if (planet.settled) {
        // Обитаемая — кольцо с точкой: сюда можно сесть.
        ctx.arc(px(planet.x), px(planet.y), 3 * dpr, 0, 2 * Math.PI);
        ctx.strokeStyle = DATA.planet;
        ctx.lineWidth = 1.5 * dpr;
        ctx.stroke();
        ctx.beginPath();
        ctx.arc(px(planet.x), px(planet.y), dpr, 0, 2 * Math.PI);
      } else {
        ctx.arc(px(planet.x), px(planet.y), 2.5 * dpr, 0, 2 * Math.PI);
      }
      ctx.fillStyle = DATA.planet;
      ctx.fill();
    }

    if (frame.pirateBase) {
      const x = px(frame.pirateBase.x);
      const y = px(frame.pirateBase.y);
      const s = 3.5 * dpr;
      ctx.fillStyle = palette.danger;
      ctx.fillRect(x - s, y - s, s * 2, s * 2);
      ctx.strokeStyle = palette.bgWindow;
      ctx.lineWidth = dpr;
      ctx.beginPath();
      ctx.moveTo(x - s * 0.5, y - s * 0.5);
      ctx.lineTo(x + s * 0.5, y + s * 0.5);
      ctx.moveTo(x + s * 0.5, y - s * 0.5);
      ctx.lineTo(x - s * 0.5, y + s * 0.5);
      ctx.stroke();
    }

    // Станция — стальной квадрат: тот же знак, что на карте галактики.
    if (frame.station) {
      const s = 3 * dpr;
      ctx.fillStyle = palette.steel;
      ctx.strokeStyle = palette.bgWindow;
      ctx.lineWidth = dpr;
      ctx.beginPath();
      ctx.rect(px(frame.station.x) - s, px(frame.station.y) - s, s * 2, s * 2);
      ctx.fill();
      ctx.stroke();
    }

    // Курс (M16b): пунктир от корабля к нужным вратам — «куда лететь» одним взглядом.
    const routeIndex = frame.gates.findIndex((_, i) => frame.routeGate === gateNumber(i));
    if (frame.own && routeIndex >= 0) {
      const gate = frame.gates[routeIndex];
      ctx.save();
      ctx.setLineDash([3 * dpr, 3 * dpr]);
      ctx.strokeStyle = palette.steel;
      ctx.lineWidth = dpr;
      ctx.beginPath();
      ctx.moveTo(px(frame.own.x), px(frame.own.y));
      ctx.lineTo(px(gate.x), px(gate.y));
      ctx.stroke();
      ctx.restore();
    }

    // Буква системы за вратами (M16c): с одного взгляда видно, куда они ведут, а не какие они по счёту.
    const letters = gateLetters(frame.gates);
    frame.gates.forEach((gate, i) => {
      const onRoute = i === routeIndex;
      ctx.beginPath();
      ctx.arc(px(gate.x), px(gate.y), (onRoute ? 4.5 : 3.5) * dpr, 0, 2 * Math.PI);
      ctx.strokeStyle = onRoute ? palette.steel : DATA.gate;
      ctx.lineWidth = (onRoute ? 2.5 : 2) * dpr;
      ctx.stroke();
      label(ctx, letters[i], px(gate.x), px(gate.y) - 10 * dpr, onRoute ? palette.textStrong : DATA.gate, dpr, palette);
    });

    // Свои из группы рисуются ниже ромбом с номером: точка корабля им не нужна, иначе метка сядет на неё.
    const inParty = new Set((frame.party ?? []).map((m) => m.id));
    ctx.lineWidth = 0.8 * dpr;
    ctx.strokeStyle = palette.bgWindow;
    for (const ship of frame.ships) {
      if (ship.dead || inParty.has(ship.id)) continue;
      ctx.beginPath();
      ctx.arc(px(ship.x), px(ship.y), 2.2 * dpr, 0, 2 * Math.PI);
      ctx.fillStyle = DATA[ship.kind];
      ctx.fill();
      ctx.stroke();
      if (ship.id === frame.targetId) reticle(ctx, px(ship.x), px(ship.y), 5 * dpr, dpr, palette.textStrong);
    }

    // Группа (M16b): ромб заметно отличается от точек кораблей, номер совпадает с номером в панели.
    for (const mark of frame.party ?? []) {
      const x = px(mark.x);
      const y = px(mark.y);
      const s = 3.2 * dpr;
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(Math.PI / 4);
      ctx.fillStyle = DATA.party;
      ctx.strokeStyle = palette.bgWindow;
      ctx.lineWidth = 0.8 * dpr;
      ctx.beginPath();
      ctx.rect(-s, -s, s * 2, s * 2);
      ctx.fill();
      ctx.stroke();
      ctx.restore();
      if (mark.id === frame.targetId) reticle(ctx, x, y, 6 * dpr, dpr, palette.textStrong);
      label(ctx, String(mark.n), x, y - 10 * dpr, DATA.party, dpr, palette);
    }

    // Ракеты — крошечные красные точки: видно, что к тебе что-то летит.
    for (const missile of frame.missiles ?? []) {
      const s = 1.2 * dpr;
      ctx.fillStyle = palette.danger;
      ctx.fillRect(px(missile.x) - s, px(missile.y) - s, s * 2, s * 2);
    }

    if (blink) {
      for (const call of frame.sos ?? []) {
        ctx.beginPath();
        ctx.arc(px(call.x), px(call.y), 6.5 * dpr, 0, 2 * Math.PI);
        ctx.strokeStyle = palette.danger;
        ctx.lineWidth = 2 * dpr;
        ctx.stroke();
      }
    }

    if (frame.invasion && !blink) {
      const x = px(frame.invasion.x);
      const y = px(frame.invasion.y);
      const s = 4 * dpr;
      ctx.strokeStyle = palette.danger;
      ctx.lineWidth = 2 * dpr;
      ctx.beginPath();
      ctx.arc(x, y, 8 * dpr, 0, 2 * Math.PI);
      ctx.moveTo(x - s, y - s);
      ctx.lineTo(x + s, y + s);
      ctx.moveTo(x + s, y - s);
      ctx.lineTo(x - s, y + s);
      ctx.stroke();
    }

    if (frame.buoy) {
      // Зона в масштабе карты: видно, куда долететь и где уже можно тормозить.
      const r = Math.max(4 * dpr, BUOY_RADIUS * scale);
      ctx.save();
      ctx.setLineDash([3 * dpr, 3 * dpr]);
      ctx.beginPath();
      ctx.arc(px(frame.buoy.x), px(frame.buoy.y), r, 0, 2 * Math.PI);
      ctx.strokeStyle = palette.warn;
      ctx.lineWidth = 1 * dpr;
      ctx.stroke();
      ctx.restore();
    }

    if (frame.objective) {
      ctx.beginPath();
      ctx.arc(px(frame.objective.x), px(frame.objective.y), 6 * dpr, 0, 2 * Math.PI);
      ctx.strokeStyle = palette.warn;
      ctx.lineWidth = 1.5 * dpr;
      ctx.stroke();
    }

    if (frame.own) {
      // Свой корабль — стрелка носом по курсу, с тёмной кромкой, чтобы читалась на диске радара.
      const { x, y, rot } = frame.own;
      ctx.save();
      ctx.translate(px(x), px(y));
      ctx.rotate(rot);
      ctx.beginPath();
      ctx.moveTo(0, -5.5 * dpr);
      ctx.lineTo(4 * dpr, 4.5 * dpr);
      ctx.lineTo(0, 2.5 * dpr);
      ctx.lineTo(-4 * dpr, 4.5 * dpr);
      ctx.closePath();
      ctx.fillStyle = palette.textStrong;
      ctx.strokeStyle = palette.bgWindow;
      ctx.lineWidth = dpr;
      ctx.fill();
      ctx.stroke();
      ctx.restore();
    }

    // Подпись системы в углу: точка опасности и имя капителью — та же связка, что на карте галактики.
    // На тёмной таблетке, чтобы врата в этом углу её не перебивали; на телефоне (96 px) места ей нет —
    // имя системы там и так стоит в статусе.
    if (frame.name && this.canvas.clientWidth >= CAPTION_MIN_WIDTH) {
      ctx.font = `600 ${Math.round(8 * dpr)}px "Manrope Variable", system-ui, sans-serif`;
      ctx.textAlign = 'left';
      ctx.textBaseline = 'middle';
      if ('letterSpacing' in ctx) ctx.letterSpacing = `${0.5 * dpr}px`;
      const text = frame.name.toUpperCase();
      const w = ctx.measureText(text).width + 17 * dpr;
      ctx.globalAlpha = 0.85;
      ctx.fillStyle = palette.bgWindow;
      roundRect(ctx, 5 * dpr, 5 * dpr, w, 11 * dpr, 5.5 * dpr);
      ctx.fill();
      ctx.globalAlpha = 1;
      ctx.beginPath();
      ctx.arc(10.5 * dpr, 10.5 * dpr, 2.5 * dpr, 0, 2 * Math.PI);
      ctx.fillStyle = color(dangerColor(frame.danger));
      ctx.fill();
      ctx.fillStyle = palette.textMuted;
      ctx.fillText(text, 16 * dpr, 11 * dpr);
      if ('letterSpacing' in ctx) ctx.letterSpacing = '0px';
    }
    ctx.restore();
  }

  /** Холст в пикселях экрана: иначе на Retina точки размыты. Заодно перечитывает токены — они могли смениться. */
  private resize(): void {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const size = Math.round(this.canvas.clientWidth * dpr);
    if (size === this.size || size === 0) return;
    this.size = this.canvas.width = this.canvas.height = size;
    this.palette = readPalette();
  }
}

/** Токены из CSS; чего нет (тесты, витрина без стилей) — из запасных значений. */
function readPalette(): Palette {
  const style = getComputedStyle(document.documentElement);
  const result = { ...FALLBACK };
  for (const key of Object.keys(TOKEN) as (keyof Palette)[]) {
    const value = style.getPropertyValue(TOKEN[key]).trim();
    if (value) result[key] = value;
  }
  return result;
}

/**
 * Подпись на карте — буква врат или номер в группе — на тёмной таблетке: иначе она теряется на планете, на диске
 * радара или на другой подписи. Шрифт задаётся каждый раз: состояние холста переживает clearRect, но полагаться
 * на это не стоит.
 */
function label(ctx: CanvasRenderingContext2D, value: string, x: number, y: number, fill: string, dpr: number, palette: Palette): void {
  ctx.save();
  ctx.font = `700 ${Math.round(8 * dpr)}px "Manrope Variable", system-ui, sans-serif`;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  const w = ctx.measureText(value).width + 5 * dpr;
  const h = 10 * dpr;
  ctx.globalAlpha = 0.85;
  ctx.fillStyle = palette.bgWindow;
  roundRect(ctx, x - w / 2, y - h / 2, w, h, h / 2);
  ctx.fill();
  ctx.globalAlpha = 1;
  ctx.fillStyle = fill;
  ctx.fillText(value, x, y + 0.5 * dpr);
  ctx.restore();
}

/** Рамка цели из четырёх уголков — как рамка прицела в мире (playerOverlay.drawFrame). */
function reticle(ctx: CanvasRenderingContext2D, x: number, y: number, half: number, dpr: number, stroke: string): void {
  const c = half * 0.5;
  ctx.strokeStyle = stroke;
  ctx.lineWidth = 1.5 * dpr;
  ctx.beginPath();
  for (const [kx, ky] of [[-1, -1], [1, -1], [1, 1], [-1, 1]]) {
    const cx = x + kx * half;
    const cy = y + ky * half;
    ctx.moveTo(cx - kx * c, cy);
    ctx.lineTo(cx, cy);
    ctx.lineTo(cx, cy - ky * c);
  }
  ctx.stroke();
}

function roundRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number): void {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}
