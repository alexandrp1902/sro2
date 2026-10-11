import { liveries, type Livery } from './art';
import { LocalRace } from './localRace';
import { angle } from './physics';
import type { Config, Input, Vec } from './types';

export interface Racer { name: string; livery: Livery; run: LocalRace; player: boolean }
const distance = (a: Vec, b: Vec) => Math.hypot(a.x - b.x, a.y - b.y);

/** Computer pilots use the same movement, damage and ordered gates as the player. */
export class RaceOpponents {
  readonly racers: Racer[];
  private routes: Vec[][];
  constructor(readonly player: LocalRace, readonly livery: Livery) {
    const { config, startsAt } = player;
    const names = ['Шакал', 'Искра', 'Кочевник'];
    const colors = liveries.filter(color => color !== livery);
    this.racers = colors.map((color, i) => {
      const run = new LocalRace(config, startsAt, 0);
      const h = config.track.spawnHeading;
      const back = i === 2 ? 5 : 0, side = i === 0 ? -5 : i === 1 ? 5 : 0;
      run.body.position = { x: config.track.spawn.x - Math.cos(h) * back - Math.sin(h) * side,
        y: config.track.spawn.y - Math.sin(h) * back + Math.cos(h) * side };
      return { name: names[i], livery: color, run, player: false };
    });
    this.routes = this.racers.map((_, i) => this.route(config, (i - 1) * 2.4));
  }
  private route(config: Config, lane: number): Vec[] {
    const { centerline, obstacles } = config.track;
    return centerline.map((p, i) => {
      const before = centerline[(i + centerline.length - 1) % centerline.length];
      const after = centerline[(i + 1) % centerline.length];
      const h = Math.atan2(after.y - before.y, after.x - before.x);
      const point = { x: p.x - Math.sin(h) * lane, y: p.y + Math.cos(h) * lane };
      for (const obstacle of obstacles) {
        const clearance = obstacle.radius + config.rules.radius + 3;
        const d = distance(point, obstacle.center);
        if (d < clearance) {
          const dx = point.x - obstacle.center.x, dy = point.y - obstacle.center.y;
          const nx = d > .001 ? dx / d : -Math.sin(h), ny = d > .001 ? dy / d : Math.cos(h);
          point.x = obstacle.center.x + nx * clearance; point.y = obstacle.center.y + ny * clearance;
        }
      }
      return point;
    });
  }
  private input(index: number): Input {
    const run = this.racers[index].run, b = run.body, route = this.routes[index];
    const idle: Input = { seq: 0, steerMode: 'heading', heading: b.heading, turn: 0, throttle: 0, brake: false, controlActive: true };
    if (route.length < 2) return idle;
    let nearest = 0, best = Infinity;
    for (let i = 0; i < route.length; i++) {
      const d = distance(b.position, route[i]);
      if (d < best) { best = d; nearest = i; }
    }
    const speed = Math.hypot(b.velocity.x, b.velocity.y);
    const ahead = (length: number) => {
      let i = nearest, traversed = 0;
      for (let n = 0; n < route.length; n++) {
        const next = (i + 1) % route.length; traversed += distance(route[i], route[next]); i = next;
        if (traversed >= length) break;
      }
      return route[i];
    };
    const target = ahead(5 + speed * .55), far = ahead(12 + speed * 1.1);
    const heading = Math.atan2(target.y - b.position.y, target.x - b.position.x);
    const bend = Math.abs(angle(Math.atan2(far.y - target.y, far.x - target.x) - heading));
    const error = Math.abs(angle(heading - b.heading));
    const desired = Math.max(12, run.config.rules.engineLimit /
      (1 + Math.max(0, bend - .06) * (2 + index * .12) + Math.max(0, error - .06)));
    // Counter lateral drift before it carries the pod into the outside bank.
    const vx = b.velocity.x - Math.cos(heading) * speed, vy = b.velocity.y - Math.sin(heading) * speed;
    return { ...idle, heading: Math.atan2(Math.sin(heading) * desired - vy * .65,
      Math.cos(heading) * desired - vx * .65), throttle: desired / run.config.rules.engineLimit,
      brake: speed > desired + 4 };
  }
  advance(now: number): void {
    if (!this.player.active) return;
    this.racers.forEach((racer, i) => racer.run.advance(now, () => this.input(i)));
  }
  standings(): Racer[] {
    const entrants = [{ name: 'Вы', livery: this.livery, run: this.player, player: true }, ...this.racers];
    const progress = (run: LocalRace) => run.lapTimesMs.length * (run.config.track.checkpoints.length + 1) + run.nextCp;
    const remaining = (run: LocalRace) => {
      const gate = run.config.track.checkpoints[run.nextCp] ?? run.config.track.finishLine;
      return distance(run.body.position, { x: (gate.a.x + gate.b.x) / 2, y: (gate.a.y + gate.b.y) / 2 });
    };
    const category = (run: LocalRace) => run.state === 'FINISHED' ? 0 : run.active ? 1 : 2;
    return entrants.sort((a, b) => category(a.run) - category(b.run) ||
      (a.run.state === 'FINISHED' && b.run.state === 'FINISHED' ? a.run.elapsedMs - b.run.elapsedMs :
        progress(b.run) - progress(a.run) || remaining(a.run) - remaining(b.run)));
  }
  get place(): number { return this.standings().findIndex(racer => racer.player) + 1; }
}
