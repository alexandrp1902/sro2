import { LocalRace } from './localRace';
import { DT } from './physics';
import type { Vec } from './types';

/** Equal-mass pods: swept circle contacts, separation, impulse and impact damage. */
export function collideRacers(runs: LocalRace[], before: Vec[], contacts: Set<string>, elapsedMs: number): void {
  const touching = new Set<string>();
  for (let i = 0; i < runs.length; i++) for (let j = i + 1; j < runs.length; j++) {
    const a = runs[i], b = runs[j];
    if (a.state !== 'RUNNING' || b.state !== 'RUNNING') continue;
    const radius = a.config.rules.radius + b.config.rules.radius;
    const px = before[j].x - before[i].x, py = before[j].y - before[i].y;
    const dx = b.body.position.x - a.body.position.x, dy = b.body.position.y - a.body.position.y;
    const mx = dx - px, my = dy - py, qa = mx * mx + my * my;
    const qb = px * mx + py * my, qc = px * px + py * py - radius * radius;
    const disc = qb * qb - qa * qc;
    let t = qc <= 0 ? 0 : qa > 1e-12 && disc >= 0 ? (-qb - Math.sqrt(disc)) / qa : Infinity;
    const overlap = Math.hypot(dx, dy) < radius;
    if ((t < 0 || t > 1) && !overlap) continue;
    t = Math.max(0, Math.min(1, t));
    const key = `${i}:${j}`; touching.add(key);
    const ax = before[i].x + (a.body.position.x - before[i].x) * t;
    const ay = before[i].y + (a.body.position.y - before[i].y) * t;
    const bx = before[j].x + (b.body.position.x - before[j].x) * t;
    const by = before[j].y + (b.body.position.y - before[j].y) * t;
    const len = Math.hypot(bx - ax, by - ay), nx = len > 1e-9 ? (bx - ax) / len : 1, ny = len > 1e-9 ? (by - ay) / len : 0;
    const closing = (a.body.velocity.x - b.body.velocity.x) * nx + (a.body.velocity.y - b.body.velocity.y) * ny;
    const hit = { x: ax + nx * a.config.rules.radius, y: ay + ny * a.config.rules.radius };
    if (closing > .2 && !contacts.has(key)) {
      a.collide(closing / 2, hit, { x: -nx, y: -ny }, elapsedMs - (1 - t) * DT * 1000);
      b.collide(closing / 2, hit, { x: nx, y: ny }, elapsedMs - (1 - t) * DT * 1000);
    }
    const impulse = Math.max(0, closing) * (1 + Math.min(a.config.rules.restitution, b.config.rules.restitution)) / 2;
    if (a.active) { a.body.velocity.x -= impulse * nx; a.body.velocity.y -= impulse * ny; }
    if (b.active) { b.body.velocity.x += impulse * nx; b.body.velocity.y += impulse * ny; }
    const separation = Math.max(0, radius - len + .001) / 2;
    a.body.position = { x: ax - nx * separation + a.body.velocity.x * DT * (1 - t), y: ay - ny * separation + a.body.velocity.y * DT * (1 - t) };
    b.body.position = { x: bx + nx * separation + b.body.velocity.x * DT * (1 - t), y: by + ny * separation + b.body.velocity.y * DT * (1 - t) };
  }
  contacts.clear(); touching.forEach(key => contacts.add(key));
}
