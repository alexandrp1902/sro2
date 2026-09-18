// Зеркало server/Sro.Sim/MeteorRules.cs — та часть, которая нужна клиенту: вид, размер, прочность и полёт.
// Полёт считают обе стороны, поэтому схема шага закреплена вектором shared/test-vectors/meteors.json.

/** Центр системы: к нему тянет камни. Зеркало SimConfig.StationX/Y и STATION в game/layout.ts. */
const CENTER = { x: 0, y: 0 };

export interface MeteorSize {
  name: string;
  /** Радиус столкновения, он же размер на экране. */
  radius: number;
  hp: number;
  speedMin: number;
  speedMax: number;
  ramDamage: number;
  weight: number;
  table?: string | null;
}

/** Тип траектории: насколько близко к центру камень целится и как быстро идёт. Клиенту нужен только для справки. */
export interface MeteorTrack {
  name: string;
  aimFactor: number;
  speedFactor: number;
  weight: number;
}

export interface MeteorRules {
  maxAlive: number;
  /** Параметр тяготения центра системы, GM: ускорение камня — gravity / r². */
  gravity: number;
  /** Ближе этого тяготение перестаёт расти. */
  gravityMinRadius: number;
  /** Сервер может прислать null, если метеоритов нет. */
  sizes?: Record<string, MeteorSize> | null;
  tracks?: Record<string, MeteorTrack> | null;
}

/** Метеоритов нет: до welcome и на серверах без meteors.json. */
export const NO_METEORS: MeteorRules = {
  maxAlive: 0,
  gravity: 0,
  gravityMinRadius: 600,
  sizes: null,
  tracks: null,
};

/** Размер неизвестен (баланс поменялся, пока камень летит) — рисуем средним. */
export const FALLBACK_SIZE: MeteorSize = {
  name: 'Метеорит',
  radius: 22,
  hp: 1,
  speedMin: 0,
  speedMax: 0,
  ramDamage: 0,
  weight: 0,
};

export function meteorSize(rules: MeteorRules, id: string): MeteorSize {
  return rules.sizes?.[id] ?? FALLBACK_SIZE;
}

/** Состояние камня в полёте. */
export interface MeteorState {
  x: number;
  y: number;
  vx: number;
  vy: number;
}

/** Шаг симуляции сервера, с. Зеркало SimConfig.Dt. */
const DT = 0.05;
/** Предел экстраполяции: снапшоты перестали приходить — камень не улетает за экран сам по себе. */
const MAX_AHEAD_SECONDS = 1;

/** Тяготение центра системы в точке (x, y). Зеркало MeteorRules.Pull. */
export function pull(rules: MeteorRules, x: number, y: number): { ax: number; ay: number } {
  if (!(rules.gravity > 0)) return { ax: 0, ay: 0 };
  const dx = x - CENTER.x;
  const dy = y - CENTER.y;
  const r = Math.hypot(dx, dy);
  if (r < 1e-9) return { ax: 0, ay: 0 };
  const soft = Math.max(r, rules.gravityMinRadius);
  const scale = -rules.gravity / (soft * soft * soft);
  return { ax: dx * scale, ay: dy * scale };
}

/** Один шаг полёта: полуявный Эйлер, как на сервере (MeteorRules.Step). */
export function step(rules: MeteorRules, s: MeteorState, dt: number): MeteorState {
  const { ax, ay } = pull(rules, s.x, s.y);
  const vx = s.vx + ax * dt;
  const vy = s.vy + ay * dt;
  return { x: s.x + vx * dt, y: s.y + vy * dt, vx, vy };
}

/**
 * Где камень через seconds после снапшота. Идём целыми тиками сервера и добираем остаток — так дуга на экране
 * повторяет серверную, а не расходится с ней: прямой формулы для полёта в поле тяготения нет.
 */
export function advance(rules: MeteorRules, from: MeteorState, seconds: number): MeteorState {
  const total = Math.max(0, Math.min(MAX_AHEAD_SECONDS, seconds));
  let s = from;
  let left = total;
  while (left > DT) {
    s = step(rules, s, DT);
    left -= DT;
  }
  return left > 0 ? step(rules, s, left) : s;
}

/** Точка поверхности камня в его собственных осях. */
export interface Point3 {
  x: number;
  y: number;
  z: number;
}

