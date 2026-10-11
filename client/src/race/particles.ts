import type { Vec } from './types';

export type ParticleKind = 'fire' | 'smoke' | 'spark' | 'engine' | 'scrap';
export interface Particle {
  kind: ParticleKind; x: number; y: number; vx: number; vy: number;
  age: number; life: number; size: number; rotation: number; spin: number; variant: number;
}
/** Visual simulation only: these particles never enter the racing collision model. */
export class RaceParticles {
  readonly particles: Particle[] = [];
  private smokeTime = 0;
  private debrisTime = 0;
  constructor(private random = Math.random) {}
  reset(): void { this.particles.length = 0; this.smokeTime = 0; this.debrisTime = 0; }
  private emit(kind: ParticleKind, p: Vec, velocity: Vec, size: number, life: number, spread: number, variant = 0): void {
    if (this.particles.length >= 360) return;
    const a = this.random() * Math.PI * 2, impulse = spread * (.35 + this.random() * .65);
    this.particles.push({ kind, x: p.x, y: p.y, vx: velocity.x + Math.cos(a) * impulse,
      vy: velocity.y + Math.sin(a) * impulse, size, life, age: 0,
      rotation: this.random() * Math.PI * 2, spin: (this.random() - .5) * 9, variant });
  }
  hit(p: Vec, velocity: Vec, normal: Vec, speed: number): void {
    for (let i = 0; i < Math.min(24, 6 + Math.ceil(speed * 2)); i++) {
      if (this.particles.length >= 360) break;
      this.emit('spark', p, { x: velocity.x * .4, y: velocity.y * .4 }, .04 + this.random() * .04, .25 + this.random() * .45, 5);
      const s = this.particles[this.particles.length - 1];
      const inward = s.vx * normal.x + s.vy * normal.y;
      if (inward < 0) { s.vx -= normal.x * inward * 2; s.vy -= normal.y * inward * 2; }
      s.vx += normal.x; s.vy += normal.y;
    }
  }
  explode(p: Vec, velocity: Vec, heading: number, engineY: number, engineX: number): void {
    for (let i = 0; i < 4; i++) this.emit('fire', p, velocity, .65 + i * .1, .3 + i * .08, 0);
    for (let i = 0; i < 54; i++) this.emit('fire', p, velocity, .22 + this.random() * .7, .35 + this.random() * .85, 10);
    for (let i = 0; i < 26; i++) this.emit('smoke', p, velocity, .4 + this.random() * .65, 1.8 + this.random() * 1.8, 6);
    for (let i = 0; i < 35; i++) this.emit('spark', p, velocity, .04 + this.random() * .07, .5 + this.random(), 19);
    for (let i = 0; i < 2; i++) {
      if (this.particles.length >= 360) break;
      const side = i ? 1 : -1;
      const pos = { x: p.x + Math.cos(heading) * engineX - Math.sin(heading) * side * engineY,
        y: p.y + Math.sin(heading) * engineX + Math.cos(heading) * side * engineY };
      this.emit('engine', pos, { x: velocity.x - Math.sin(heading) * side * 5,
        y: velocity.y + Math.cos(heading) * side * 5 }, 1, 3.6, 2, i);
      this.particles[this.particles.length - 1].rotation = heading;
    }
    for (let i = 0; i < 9; i++) this.emit('scrap', p, velocity, .1 + this.random() * .18, 2 + this.random(), 12, i % 3);
  }
  update(dt: number, source?: { position: Vec; velocity: Vec; hp: number; maxHp: number }): void {
    dt = Math.max(0, Math.min(.05, dt));
    if (source && source.hp / source.maxHp < .45) {
      const severity = 1 - source.hp / (source.maxHp * .45);
      this.smokeTime += dt * (12 + severity * 34);
      while (this.smokeTime >= 1) {
        this.smokeTime--;
        this.emit('smoke', source.position, source.velocity, .22 + severity * .28, 1.2 + severity, .7);
      }
    } else this.smokeTime = 0;
    // Emit burning wakes from moving engines, at the same rate on all displays.
    this.debrisTime += dt;
    const trail = this.debrisTime >= 1 / 30;
    if (trail) this.debrisTime %= 1 / 30;
    const count = this.particles.length;
    for (let i = count - 1; i >= 0; i--) {
      const p = this.particles[i]; p.age += dt;
      if (p.age >= p.life) { this.particles.splice(i, 1); continue; }
      const drag = p.kind === 'smoke' ? .65 : p.kind === 'fire' ? .35 : .12;
      const decay = Math.exp(-drag * dt), travel = (1 - decay) / drag;
      p.x += p.vx * travel; p.y += p.vy * travel;
      p.vx *= decay; p.vy *= decay; p.rotation += p.spin * dt;
      if (trail && p.kind === 'engine' && p.age < 1.6) {
        this.emit('fire', p, { x: p.vx * .35, y: p.vy * .35 }, .22, .45, .8);
        this.emit('smoke', p, { x: p.vx * .2, y: p.vy * .2 }, .28, 1.2, .5);
      }
    }
  }
}
