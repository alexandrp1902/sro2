import { Assets, Container, Graphics, Sprite, type Texture } from 'pixi.js';
import type { Body } from './physics';
import type { Config, Input } from './types';

/** Uses the game's existing Pixi renderer and ticker. All geometry is in world units. */
export class RaceScene {
  view = new Container();
  private background = new Graphics();
  private world = new Container();
  private track = new Graphics();
  private marker = new Graphics();
  private vehicle = new Container();
  private hull = new Sprite();
  private textures = new Map<string, Texture>();
  private exhaust = new Graphics();
  private effects = new Graphics();
  private lastHp = 100;
  private hitAt = -1000;
  private camera = { x: 0, y: 0 };
  constructor() {
    this.view.addChild(this.background, this.world);
    this.world.addChild(this.track, this.effects, this.marker, this.vehicle);
    this.vehicle.addChild(this.exhaust, this.hull); this.view.visible = false;
    this.hull.anchor.set(.5);
  }
  async prepare(livery: 'copper' | 'blue'): Promise<void> {
    const url = `${import.meta.env.BASE_URL}race/bolide-${livery}.png`;
    let texture = this.textures.get(url);
    if (!texture) {
      texture = await Assets.load<Texture>({ src: url, data: { autoGenerateMipmaps: true } });
      this.textures.set(url, texture);
    }
    this.hull.texture = texture;
    this.hull.width = 3.65;
    this.hull.height = 3.65 * texture.height / texture.width;
  }
  load(config: Config): void {
    this.track.clear(); const t = config.track;
    let seed = 731;
    const random = () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 4294967296; };
    for (let i = 0; i < 550; i++) {
      const x = -120 + random() * 370, y = -80 + random() * 290, radius = .3 + random() * 1.9;
      this.track.ellipse(x, y, radius * 1.8, radius).fill({ color: i % 3 ? 0x654332 : 0x765137, alpha: .7 });
      if (i % 5 === 0) this.track.circle(x, y, radius * .7).fill(0x8c6247);
    }
    // The racing surface follows the exact collision banks, including every rock spur.
    const outer = t.walls.filter(w => w.id.startsWith('wall-0-'));
    const inner = t.walls.filter(w => w.id.startsWith('wall-1-'));
    for (const [width, color] of [[9, 0x3b291f], [6, 0x704632], [3.8, 0x986748]]) {
      for (const bank of [outer, inner]) {
        this.track.moveTo(bank[0].a.x, bank[0].a.y);
        for (const w of bank) this.track.lineTo(w.b.x, w.b.y);
        this.track.closePath().stroke({ color, width, join: 'round' });
      }
    }
    for (let i = 0; i < outer.length; i++) {
      const a = outer[i], b = inner[i];
      this.track.poly([a.a.x, a.a.y, a.b.x, a.b.y, b.b.x, b.b.y, b.a.x, b.a.y]).fill(0x34312c);
    }
    for (const w of t.walls) this.track.moveTo(w.a.x, w.a.y).lineTo(w.b.x, w.b.y)
      .stroke({ color: 0x211b17, width: 1.2 }).stroke({ color: 0xd69d69, width: .35 });
    for (let i = 0; i < t.centerline.length; i += 10) {
      const p = t.centerline[i], next = t.centerline[(i + 1) % t.centerline.length];
      const h = Math.atan2(next.y - p.y, next.x - p.x);
      const f = { x: Math.cos(h), y: Math.sin(h) }, n = { x: -f.y, y: f.x };
      this.track.moveTo(p.x - f.x + n.x * .7, p.y - f.y + n.y * .7).lineTo(p.x, p.y)
        .lineTo(p.x - f.x - n.x * .7, p.y - f.y - n.y * .7).stroke({ color: 0xa08c68, width: .13 });
    }
    for (const cp of t.checkpoints) this.track.moveTo(cp.a.x, cp.a.y).lineTo(cp.b.x, cp.b.y).stroke({ color: 0x497274, width: .09, alpha: .7 });
    for (let i = 2; i < outer.length; i += 30) {
      const w = outer[i], p = w.a, n = w.b;
      const h = Math.atan2(n.y - p.y, n.x - p.x), x = p.x + Math.sin(h) * 2, y = p.y - Math.cos(h) * 2;
      this.track.rect(x - 1, y - .7, 2, 1.4).fill(0xd8a556).stroke({ color: 0x302b24, width: .15 });
      this.track.moveTo(x - .5, y - .35).lineTo(x, y).lineTo(x - .5, y + .35).stroke({ color: 0x332c23, width: .18 });
    }
    const g = t.finishLine;
    this.track.moveTo(g.a.x, g.a.y).lineTo(g.b.x, g.b.y).stroke({ color: 0xf1e9c8, width: .7 });
    for (const o of t.obstacles) {
      this.track.circle(o.center.x + .3, o.center.y + .3, o.radius).fill({ color: 0x151a1a, alpha: .5 });
      this.track.circle(o.center.x, o.center.y, o.radius).fill(0xab8060).stroke({ color: 0xf7c384, width: .1 });
    }
    this.camera = { ...t.spawn }; this.lastHp = config.rules.hp; this.hitAt = -1000;
    this.effects.clear(); this.view.visible = true;
  }
  frame(now: number, width: number, height: number, b: Body, input: Input, crashed: boolean): void {
    this.background.clear().rect(0, 0, width, height).fill(0x503527);
    const speed = Math.hypot(b.velocity.x, b.velocity.y);
    const scale = Math.min(width, height) / (55 + Math.min(45, speed * .8));
    const lookX = Math.max(-width / scale * .27, Math.min(width / scale * .27, b.velocity.x * 1.05));
    const lookY = Math.max(-height / scale * .2, Math.min(height / scale * .2, b.velocity.y * 1.05));
    const target = { x: b.position.x + lookX, y: b.position.y + lookY };
    this.camera.x += (target.x - this.camera.x) * .12; this.camera.y += (target.y - this.camera.y) * .12;
    this.world.scale.set(scale);
    this.world.position.set(width / 2 - this.camera.x * scale, height * .43 - this.camera.y * scale);
    this.vehicle.position.set(b.position.x, b.position.y); this.vehicle.rotation = b.heading;
    this.vehicle.visible = !crashed;
    this.marker.clear();
    if (!crashed) this.marker.circle(b.position.x, b.position.y, 2.3).stroke({ color: 0x92f4ee, width: .12, alpha: .7 });
    this.exhaust.clear();
    const power = input.brake ? 0 : input.throttle;
    for (const y of [-.7, .7]) if (power > 0) {
      this.exhaust.moveTo(-1.4, y - .2).lineTo(-1.55 - power * (1.3 + .24 * Math.sin(now / 42)), y)
        .lineTo(-1.4, y + .2).closePath().fill({ color: 0x58e4f0, alpha: .8 });
    }
    if (b.hp < this.lastHp) this.hitAt = now;
    this.lastHp = b.hp; this.effects.clear();
    const age = (now - this.hitAt) / 1000;
    if (age >= 0 && age < .8) {
      for (let i = 0; i < 12; i++) {
        const a = i * Math.PI / 6, d = age * (crashed ? 16 : 5);
        this.effects.circle(b.position.x + Math.cos(a) * d, b.position.y + Math.sin(a) * d, crashed ? .2 : .07)
          .fill({ color: 0xffc36c, alpha: 1 - age / .8 });
      }
      if (crashed) this.effects.circle(b.position.x, b.position.y, age * 6).fill({ color: 0xef722c, alpha: .6 * (1 - age / .8) });
    }
    if (b.hp < 45 && !crashed) this.effects.circle(b.position.x - Math.cos(b.heading), b.position.y - Math.sin(b.heading), .5).fill({ color: 0x262626, alpha: .5 });
  }
  close(): void { this.view.visible = false; this.effects.clear(); this.track.clear(); }
}
