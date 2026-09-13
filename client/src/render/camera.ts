import type { Container } from 'pixi.js';

/** Камера центрируется на корабле игрока и не вращается (GDD §42). */
export class Camera {
  x = 0;
  y = 0;
  zoom = 1;

  follow(x: number, y: number, zoom: number): this {
    this.x = x;
    this.y = y;
    this.zoom = zoom;
    return this;
  }

  apply(world: Container, screenWidth: number, screenHeight: number): void {
    world.scale.set(this.zoom);
    world.position.set(screenWidth / 2 - this.x * this.zoom, screenHeight / 2 - this.y * this.zoom);
  }
}
