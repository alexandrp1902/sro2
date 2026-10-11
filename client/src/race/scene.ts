import { Assets, Container, FillPattern, Graphics, Rectangle, Sprite, Texture } from 'pixi.js';
import type { Body, Impact } from './physics';
import type { Config, Input, Vec } from './types';
import { artUrl, podLayout, raceAssets, type Livery } from './art';
import { RaceParticles } from './particles';
import type { Racer } from './opponents';

/** Uses the game's existing Pixi renderer and ticker. All geometry is in world units. */
export class RaceScene {
  view = new Container();
  private background = new Graphics();
  private world = new Container();
  private track = new Graphics();
  private vehicle = new Container();
  private rivals = new Container();
  private rivalViews: { view: Container; flames: Sprite[]; wasCrashed: boolean }[] = [];
  private hull = new Sprite();
  private pilot = new Graphics();
  private link = new Sprite();
  private flames = [new Sprite(), new Sprite()];
  private scenery = new Container();
  private banks = new Container();
  private landMask = new Graphics();
  private floorShadow = new Graphics();
  private floorMask = new Graphics();
  private decals = new Container();
  private particleView = new Container();
  private particleSprites: Sprite[] = [];
  private particles = new RaceParticles();
  private smokeTexture?: Texture;
  private fireTexture?: Texture;
  private debrisTextures: Texture[] = [];
  private livery: Livery = 'copper';
  private lastNow = 0;
  private maxHp = 100;
  private wasCrashed = false;
  private textures = new Map<string, Texture>();
  private camera = { x: 0, y: 0 };
  constructor() {
    this.view.addChild(this.background, this.world);
    this.world.addChild(this.track, this.banks, this.landMask, this.floorShadow, this.floorMask, this.scenery, this.decals, this.rivals, this.vehicle, this.particleView);
    this.banks.setMask({ mask: this.landMask, inverse: true });
    this.floorShadow.mask = this.floorMask;
    this.particleView.sortableChildren = true;
    this.vehicle.addChild(...this.flames, this.hull, this.link, this.pilot); this.view.visible = false;
    this.hull.anchor.set(.5);
  }
  async prepare(livery: Livery): Promise<void> {
    await Promise.all(raceAssets.map(async name => {
      if (!this.textures.has(name)) this.textures.set(name,
        await Assets.load<Texture>({ src: artUrl(name), data: { autoGenerateMipmaps: true } }));
    }));
    this.livery = livery;
    const texture = this.textures.get(`pod-${livery}-v1`)!;
    this.hull.texture = texture;
    this.hull.width = 3.65;
    this.hull.height = 3.65 * texture.height / texture.width;
    const layout = podLayout[livery];
    for (let i = 0; i < 2; i++) {
      const flame = this.flames[i]; flame.texture = this.textures.get('pod-exhaust-v1')!;
      flame.anchor.set(.96, .5); flame.blendMode = 'add';
      flame.position.set((layout.nozzle - .5) * 3.65, (i ? 1 : -1) * layout.engineY * this.hull.height);
    }
    this.link.texture = this.textures.get('pod-energy-link-v1')!; this.link.anchor.set(.5);
    this.link.position.set(.8, 0); this.link.width = .45; this.link.height = layout.engineY * this.hull.height * 2;
    this.link.blendMode = 'add';
    // Small seated figure drawn in the sprite's open cockpit, facing the engines.
    this.pilot.clear().ellipse(-.06, 0, .16, .145).fill(0x282d32)
      .ellipse(-.03, 0, .115, .13).fill(livery === 'white' ? 0x9c4835 : 0xc4ac80)
      .moveTo(0, -.105).lineTo(.16, -.14).moveTo(0, .105).lineTo(.16, .14).stroke({ color: 0x403b32, width: .06 })
      .circle(.075, 0, .097).fill(0xd7c7a2).stroke({ color: 0x282c31, width: .022 })
      .ellipse(.115, 0, .04, .073).fill(0x152c3b)
      .moveTo(.13, -.045).lineTo(.14, .035).stroke({ color: 0x84c9dc, width: .016 });
    this.pilot.position.set((layout.seat - .5) * 3.65, 0);
    for (const t of this.debrisTextures) t.destroy();
    this.debrisTextures = [0, 1].map(i => new Texture({ source: texture.source,
      frame: new Rectangle(Math.floor(texture.width * layout.engineStart), i ? texture.height / 2 : 0,
        Math.floor(texture.width * (1 - layout.engineStart)), texture.height / 2) }));
    for (const [x, y, w, h] of [[.12, .40, .18, .20], [.65, .22, .12, .12], [.70, .64, .10, .14]]) {
      this.debrisTextures.push(new Texture({ source: texture.source, frame: new Rectangle(
        Math.floor(x * texture.width), Math.floor(y * texture.height), Math.floor(w * texture.width), Math.floor(h * texture.height)) }));
    }
    this.smokeTexture ??= this.cloud(false); this.fireTexture ??= this.cloud(true);
  }
  private cloud(fire: boolean): Texture {
    const canvas = document.createElement('canvas'); canvas.width = canvas.height = 96;
    const ctx = canvas.getContext('2d')!;
    // Overlapping soft lobes avoid flat geometric discs at mobile sizes.
    for (let i = 0; i < 7; i++) {
      const a = i * 2.4, x = 48 + Math.cos(a) * 13, y = 48 + Math.sin(a) * 13;
      const g = ctx.createRadialGradient(x, y, 0, x, y, 31);
      g.addColorStop(0, fire ? 'rgba(255,249,207,.85)' : 'rgba(80,74,68,.5)');
      g.addColorStop(.3, fire ? 'rgba(255,175,34,.7)' : 'rgba(54,50,46,.4)');
      g.addColorStop(.65, fire ? 'rgba(235,54,8,.35)' : 'rgba(30,28,26,.22)');
      g.addColorStop(1, 'rgba(0,0,0,0)'); ctx.fillStyle = g; ctx.fillRect(0, 0, 96, 96);
    }
    return Texture.from(canvas);
  }
  private sprite(name: string, parent: Container, x: number, y: number, width: number, rotation = 0): Sprite {
    const sprite = new Sprite(this.textures.get(name)); sprite.anchor.set(.5); sprite.position.set(x, y);
    sprite.width = width; sprite.height = width * sprite.texture.height / sprite.texture.width;
    sprite.rotation = rotation; parent.addChild(sprite); return sprite;
  }
  private clearScenery(): void {
    for (const parent of [this.banks, this.scenery, this.decals]) for (const child of parent.removeChildren()) child.destroy();
    this.landMask.clear();
    this.floorShadow.clear(); this.floorMask.clear();
  }
  private pattern(name: string, size: number): FillPattern {
    const texture = this.textures.get(name)!;
    const pattern = new FillPattern({ texture, repetition: 'repeat', textureSpace: 'global' });
    pattern.transform.scale(size / texture.width, size / texture.height); return pattern;
  }
  load(config: Config): void {
    for (const child of this.rivals.removeChildren()) child.destroy({ children: true });
    this.rivalViews = [];
    this.track.clear(); this.clearScenery(); const t = config.track;
    let seed = 731;
    const random = () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 4294967296; };
    const xs = t.walls.flatMap(w => [w.a.x, w.b.x]), ys = t.walls.flatMap(w => [w.a.y, w.b.y]);
    const minX = Math.min(...xs) - 150, minY = Math.min(...ys) - 150;
    const spanX = Math.max(...xs) - minX + 150, spanY = Math.max(...ys) - minY + 150;
    this.track.rect(minX, minY, spanX, spanY).fill(this.pattern('canyon-rock-tile-v1', 32));
    // The racing surface follows the exact collision banks, including every rock spur.
    const outer = t.walls.filter(w => w.id.startsWith('wall-0-'));
    const inner = t.walls.filter(w => w.id.startsWith('wall-1-'));
    // Individual ribbon sections avoid triangulating a huge polygon with a hole.
    // The inverse of the same ribbon clips the cliffs exactly to collision banks.
    const sand = this.pattern('canyon-sand-tile-v1', 30);
    for (let i = 0; i < outer.length; i++) {
      const a = outer[i], b = inner[i];
      const section = [a.a.x, a.a.y, a.b.x, a.b.y, b.b.x, b.b.y, b.a.x, b.a.y];
      this.track.poly(section).fill(sand);
      this.landMask.poly(section).fill(0xffffff);
      this.floorMask.poly(section).fill(0xffffff);
    }
    // Build broad, textured escarpments from the collision edge up to the mesa.
    // Normals point into land; erosion varies smoothly rather than at each sprite.
    const cliff = new Graphics(); this.banks.addChild(cliff);
    const rock = this.pattern('canyon-rock-tile-v1', 22);
    const landNormal = (bank: typeof outer, i: number): Vec => {
      const p = bank[i].a, center = t.centerline[i];
      const length = Math.hypot(p.x - center.x, p.y - center.y);
      return { x: (p.x - center.x) / length, y: (p.y - center.y) / length };
    };
    for (const bank of [outer, inner]) {
      const normals = bank.map((_, i) => landNormal(bank, i));
      const depth = bank.map((_, i) => 11 + 2.1 * Math.sin(i * .083) + 1.3 * Math.sin(i * .21));
      const point = (i: number, level: number) => {
        const p = bank[i].a, n = normals[i];
        return { x: p.x + n.x * depth[i] * level, y: p.y + n.y * depth[i] * level };
      };
      for (let layer = 0; layer < 6; layer++) for (let i = 0; i < bank.length; i++) {
        const j = (i + 1) % bank.length;
        const a = point(i, layer / 6), b = point(j, layer / 6);
        const c = point(j, (layer + 1) / 6), d = point(i, (layer + 1) / 6);
        const face = [a.x, a.y, b.x, b.y, c.x, c.y, d.x, d.y];
        const shade = .5 - layer * .066 + (normals[i].x * .6 + normals[i].y * .8) * .12;
        cliff.poly(face).fill(rock).poly(face).fill({ color: 0x291713, alpha: Math.max(.06, shade) });
        cliff.moveTo(d.x, d.y).lineTo(c.x, c.y).stroke({ color: layer % 2 ? 0xf0ba7c : 0x582e21,
          width: layer % 2 ? .12 : .22, alpha: .4 });
      }
      // Soft directional shadows anchor the tall walls to the sandy canyon floor.
      for (let band = 0; band < 10; band++) for (let i = 0; i < bank.length; i++) {
        const j = (i + 1) % bank.length, n = normals[i], m = normals[j];
        const reach = (v: Vec) => 1.2 + 4.5 * Math.max(0, -v.x * .6 - v.y * .8);
        const a = bank[i].a, b = bank[j].a, near = band / 10, far = (band + 1) / 10;
        this.floorShadow.poly([a.x - n.x * reach(n) * near, a.y - n.y * reach(n) * near,
          b.x - m.x * reach(m) * near, b.y - m.y * reach(m) * near,
          b.x - m.x * reach(m) * far, b.y - m.y * reach(m) * far,
          a.x - n.x * reach(n) * far, a.y - n.y * reach(n) * far])
          .fill({ color: 0x241918, alpha: .32 * (1 - near) ** 1.5 });
      }
      // Overlapping formations make the mesa rim irregular and hide strip joins.
      for (let tier = 0; tier < 3; tier++) for (let i = 0; i < bank.length; i += 3) {
        const w = bank[i], n = normals[i], h = Math.atan2(w.b.y - w.a.y, w.b.x - w.a.x);
        const offset = tier === 0 ? 1.6 : tier === 1 ? depth[i] * .7 : depth[i] + 5;
        const sprite = this.sprite(i % 9 === 0 ? 'canyon-bank-b-v1' : 'canyon-bank-a-v1', this.banks,
          w.a.x + n.x * offset, w.a.y + n.y * offset, 11 + random() * 7, h + (random() - .5) * .3);
        sprite.tint = tier === 0 ? 0x87604b : tier === 1 ? 0xbb8c6b : 0xffdfb3;
        if (tier === 2 && i % 9 === 0) this.sprite('canyon-rock-cluster-v1', this.banks,
          w.a.x + n.x * (offset + 5), w.a.y + n.y * (offset + 5), 5 + random() * 4, random() * Math.PI * 2);
      }
    }
    for (let i = 0; i < t.centerline.length; i += 10) {
      const p = t.centerline[i], next = t.centerline[(i + 1) % t.centerline.length];
      const h = Math.atan2(next.y - p.y, next.x - p.x);
      this.sprite('race-arrow-v1', this.decals, p.x, p.y, 2, h).alpha = .65;
    }
    for (const cp of t.checkpoints) this.track.moveTo(cp.a.x, cp.a.y).lineTo(cp.b.x, cp.b.y).stroke({ color: 0x497274, width: .09, alpha: .7 });
    for (const cp of t.checkpoints) for (const p of [cp.a, cp.b]) {
      this.sprite('race-checkpoint-post-v1', this.banks, p.x, p.y, 1.8);
      this.sprite('canyon-beacon-v1', this.banks, p.x, p.y, .9);
    }
    for (let i = 2; i < outer.length; i += 30) {
      const w = outer[i], p = w.a, n = w.b;
      const h = Math.atan2(n.y - p.y, n.x - p.x), x = p.x + Math.sin(h) * 2, y = p.y - Math.cos(h) * 2;
      this.track.rect(x - 1, y - .7, 2, 1.4).fill(0xd8a556).stroke({ color: 0x302b24, width: .15 });
      this.track.moveTo(x - .5, y - .35).lineTo(x, y).lineTo(x - .5, y + .35).stroke({ color: 0x332c23, width: .18 });
    }
    const g = t.finishLine;
    this.track.moveTo(g.a.x, g.a.y).lineTo(g.b.x, g.b.y).stroke({ color: 0xf1e9c8, width: .7 });
    const finishLength = Math.hypot(g.b.x - g.a.x, g.b.y - g.a.y), finishAngle = Math.atan2(g.b.y - g.a.y, g.b.x - g.a.x);
    for (let d = .4; d < finishLength; d += .8) this.sprite('race-start-stripe-v1', this.decals,
      g.a.x + Math.cos(finishAngle) * d, g.a.y + Math.sin(finishAngle) * d, .85, finishAngle);
    for (const o of t.obstacles) {
      this.track.circle(o.center.x + .3, o.center.y + .3, o.radius).fill({ color: 0x151a1a, alpha: .5 });
      this.sprite(o.reason === 'COLUMN' ? 'canyon-column-v1' : o.radius > 1.6 ? 'canyon-rock-large-v1' : 'canyon-rock-small-v1',
        this.scenery, o.center.x, o.center.y, o.radius * 2.1);
    }
    this.camera = { ...t.spawn }; this.maxHp = config.rules.hp; this.lastNow = 0; this.wasCrashed = false;
    this.particles.reset(); this.renderParticles(); this.view.visible = true;
  }
  frame(now: number, width: number, height: number, b: Body, input: Input, crashed: boolean, crashVelocity?: Vec, impacts: Impact[] = [], racers: readonly Racer[] = []): void {
    this.background.clear().rect(0, 0, width, height).fill(0x503527);
    const speed = Math.hypot(b.velocity.x, b.velocity.y);
    const scale = 2 * Math.min(width, height) / (55 + Math.min(45, speed * .8));
    const lookX = Math.max(-width / scale * .27, Math.min(width / scale * .27, b.velocity.x * 1.05));
    const lookY = Math.max(-height / scale * .2, Math.min(height / scale * .2, b.velocity.y * 1.05));
    const target = { x: b.position.x + lookX, y: b.position.y + lookY };
    this.camera.x += (target.x - this.camera.x) * .12; this.camera.y += (target.y - this.camera.y) * .12;
    this.world.scale.set(scale);
    this.world.position.set(width / 2 - this.camera.x * scale, height * .43 - this.camera.y * scale);
    this.vehicle.position.set(b.position.x, b.position.y); this.vehicle.rotation = b.heading;
    this.vehicle.visible = !crashed;
    racers.forEach((racer, index) => {
      let rival = this.rivalViews[index];
      if (!rival) {
        const view = new Container(), layout = podLayout[racer.livery];
        const hull = this.sprite(`pod-${racer.livery}-v1`, view, 0, 0, 3.65);
        const flames = [0, 1].map(i => {
          const flame = this.sprite('pod-exhaust-v1', view, (layout.nozzle - .5) * 3.65,
            (i ? 1 : -1) * layout.engineY * hull.height, 1);
          flame.anchor.set(.96, .5); flame.blendMode = 'add'; view.setChildIndex(flame, i); return flame;
        });
        const pilot = new Graphics().circle((layout.seat - .5) * 3.65, 0, .12).fill(0xd7c7a2);
        view.addChild(pilot); this.rivals.addChild(view);
        rival = { view, flames, wasCrashed: false }; this.rivalViews[index] = rival;
      }
      const body = racer.run.body;
      for (const impact of racer.run.impacts.splice(0)) this.particles.hit(impact.position, impact.velocity, impact.normal, impact.speed);
      if (racer.run.state === 'CRASHED' && !rival.wasCrashed) {
        const layout = podLayout[racer.livery];
        this.particles.explode(body.position, racer.run.crashVelocity ?? body.velocity, body.heading,
          layout.engineY * 3.65, ((layout.engineStart + 1) / 2 - .5) * 3.65);
        rival.wasCrashed = true;
      }
      rival.view.position.set(body.position.x, body.position.y); rival.view.rotation = body.heading;
      rival.view.visible = racer.run.state !== 'CRASHED' && racer.run.state !== 'ABORTED';
      for (const flame of rival.flames) {
        flame.visible = racer.run.state === 'RUNNING' && Math.hypot(body.velocity.x, body.velocity.y) > 1;
        flame.width = 1.6 + .2 * Math.sin(now / 31 + index); flame.height = .55;
      }
    });
    const power = input.brake ? 0 : input.throttle;
    for (let i = 0; i < 2; i++) {
      const flame = this.flames[i], flicker = 1 + .10 * Math.sin(now / 31 + i * 2) + .05 * Math.sin(now / 13);
      flame.visible = power > 0 && !crashed;
      flame.width = (.35 + power * 2.6) * flicker; flame.height = (.32 + power * .43) * flicker;
      flame.alpha = .65 + power * .35;
    }
    this.link.alpha = .7 + .2 * Math.sin(now / 28);
    const dt = this.lastNow ? (now - this.lastNow) / 1000 : 0; this.lastNow = now;
    for (const impact of impacts) this.particles.hit(impact.position, impact.velocity, impact.normal, impact.speed);
    if (crashed && !this.wasCrashed) {
      const layout = podLayout[this.livery];
      this.particles.explode(b.position, crashVelocity ?? b.velocity, b.heading,
        layout.engineY * this.hull.height, ((layout.engineStart + 1) / 2 - .5) * 3.65);
    }
    this.wasCrashed = crashed;
    this.particles.update(dt, crashed ? undefined : { position: b.position, velocity: b.velocity, hp: b.hp, maxHp: this.maxHp });
    this.renderParticles();
  }
  private renderParticles(): void {
    const particles = this.particles.particles;
    for (let i = 0; i < particles.length; i++) {
      const p = particles[i], age = p.age / p.life;
      let sprite = this.particleSprites[i];
      if (!sprite) {
        sprite = new Sprite(); sprite.anchor.set(.5); this.particleView.addChild(sprite); this.particleSprites.push(sprite);
      }
      sprite.visible = true; sprite.position.set(p.x, p.y); sprite.rotation = p.rotation;
      sprite.tint = 0xffffff; sprite.blendMode = 'normal';
      sprite.zIndex = p.kind === 'smoke' ? 0 : p.kind === 'fire' ? 1 : 2;
      if (p.kind === 'engine') {
        sprite.texture = this.debrisTextures[p.variant]; sprite.width = (1 - podLayout[this.livery].engineStart) * 3.65;
        sprite.height = this.hull.height / 2; sprite.alpha = Math.min(1, (1 - age) * 4);
      } else if (p.kind === 'scrap') {
        sprite.texture = this.debrisTextures[2 + p.variant]; sprite.width = p.size * 3;
        sprite.height = sprite.width * sprite.texture.height / sprite.texture.width;
        sprite.alpha = Math.min(1, (1 - age) * 3);
      } else if (p.kind === 'smoke') {
        sprite.texture = this.smokeTexture!; sprite.width = sprite.height = p.size * (2 + age * 5);
        sprite.alpha = Math.min(1, p.age * 12) * (1 - age) * .65;
      } else {
        sprite.texture = this.fireTexture!; sprite.blendMode = 'add';
        sprite.width = p.size * (p.kind === 'spark' ? 5 : 2 + age * 2.5);
        sprite.height = p.size * (p.kind === 'spark' ? 1 : 2 + age * 2.5);
        sprite.rotation = p.kind === 'spark' ? Math.atan2(p.vy, p.vx) : p.rotation;
        sprite.tint = age < .25 ? 0xfff4c0 : age < .65 ? 0xffaa42 : 0xef431b;
        sprite.alpha = (1 - age) ** .7;
      }
    }
    for (let i = particles.length; i < this.particleSprites.length; i++) this.particleSprites[i].visible = false;
  }
  close(): void {
    this.view.visible = false; this.particles.reset(); this.renderParticles(); this.track.clear(); this.clearScenery();
  }
}
