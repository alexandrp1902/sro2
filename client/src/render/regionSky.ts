import { Assets, Container, Texture, TilingSprite } from 'pixi.js';

/** У какого региона какой фон; чего нет в списке — фона нет, небо рисует только туманность. */
const REGIONS = new Set(['core', 'frontier', 'rim']);

/** Плитка нарисована бесшовной; этот размер она занимает на экране при зуме 1. */
const TILE = 1024;
/** Самый дальний слой: почти стоит на месте, и от этого кажется бесконечно далёким. */
const PARALLAX = 0.06;
/** Фон непрозрачный, поэтому приглушаем: звёзды и туманность поверх должны остаться главными. */
const ALPHA = 0.55;
const FADE_MS = 800;

/**
 * Нарисованный фон региона (пачка арта B): дальний план за звёздами. Ядро — спокойная синева,
 * Пограничье и Рубеж — свои оттенки, так что регион виден, не открывая карту.
 *
 * Это экранный слой с параллаксом, как звёзды, а не часть мира: он непрозрачный и, попади он в world,
 * закрыл бы собой звёздное небо под ним. Систему от системы отличает туманность поверх — она своя у каждой.
 */
export class RegionSky {
  readonly view = new Container();
  private sprite: TilingSprite | null = null;
  private readyAt = 0;
  private cancelled = false;

  constructor(region: string | null | undefined) {
    if (!region || !REGIONS.has(region)) return;
    void Assets.load<Texture>(`sky/bg-${region}.webp`)
      .then((texture) => {
        if (this.cancelled) return;
        const sprite = new TilingSprite({ texture, width: 1, height: 1 });
        sprite.alpha = 0;
        this.view.addChild(sprite);
        this.sprite = sprite;
        this.readyAt = performance.now();
      })
      .catch(() => {
        // Картинки нет — остаёмся без дальнего плана: небо по-прежнему рисует туманность.
      });
  }

  /** Слой сдвигается на малую долю сдвига мира — как дальние звёзды, только ещё медленнее. */
  update(cameraX: number, cameraY: number, zoom: number, width: number, height: number, now: number): void {
    const sprite = this.sprite;
    if (!sprite) return;
    sprite.width = width;
    sprite.height = height;
    sprite.tileScale.set(zoom);
    sprite.tilePosition.set(width / 2 - cameraX * PARALLAX * zoom, height / 2 - cameraY * PARALLAX * zoom);
    if (sprite.alpha < ALPHA) sprite.alpha = Math.min(ALPHA, (ALPHA * (now - this.readyAt)) / FADE_MS);
  }

  /** Текстуру не уничтожаем: её держит кеш Assets и переиспользуют соседние системы региона. */
  destroy(): void {
    this.cancelled = true;
    this.view.destroy({ children: true });
  }
}

/** Ширина плитки в пикселях при зуме 1 — для тестов и отладки. */
export const SKY_TILE = TILE;
