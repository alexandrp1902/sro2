import { liveries, type Livery } from './art';
import { LocalRace } from './localRace';
import { angle, DT } from './physics';
import { collideRacers } from './collisions';
import type { Config, Input, Vec } from './types';

export interface Racer { name: string; livery: Livery; run: LocalRace; player: boolean }
const distance = (a: Vec, b: Vec) => Math.hypot(a.x - b.x, a.y - b.y);

/** Fixed-step race field. Computer pilots have separate lanes and tuned corner handling. */
export class RaceOpponents {
  readonly racers: Racer[];
  private routes: Vec[][];
  private speeds: number[][];
  private lastNow: number;
  private accumulator = 0;
  private contacts = new Set<string>();
  constructor(readonly player: LocalRace, readonly livery: Livery) {
    const { config, startsAt } = player;
    this.lastNow = startsAt;
    const names = ['Шакал', 'Искра', 'Кочевник'];
    const colors = liveries.filter(color => color !== livery);
    this.racers = colors.map((color, i) => {
      // Stronger lateral control lets AI follow its assigned lane at racing speed.
      const run = new LocalRace({ ...config, rules: { ...config.rules,
        lateralK: 3, angularAccel: 8, turnFast: 2.5 } }, startsAt, 0);
      const h = config.track.spawnHeading;
      const back = i === 2 ? 5 : 0, side = i === 0 ? -5 : i === 1 ? 5 : 0;
      run.body.position = { x: config.track.spawn.x - Math.cos(h) * back - Math.sin(h) * side,
        y: config.track.spawn.y - Math.sin(h) * back + Math.cos(h) * side };
      return { name: names[i], livery: color, run, player: false };
    });
    this.routes = this.racers.map((_, i) => this.route(config, [-4.8, 4.8, 0][i]));
    this.speeds = this.routes.map((route, lane) => route.map((p, i) => {
      const before = route[(i + route.length - 3) % route.length], after = route[(i + 3) % route.length];
      const turn = Math.abs(angle(Math.atan2(after.y - p.y, after.x - p.x) - Math.atan2(p.y - before.y, p.x - before.x)));
      const curvature = turn / Math.max(.1, (distance(before, p) + distance(p, after)) / 2);
      // Lane-specific corner limits target 24-second flying laps in Red Canyon.
      return Math.min(config.rules.engineLimit, Math.sqrt([30.5, 27.5, 28.5][lane] / Math.max(.0001, curvature)), 2 / Math.max(.0001, curvature));
    }));
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
    const target = ahead(4 + speed * .32);
    const heading = Math.atan2(target.y - b.position.y, target.x - b.position.x);
    const error = Math.abs(angle(heading - b.heading));
    let desired = run.config.rules.engineLimit;
    let travelled = 0;
    for (let n = 0; n < route.length && travelled < 80; n++) {
      const i = (nearest + n) % route.length;
      // A standing start and slightly gentler first-lap corners target 26 seconds.
      const cornerSpeed = this.speeds[index][i] * (run.lapTimesMs.length === 0 && this.speeds[index][i] < run.config.rules.engineLimit ? .95 : 1);
      desired = Math.min(desired, Math.sqrt(cornerSpeed ** 2 + 2 * run.config.rules.brakeAccel * .65 * travelled));
      travelled += distance(route[i], route[(i + 1) % route.length]);
    }
    desired /= 1 + Math.max(0, error - .5);
    // Yield to a slower racer occupying this lane; the player can still ram any pod.
    for (const other of [this.player, ...this.racers.map(r => r.run)]) {
      if (other === run || !other.active) continue;
      const dx = other.body.position.x - b.position.x, dy = other.body.position.y - b.position.y;
      const along = dx * Math.cos(heading) + dy * Math.sin(heading);
      const across = Math.abs(-dx * Math.sin(heading) + dy * Math.cos(heading));
      const clearance = run.config.rules.radius + other.config.rules.radius + .6;
      if (along > 0 && along < 16 && across < clearance) {
        const leaderSpeed = Math.max(0, other.body.velocity.x * Math.cos(heading) + other.body.velocity.y * Math.sin(heading));
        desired = Math.min(desired, leaderSpeed + Math.max(0, along - clearance - 1) * 2);
      }
    }
    // Counter lateral drift before it carries the pod into the outside bank.
    const vx = b.velocity.x - Math.cos(heading) * speed, vy = b.velocity.y - Math.sin(heading) * speed;
    return { ...idle, heading: desired < .1 ? b.heading : Math.atan2(Math.sin(heading) * desired - vy,
      Math.cos(heading) * desired - vx), throttle: desired / run.config.rules.engineLimit,
      brake: speed > desired + 1 };
  }
  advance(now: number, input: Input): void {
    if (!this.player.active || now < this.player.startsAt) return;
    this.accumulator += Math.min(.25, Math.max(0, (now - this.lastNow) / 1000)); this.lastNow = now;
    const runs = [this.player, ...this.racers.map(r => r.run)];
    while (this.accumulator + 1e-10 >= DT && this.player.active) {
      const stepStart = now - this.player.startsAt - this.accumulator * 1000;
      const before = runs.map(r => ({ ...r.body.position }));
      this.player.advanceStep(stepStart, input);
      this.racers.forEach((racer, i) => racer.run.advanceStep(stepStart, this.input(i)));
      collideRacers(runs, before, this.contacts, stepStart + DT * 1000);
      this.accumulator -= DT;
    }
    if (this.player.active) this.player.elapsedMs = Math.round(now - this.player.startsAt);
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
