// Модель полёта (боевой документ v0.2, §1–18, §55). Построчное зеркало server/Sro.Sim/Movement.cs:
// порядок операций совпадает, чтобы предсказание клиента сходилось с сервером. Сверка — shared/test-vectors.

export const TICK_RATE = 20;
export const DT = 1 / TICK_RATE;

/** Мир — квадрат ±WORLD_HALF_SIZE. */
export const WORLD_HALF_SIZE = 4000;

const TAU = 2 * Math.PI;
const DEG_TO_RAD = Math.PI / 180;
const DIRECTION_EPSILON = 1e-6;
/** Остаток бокового скольжения ниже этого гасится в ноль, иначе корабль вечно «ползёт». */
const LATERAL_STOP_SPEED = 0.5;

export interface HullParams {
  name: string;
  maxSpeed: number;
  acceleration: number;
  brakeAcceleration: number;
  /** Градусы в секунду. */
  turnRate: number;
  /** За это время боковая скорость гаснет примерно до 5%. */
  lateralDampTime: number;
  /** Доля погашенной боковой скорости, переходящая в продольную (0 — выключено). */
  lateralToForward: number;
  size: number;
  /** Прочность корпуса. */
  hp: number;
  /** Ёмкость щита: урон сначала снимает щит (GDD §17). */
  shield: number;
  /** Восстановление щита в секунду после паузы без урона. */
  shieldRegen: number;
  /** Базовое уклонение, % (боевой документ §39). */
  evasion: number;
  /** Добавка к уклонению на полной скорости, % (§40). */
  moveEvasion: number;
}

export type HullConfig = Record<string, HullParams>;

/** rot = 0 — нос вверх; экранные координаты, y вниз. */
export interface ShipState {
  x: number;
  y: number;
  rot: number;
  vx: number;
  vy: number;
}

/** Желаемое направление на экране (единичный вектор или ноль) и тяга 0…1. */
export interface MoveInput {
  dx: number;
  dy: number;
  throttle: number;
}

/** Угол в диапазон [−π, π). */
export function wrapAngle(a: number): number {
  return a - TAU * Math.floor((a + Math.PI) / TAU);
}

export function moveTowardsAngle(current: number, target: number, maxDelta: number): number {
  const diff = wrapAngle(target - current);
  if (Math.abs(diff) <= maxDelta) return wrapAngle(target);
  return wrapAngle(current + (diff > 0 ? maxDelta : -maxDelta));
}

/** Угол носа, при котором корабль смотрит в направлении (dx, dy). */
export function directionAngle(dx: number, dy: number): number {
  return Math.atan2(dx, -dy);
}

/** Один шаг симуляции. Меняет state на месте. */
export function step(s: ShipState, input: MoveInput, hull: HullParams, dt: number): void {
  // 1. Разворот носом к желаемому направлению с ограничением TurnRate.
  if (input.dx * input.dx + input.dy * input.dy > DIRECTION_EPSILON) {
    const desired = Math.atan2(input.dx, -input.dy);
    s.rot = moveTowardsAngle(s.rot, desired, hull.turnRate * DEG_TO_RAD * dt);
  }

  // 2. Скорость в осях корабля: forward = (fx, fy), right = (−fy, fx).
  const fx = Math.sin(s.rot);
  const fy = -Math.cos(s.rot);
  let vf = s.vx * fx + s.vy * fy;
  const vl = s.vx * -fy + s.vy * fx;

  // 3. Продольная скорость тянется к MaxSpeed·Throttle; через ноль не перескакивает.
  //    Тяга не разгоняет суммарную скорость (вместе с заносом) выше MaxSpeed·Throttle, но и не отнимает набранную.
  const throttle = Math.min(1, Math.max(0, input.throttle));
  const target = hull.maxSpeed * throttle;
  const lateral = Math.abs(vl);
  if (vf < 0) vf = Math.min(0, vf + hull.brakeAcceleration * dt);
  else if (vf > target) vf = Math.max(target, vf - hull.brakeAcceleration * dt);
  else {
    const room = Math.sqrt(Math.max(0, target * target - vl * vl));
    if (vf < room) vf = Math.min(room, vf + hull.acceleration * dt);
  }

  // 4. Стабилизация бокового скольжения; без тяги тормозит сильнее.
  let damped = lateral * Math.exp((-3 * dt) / hull.lateralDampTime);
  if (throttle === 0) damped = Math.min(damped, lateral - hull.brakeAcceleration * dt);
  if (damped < LATERAL_STOP_SPEED) damped = 0;
  if (hull.lateralToForward > 0 && vf >= 0) {
    const room = Math.sqrt(Math.max(0, target * target - damped * damped));
    if (vf < room) vf = Math.min(room, vf + (lateral - damped) * hull.lateralToForward);
  }
  const newVl = vl < 0 ? -damped : damped;

  s.vx = fx * vf - fy * newVl;
  s.vy = fy * vf + fx * newVl;
  s.x += s.vx * dt;
  s.y += s.vy * dt;

  // 5. Граница мира: упираемся, наружная скорость гасится.
  if (s.x > WORLD_HALF_SIZE) {
    s.x = WORLD_HALF_SIZE;
    if (s.vx > 0) s.vx = 0;
  } else if (s.x < -WORLD_HALF_SIZE) {
    s.x = -WORLD_HALF_SIZE;
    if (s.vx < 0) s.vx = 0;
  }
  if (s.y > WORLD_HALF_SIZE) {
    s.y = WORLD_HALF_SIZE;
    if (s.vy > 0) s.vy = 0;
  } else if (s.y < -WORLD_HALF_SIZE) {
    s.y = -WORLD_HALF_SIZE;
    if (s.vy < 0) s.vy = 0;
  }
}

/** Продольная и боковая скорость относительно носа — для HUD и dev-панели. */
export function localVelocity(s: ShipState): { forward: number; lateral: number } {
  const fx = Math.sin(s.rot);
  const fy = -Math.cos(s.rot);
  return { forward: s.vx * fx + s.vy * fy, lateral: s.vx * -fy + s.vy * fx };
}

export function copyState(to: ShipState, from: ShipState): void {
  to.x = from.x;
  to.y = from.y;
  to.rot = from.rot;
  to.vx = from.vx;
  to.vy = from.vy;
}
