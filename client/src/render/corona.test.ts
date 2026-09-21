import { describe, expect, it } from 'vitest';
import { coronaFrame, coronaLayers, coronaStill } from './corona';

describe('корона звезды', () => {
  it('у каждого вида звезды свои цвета, у незнакомого — жёлтые', () => {
    const blue = coronaLayers('blue');
    const yellow = coronaLayers('yellow');
    expect(blue.map((l) => l.color)).not.toEqual(yellow.map((l) => l.color));
    expect(coronaLayers('невиданная').map((l) => l.color)).toEqual(yellow.map((l) => l.color));
  });

  it('слои идут наружу и к краю становятся бледнее: ближний к диску — самый плотный', () => {
    const layers = coronaLayers('yellow');
    expect(layers.length).toBeGreaterThan(2);
    for (let i = 1; i < layers.length; i++) {
      expect(layers[i].radius).toBeGreaterThan(layers[i - 1].radius);
      expect(layers[i].alpha).toBeLessThan(layers[i - 1].alpha);
    }
    // Самый внутренний слой всё же шире диска: иначе корона пряталась бы под картинкой звезды.
    expect(layers[0].radius).toBeGreaterThan(1);
  });

  it('слой возвращается в то же положение через свой период', () => {
    const layer = coronaLayers('yellow')[1];
    const now = coronaFrame(layer, 3);
    const later = coronaFrame(layer, 3 + layer.period);
    expect(later.sx).toBeCloseTo(now.sx, 9);
    expect(later.sy).toBeCloseTo(now.sy, 9);
    expect(later.alpha).toBeCloseTo(now.alpha, 9);
  });

  /** Ровно то, чего просил заказчик: не жёсткие геометрические круги, движущиеся как один. */
  it('слои не дышат в такт и ни один не остаётся ровным кругом', () => {
    const layers = coronaLayers('orange');
    for (const seconds of [0, 1.7, 4.2, 9, 20.5]) {
      const frames = layers.map((l) => coronaFrame(l, seconds));
      const scales = frames.map((f) => Math.round(f.sx * 1000));
      expect(new Set(scales).size, `в ${seconds} с слои совпали`).toBeGreaterThan(1);
      for (const frame of frames) expect(frame.sx).not.toBeCloseTo(frame.sy, 3);
    }
  });

  it('пульсация не разносит слой: масштаб и прозрачность держатся в своих пределах', () => {
    for (const layer of coronaLayers('red')) {
      for (let seconds = 0; seconds < 60; seconds += 0.25) {
        const frame = coronaFrame(layer, seconds);
        expect(Math.abs(frame.sx - 1)).toBeLessThanOrEqual(layer.amplitude + 1e-9);
        expect(frame.alpha).toBeGreaterThan(0);
        expect(frame.alpha).toBeLessThanOrEqual(layer.alpha + 1e-9);
      }
    }
  });

  it('спокойный кадр стоит на месте: ни поворота, ни растяжения', () => {
    const layer = coronaLayers('white')[0];
    const still = coronaStill(layer);
    expect(still).toEqual({ sx: 1, sy: layer.squash, alpha: layer.alpha, rotation: 0 });
  });
});
