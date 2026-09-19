import type { GateDto, PirateBaseDto } from '../net/protocol';
import { WORLD_HALF_SIZE } from '../sim/movement';

const RENDER_INTERVAL_MS = 100;

const COLORS = {
  background: 'rgba(10, 16, 28, 0.72)',
  border: 'rgba(143, 180, 232, 0.35)',
  radar: 'rgba(111, 168, 255, 0.10)',
  radarEdge: 'rgba(111, 168, 255, 0.45)',
  station: '#6fa8ff',
  star: '#ffc46b',
  planet: '#9fc7a8',
  pirateBase: '#ff7a6b',
  gate: '#b58cff',
  own: '#7fd4ff',
  player: '#ffb45a',
  pirate: '#ff6b5a',
  drone: '#9ccf9a',
  trader: '#e8c95a',
  missile: '#ff4a3a',
  target: '#ffffff',
  objective: '#ffd166',
};

/** Точка на миникарте: корабль в радаре. */
export interface MinimapShip {
  id: number;
  x: number;
  y: number;
  kind: 'player' | 'pirate' | 'drone' | 'trader';
  dead: boolean;
}

export interface MinimapFrame {
  /** Есть ли звезда в центре (без сервера — нет). */
  sun: boolean;
  /** Где сейчас станция на орбите; null — станции нет. */
  station: { x: number; y: number } | null;
  planets: readonly { x: number; y: number }[];
  pirateBase: PirateBaseDto | null;
  gates: readonly GateDto[];
  own: { x: number; y: number; rot: number } | null;
  /** Радиус радара: всё, что сервер присылает, — внутри него (GDD §10). */
  radar: number;
  ships: Iterable<MinimapShip>;
  targetId: number;
  /** Цель задания или обучения — золотое кольцо; null — нет. */
  objective?: { x: number; y: number } | null;
  /** Ракеты в полёте. */
  missiles?: readonly { x: number; y: number }[];
}

/**
 * Миникарта (GDD §43): вся система целиком — звезда, станция и планеты на орбитах, пиратская база, врата,
 * круг радара и корабли в нём.
 * Врата видны всегда: это карта, а не радар. Тап открывает карту галактики.
 */
export class Minimap {
  private readonly ctx: CanvasRenderingContext2D;
  private lastRender = 0;
  private size = 0;

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

    const { ctx, size } = this;
    const scale = size / (WORLD_HALF_SIZE * 2);
    const px = (v: number) => size / 2 + v * scale;
    const dpr = size / this.canvas.clientWidth || 1;

    ctx.clearRect(0, 0, size, size);
    ctx.fillStyle = COLORS.background;
    roundRect(ctx, 0, 0, size, size, 10 * dpr);
    ctx.fill();
    ctx.strokeStyle = COLORS.border;
    ctx.lineWidth = dpr;
    ctx.stroke();

    ctx.save();
    roundRect(ctx, 0, 0, size, size, 10 * dpr);
    ctx.clip();

    if (frame.own) {
      ctx.beginPath();
      ctx.arc(px(frame.own.x), px(frame.own.y), frame.radar * scale, 0, 2 * Math.PI);
      ctx.fillStyle = COLORS.radar;
      ctx.fill();
      ctx.strokeStyle = COLORS.radarEdge;
      ctx.lineWidth = dpr;
      ctx.stroke();
    }

    if (frame.sun) {
      ctx.beginPath();
      ctx.arc(px(0), px(0), 4 * dpr, 0, 2 * Math.PI);
      ctx.fillStyle = COLORS.star;
      ctx.fill();
    }

    for (const planet of frame.planets) {
      ctx.beginPath();
      ctx.arc(px(planet.x), px(planet.y), 2.5 * dpr, 0, 2 * Math.PI);
      ctx.fillStyle = COLORS.planet;
      ctx.fill();
    }

    if (frame.pirateBase) {
      const s = 3.5 * dpr;
      ctx.fillStyle = COLORS.pirateBase;
      ctx.fillRect(px(frame.pirateBase.x) - s, px(frame.pirateBase.y) - s, s * 2, s * 2);
    }

    if (frame.station) {
      const s = 3 * dpr;
      ctx.fillStyle = COLORS.station;
      ctx.fillRect(px(frame.station.x) - s, px(frame.station.y) - s, s * 2, s * 2);
    }

    for (const gate of frame.gates) {
      ctx.beginPath();
      ctx.arc(px(gate.x), px(gate.y), 3.5 * dpr, 0, 2 * Math.PI);
      ctx.strokeStyle = COLORS.gate;
      ctx.lineWidth = 2 * dpr;
      ctx.stroke();
    }

    for (const ship of frame.ships) {
      if (ship.dead) continue;
      ctx.beginPath();
      ctx.arc(px(ship.x), px(ship.y), 2 * dpr, 0, 2 * Math.PI);
      ctx.fillStyle = COLORS[ship.kind];
      ctx.fill();
      if (ship.id === frame.targetId) {
        ctx.strokeStyle = COLORS.target;
        ctx.lineWidth = dpr;
        ctx.beginPath();
        ctx.arc(px(ship.x), px(ship.y), 4.5 * dpr, 0, 2 * Math.PI);
        ctx.stroke();
      }
    }

    // Ракеты — крошечные красные точки: видно, что к тебе что-то летит.
    for (const missile of frame.missiles ?? []) {
      const s = 1.2 * dpr;
      ctx.fillStyle = COLORS.missile;
      ctx.fillRect(px(missile.x) - s, px(missile.y) - s, s * 2, s * 2);
    }

    if (frame.objective) {
      ctx.beginPath();
      ctx.arc(px(frame.objective.x), px(frame.objective.y), 6 * dpr, 0, 2 * Math.PI);
      ctx.strokeStyle = COLORS.objective;
      ctx.lineWidth = 1.5 * dpr;
      ctx.stroke();
    }

    if (frame.own) {
      // Свой корабль — стрелка носом по курсу.
      const { x, y, rot } = frame.own;
      ctx.save();
      ctx.translate(px(x), px(y));
      ctx.rotate(rot);
      ctx.beginPath();
      ctx.moveTo(0, -5 * dpr);
      ctx.lineTo(3.5 * dpr, 4 * dpr);
      ctx.lineTo(-3.5 * dpr, 4 * dpr);
      ctx.closePath();
      ctx.fillStyle = COLORS.own;
      ctx.fill();
      ctx.restore();
    }
    ctx.restore();
  }

  /** Холст в пикселях экрана: иначе на Retina точки размыты. */
  private resize(): void {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const size = Math.round(this.canvas.clientWidth * dpr);
    if (size === this.size || size === 0) return;
    this.size = this.canvas.width = this.canvas.height = size;
  }
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
