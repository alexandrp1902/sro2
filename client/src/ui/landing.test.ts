import { describe, expect, it } from 'vitest';
import { Landing, landingArt } from './landing';

describe('landingArt', () => {
  it('даёт кадр снижения каждому нарисованному виду планеты', () => {
    expect(landingArt('terran')).toBe('dock/landing-terran.webp');
    expect(landingArt('lava')).toBe('dock/landing-lava.webp');
    expect(landingArt('ice')).toBe('dock/landing-ice.webp');
  });

  it('у газового гиганта кадр орбитальной платформы: на него самого не садятся', () => {
    expect(landingArt('gas')).toBe('dock/landing-orbital-platform.webp');
  });

  /**
   * Три секунды — требование плейтеста (M16a): за прежние 1.4 с кадр не успевали рассмотреть.
   * Столько же стоит в стиле переменной --landing-ms, и ставит её сам Landing.show.
   */
  it('заставка идёт около трёх секунд', () => {
    expect(Landing.totalMs).toBe(3000);
    expect(Landing.fadeMs).toBeLessThan(Landing.totalMs);
  });

  it('чего не нарисовали, того и не показываем', () => {
    expect(landingArt('ringed')).toBeNull(); // кадра для кольчатой нет
    expect(landingArt('unknown')).toBeNull();
    expect(landingArt(null)).toBeNull();
    expect(landingArt(undefined)).toBeNull();
  });

  /**
   * Нет кадра — нет и заставки (M16a): держать три секунды пустой чёрный экран значило бы
   * придумывать задержку там, где смотреть не на что. Landing.show на null из landingArt выходит сразу.
   */
  it('отсутствие кадра — это отсутствие заставки, а не пауза', () => {
    expect(landingArt('ringed')).toBeNull();
  });

  /**
   * Виды, на которых в galaxy.json стоят поселения, — те, куда вообще можно сесть. У каждого кадр обязан
   * быть: без него посадка пройдёт через затемнение, и это заметят только в плейтесте. Появится поселение
   * на новом виде — добавить его и сюда, и в LANDING_ART (galaxy.json клиент не читает: в нём комментарии).
   */
  it('у каждого обитаемого вида планеты кадр есть', () => {
    for (const kind of ['terran', 'ice', 'barren', 'desert', 'gas', 'jungle', 'lava']) {
      expect(landingArt(kind), `вид «${kind}» без кадра снижения`).not.toBeNull();
    }
  });
});