/**
 * Облако точек неровного камня: вершины по сфере со случайным радиусом. Форма зависит только от id,
 * поэтому один и тот же камень у всех игроков выглядит одинаково и ни байта не едет по сети.
 */
export function rockPoints(id: number, radius: number, count = 26): Point3[] {
  const random = seeded(id);
  const points: Point3[] = [];
  for (let i = 0; i < count; i++) {
    // Спираль Фибоначчи: точки ложатся по сфере ровно, без сгустков у полюсов.
    const z = 1 - (2 * (i + 0.5)) / count;
    const ring = Math.sqrt(Math.max(0, 1 - z * z));
    const angle = i * 2.399963229728653;
    const r = radius * (0.74 + random() * 0.34);
    points.push({ x: Math.cos(angle) * ring * r, y: Math.sin(angle) * ring * r, z: z * r });
  }
  return points;
}

/** Угловая скорость камня по трём осям, рад/с: кувыркается, а не крутится в плоскости экрана. */
export function spinFor(id: number): Point3 {
  const random = seeded(id * 7919 + 13);
  const rate = () => (random() < 0.5 ? -1 : 1) * (0.15 + random() * 0.65);
  return { x: rate(), y: rate(), z: rate() };
}

/** Поворот облака точек на углы вокруг трёх осей — обычная матрица поворота, применённая по очереди. */
export function rotatePoints(points: readonly Point3[], ax: number, ay: number, az: number): Point3[] {
  const [sx, cx] = [Math.sin(ax), Math.cos(ax)];
  const [sy, cy] = [Math.sin(ay), Math.cos(ay)];
  const [sz, cz] = [Math.sin(az), Math.cos(az)];
  return points.map((p) => {
    const y1 = p.y * cx - p.z * sx;
    const z1 = p.y * sx + p.z * cx;
    const x2 = p.x * cy + z1 * sy;
    const z2 = -p.x * sy + z1 * cy;
    return { x: x2 * cz - y1 * sz, y: x2 * sz + y1 * cz, z: z2 };
  });
}

/**
 * Силуэт повёрнутого камня — выпуклая оболочка проекции (обход Эндрю).
 * @returns плоский массив x0, y0, x1, y1… против часовой стрелки
 */
export function silhouette(points: readonly Point3[]): number[] {
  const sorted = points.map((p) => [p.x, p.y] as const).sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  if (sorted.length < 3) return sorted.flatMap(([x, y]) => [x, y]);

  const cross = (o: readonly number[], a: readonly number[], b: readonly number[]) =>
    (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const build = (source: readonly (readonly number[])[]) => {
    const chain: (readonly number[])[] = [];
    for (const p of source) {
      while (chain.length >= 2 && cross(chain[chain.length - 2], chain[chain.length - 1], p) <= 0) chain.pop();
      chain.push(p);
    }
    chain.pop();
    return chain;
  };
  return [...build(sorted), ...build([...sorted].reverse())].flatMap(([x, y]) => [x, y]);
}

/**
 * Отсекает от многоугольника всё по одну сторону прямой (алгоритм Сазерленда — Ходжмана).
 * Прямая задана нормалью (nx, ny) и смещением: остаётся часть, где n·p >= offset.
 * Этим из силуэта вырезается ночная сторона камня — она и делает его объёмным, а не плоским.
 * @param poly плоский массив x0, y0, x1, y1…
 */
export function clipHalfPlane(poly: readonly number[], nx: number, ny: number, offset: number): number[] {
  const out: number[] = [];
  const count = poly.length / 2;
  if (count < 3) return out;

  for (let i = 0; i < count; i++) {
    const ax = poly[i * 2];
    const ay = poly[i * 2 + 1];
    const j = (i + 1) % count;
    const bx = poly[j * 2];
    const by = poly[j * 2 + 1];
    const da = ax * nx + ay * ny - offset;
    const db = bx * nx + by * ny - offset;

    if (da >= 0) out.push(ax, ay);
    if (da >= 0 !== db >= 0) {
      const t = da / (da - db);
      out.push(ax + (bx - ax) * t, ay + (by - ay) * t);
    }
  }
  return out;
}

/** mulberry32: маленький детерминированный генератор, одинаковый в любом браузере. */
function seeded(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
