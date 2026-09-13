import type { Hulls } from '../sim/hulls';
import { DT, copyState, step, wrapAngle, type MoveInput, type ShipState } from '../sim/movement';
import type { ShipDto } from './protocol';

/** §52: при большом расхождении состояние сервера применяется сразу. */
const SNAP_DISTANCE = 100;
/** Малая коррекция гаснет плавно с этой постоянной времени, с. */
const CORRECTION_TAU = 0.1;
const MAX_PENDING = 100;

/**
 * Предсказание своего корабля. Каждый шаг клиента — один вход серверу; сервер делает ровно один шаг
 * на вход и возвращает ack. Снапшот = состояние после входа ack, поэтому неподтверждённые входы
 * переигрываются поверх него, и при совпадении моделей коррекция нулевая.
 */
export class Prediction {
  /** Состояния двух последних шагов: рендер интерполирует между ними. */
  readonly prev: ShipState;
  readonly curr: ShipState;
  hullId: string;
  lastCorrection = 0;
  snaps = 0;

  private pending: { seq: number; input: MoveInput }[] = [];
  private seq = 0;
  private synced = false;
  private peakCorrection = 0;
  private readonly offset = { x: 0, y: 0, rot: 0 };

  constructor(
    private readonly hulls: Hulls,
    hullId: string,
    spawn: { x: number; y: number },
  ) {
    this.hullId = hullId;
    this.prev = { x: spawn.x, y: spawn.y, rot: 0, vx: 0, vy: 0 };
    this.curr = { ...this.prev };
  }

  /** Сверено ли состояние с сервером; нет — корабль летает локально. */
  get isSynced(): boolean {
    return this.synced;
  }

  get pendingCount(): number {
    return this.pending.length;
  }

  /** @param send отправка входа серверу; null — связи нет, летим локально */
  step(input: MoveInput, send: ((seq: number, input: MoveInput) => void) | null): void {
    copyState(this.prev, this.curr);
    step(this.curr, input, this.hulls.get(this.hullId), DT);
    if (!send) return;
    this.seq++;
    this.pending.push({ seq: this.seq, input });
    if (this.pending.length > MAX_PENDING) this.pending.shift();
    send(this.seq, input);
  }

  /** Новая сессия с сервером: входы нумеруются заново, первый снапшот принимается как есть. */
  resetNet(): void {
    this.seq = 0;
    this.pending = [];
    this.synced = false;
  }

  reconcile(ship: ShipDto): void {
    this.hullId = ship.hull;
    while (this.pending.length > 0 && this.pending[0].seq <= ship.ack) this.pending.shift();

    const state: ShipState = { x: ship.x, y: ship.y, rot: ship.r, vx: ship.vx, vy: ship.vy };
    const hull = this.hulls.get(ship.hull);
    for (const { input } of this.pending) step(state, input, hull, DT);

    if (!this.synced) {
      this.synced = true;
      copyState(this.prev, state);
      copyState(this.curr, state);
      this.offset.x = this.offset.y = this.offset.rot = 0;
      return;
    }

    const ex = state.x - this.curr.x;
    const ey = state.y - this.curr.y;
    const er = wrapAngle(state.rot - this.curr.rot);
    const error = Math.hypot(ex, ey);
    this.lastCorrection = error;
    this.peakCorrection = Math.max(this.peakCorrection, error);

    if (error > SNAP_DISTANCE) {
      this.snaps++;
      this.offset.x = this.offset.y = this.offset.rot = 0;
    } else {
      // Картинка остаётся на месте, а разница догоняется за ~CORRECTION_TAU.
      this.offset.x -= ex;
      this.offset.y -= ey;
      this.offset.rot -= er;
    }
    // Оба кадра интерполяции сдвигаются вместе, иначе lerp между ними прыгнет.
    this.prev.x += ex;
    this.prev.y += ey;
    this.prev.rot += er;
    copyState(this.curr, state);
  }

  /** Наибольшая коррекция с прошлого вызова — для dev-панели. */
  takePeakCorrection(): number {
    const peak = this.peakCorrection;
    this.peakCorrection = 0;
    return peak;
  }

  /** Состояние для рендера: интерполяция между шагами плюс гаснущее смещение коррекции. */
  render(alpha: number, frameSeconds: number): ShipState {
    const decay = Math.exp(-frameSeconds / CORRECTION_TAU);
    this.offset.x *= decay;
    this.offset.y *= decay;
    this.offset.rot *= decay;
    const { prev, curr } = this;
    return {
      x: prev.x + (curr.x - prev.x) * alpha + this.offset.x,
      y: prev.y + (curr.y - prev.y) * alpha + this.offset.y,
      rot: prev.rot + wrapAngle(curr.rot - prev.rot) * alpha + this.offset.rot,
      vx: curr.vx,
      vy: curr.vy,
    };
  }
}
