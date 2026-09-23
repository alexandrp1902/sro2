import { describe, expect, it } from 'vitest';
import { coronaExtent, coronaFrame, coronaRings, coronaStill, discRadius } from './corona';
import meta from './spriteMeta.json';
import { type SpriteName } from './sprites';

/** Картинки звёзд: у каждой своя доля диска, и корону вокруг каждой из них надо проверять отдельно. */
const SUNS: SpriteName[] = ['suns-yellow', 'suns-orange', 'suns-blue', 'suns-red', 'suns-white', 'suns-binary'];

/** Самое тесное отношение burnRadius/radius в shared/galaxy.json — 2.48: корона обязана быть внутри. */
const HEAT = 2.3;

describe('корона звезды', () => {
  it('у каждого вида звезды свои цвета, у незнакомого — жёлтые', () => {
    const blue = coronaRings('blue');
    const yellow = coronaRings('yellow');
    expect(blue.map((r) => r.color)).not.toEqual(yellow.map((r) => r.color));
    expect(coronaRings('невиданная').map((r) => r.color)).toEqual(yellow.map((r) => r.color));
  });

  it('слои идут от широкого к узкому и к краю не становятся плотнее', () => {
    const rings = coronaRings('yellow');
    expect(rings.length).toBe(4);
    for (let i = 1; i < rings.length; i++) {
      expect(coronaExtent(rings[i])).toBeLessThan(coronaExtent(rings[i - 1]));
      // Не строго: основная корона и протуберанцы светят вровень, дальше — только бледнее.
      expect(rings[i].alpha).toBeGreaterThanOrEqual(rings[i - 1].alpha);
    }
    expect(rings[rings.length - 1].alpha).toBeGreaterThan(rings[0].alpha);
    // Самый узкий слой всё же шире диска: иначе корона пряталась бы под картинкой звезды.
    expect(coronaExtent(rings[rings.length - 1])).toBeGreaterThan(1);
  });

  /** Главная беда такой короны: дырка вылезла из-за диска, и посреди звезды дыра с жёсткой кромкой. */
  it('дырка остаётся под диском даже на полном вдохе', () => {
    for (const ring of coronaRings('white')) {
      expect(ring.size * ring.hole * (1 + ring.amplitude), ring.sprite).toBeLessThan(0.95);
    }
  });

  it('корона не выходит из зоны жара ни у одной звезды', () => {
    for (const sun of SUNS) {
      // Радиус диска считается от половины длинной стороны картинки — как её ставит centred().
      const disc = discRadius(sun, 1.3);
      for (const ring of coronaRings('yellow')) {
        expect(coronaExtent(ring) * disc, `${sun} / ${ring.sprite}`).toBeLessThanOrEqual(HEAT);
      }
    }
  });

  it('слой возвращается в то же положение через свой период', () => {
    const ring = coronaRings('yellow')[1];
    const now = coronaFrame(ring, 3);
    const later = coronaFrame(ring, 3 + ring.period);
    expect(later.scale).toBeCloseTo(now.scale, 9);
    expect(later.alpha).toBeCloseTo(now.alpha, 9);
  });

  it('пульсация не разносит слой: масштаб и прозрачность держатся в своих пределах', () => {
    for (const ring of coronaRings('red')) {
      for (let seconds = 0; seconds < 60; seconds += 0.25) {
        const frame = coronaFrame(ring, seconds);
        expect(Math.abs(frame.scale - 1)).toBeLessThanOrEqual(ring.amplitude + 1e-9);
        expect(frame.alpha).toBeGreaterThan(0);
        expect(frame.alpha).toBeLessThanOrEqual(ring.alpha + 1e-9);
      }
    }
  });

  it('слои не дышат в такт', () => {
    const rings = coronaRings('orange');
    for (const seconds of [0, 1.7, 4.2, 9, 20.5]) {
      const scales = rings.map((r) => Math.round(coronaFrame(r, seconds).scale * 1000));
      expect(new Set(scales).size, `в ${seconds} с слои совпали`).toBeGreaterThan(1);
    }
  });

  it('спокойный кадр стоит на месте', () => {
    const ring = coronaRings('white')[0];
    expect(coronaStill(ring)).toEqual({ scale: 1, alpha: ring.alpha, rotation: 0 });
  });

  /** Орбитальное время — unix-секунды: от их величины кадр зависеть не должен. */
  it('держится на unix-времени', () => {
    const now = 1.7e9;
    for (const ring of coronaRings('binary')) {
      const frame = coronaFrame(ring, now);
      expect(Number.isFinite(frame.rotation)).toBe(true);
      expect(frame.rotation).toBeGreaterThanOrEqual(0);
      expect(frame.rotation).toBeLessThan(Math.PI * 2);
      expect(coronaFrame(ring, now + ring.period).scale).toBeCloseTo(frame.scale, 9);
    }
  });

  /**
   * Кольца режутся режимом «ring» (tools/sprites.py): поля не обрезаются, картинка остаётся
   * квадратной и концентричной. Потеряется режим — обрезка по альфе сдвинет центр, и доли дырки,
   * на которых держатся все размеры выше, станут неправдой.
   */
  it('кольца нарезаны квадратными', () => {
    for (const ring of coronaRings('yellow')) {
      const size = (meta as Record<string, { w: number; h: number }>)[ring.sprite];
      expect(size, ring.sprite).toBeDefined();
      expect(size.w, ring.sprite).toBe(size.h);
    }
  });
});
