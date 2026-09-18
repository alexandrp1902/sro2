// Зеркало server/Sro.Sim/MeteorRules.cs — та часть, которая нужна клиенту: вид, размер, прочность
// и пороги предупреждения о таране. Появление, трассы и сам таран считает только сервер.

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

export interface MeteorRules {
  maxAlive: number;
  /** Предупреждать о таране, если до касания меньше стольких секунд. */
  warnSeconds: number;
  /** …и разминуться выходит ближе (сумма радиусов × этот множитель). */
  warnMissFactor: number;
  /** Сервер может прислать null, если метеоритов нет. */
  sizes?: Record<string, MeteorSize> | null;
}

/** Метеоритов нет: до welcome и на серверах без meteors.json. */
export const NO_METEORS: MeteorRules = {
  maxAlive: 0,
  warnSeconds: 4,
  warnMissFactor: 1.6,
  sizes: null,
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

/** Предел экстраполяции: снапшоты перестали приходить — камень не улетает за экран сам по себе. */
const MAX_AHEAD_SECONDS = 1;

/**
 * Где метеорит через seconds после снапшота. Он летит строго по прямой с постоянной скоростью, поэтому это
 * точное положение, а не догадка — буфер кадров для интерполяции не нужен.
 */
export function positionAt(
  m: { x: number; y: number; vx: number; vy: number },
  seconds: number,
): { x: number; y: number } {
  const t = Math.max(0, Math.min(MAX_AHEAD_SECONDS, seconds));
  return { x: m.x + m.vx * t, y: m.y + m.vy * t };
}

/** Тело для проверки сближения: центр, скорость и радиус. */
export interface Body {
  x: number;
  y: number;
  vx: number;
  vy: number;
  size: number;
}

export interface Risk {
  /** Секунд до касания; 0 — уже касаемся. */
  seconds: number;
  /** На каком расстоянии между центрами разойдёмся, если никто не свернёт. */
  miss: number;
}

/** Относительная скорость ниже этой — «стоим друг относительно друга», сближения нет. */
const STILL_SPEED_SQ = 1e-6;

/**
 * Опасен ли курс метеорита для корабля, если оба не свернут: сближение по прямым, ближайшая точка
 * t* = −(p·v)/(v·v), промах |p + v·t*|. Опасно, когда промах не больше суммы радиусов × warnMissFactor,
 * а до касания не больше warnSeconds. Расходятся или стоят — null.
 */
export function interceptRisk(own: Body, m: Body, rules: Pick<MeteorRules, 'warnSeconds' | 'warnMissFactor'>): Risk | null {
  const px = m.x - own.x;
  const py = m.y - own.y;
  const vx = m.vx - own.vx;
  const vy = m.vy - own.vy;
  const vv = vx * vx + vy * vy;
  if (vv < STILL_SPEED_SQ) return null;

  const closest = -(px * vx + py * vy) / vv;
  if (closest < 0) return null; // уже расходимся
  const miss = Math.hypot(px + vx * closest, py + vy * closest);
  const reach = own.size + m.size;
  if (miss > reach * rules.warnMissFactor) return null;

  // Касание раньше ближайшей точки на половину хорды; проходит мимо — считаем до ближайшей точки.
  const chord = miss < reach ? Math.sqrt(reach * reach - miss * miss) / Math.sqrt(vv) : 0;
  const seconds = Math.max(0, closest - chord);
  if (seconds > rules.warnSeconds) return null;
  return { seconds, miss };
}

/**
 * Неровный многоугольник камня — вершины по кругу с разным радиусом. Форма зависит только от id:
 * у всех игроков один и тот же метеорит выглядит одинаково, и ни байта в протоколе.
 * @returns плоский массив x0, y0, x1, y1…
 */
export function shapeFor(id: number, radius: number, vertices = 9): number[] {
  const random = seeded(id);
  const points: number[] = [];
  for (let i = 0; i < vertices; i++) {
    const angle = (i / vertices) * Math.PI * 2 + (random() - 0.5) * 0.35;
    const r = radius * (0.78 + random() * 0.3);
    points.push(Math.cos(angle) * r, Math.sin(angle) * r);
  }
  return points;
}

/** Скорость вращения камня, рад/с: тоже от id, со знаком — одни крутятся по часовой, другие против. */
export function spinFor(id: number): number {
  const random = seeded(id * 7919 + 13);
  return (random() < 0.5 ? -1 : 1) * (0.4 + random() * 1.1);
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
