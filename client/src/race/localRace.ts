import { body, crossing, DT, step, type Body, type Impact } from './physics';
import type { Config, Input, Vec } from './types';

/** Browser owns movement, gates and damage; the server only measures the attempt. */
export class LocalRace {
  readonly body: Body;
  readonly lapTimesMs: number[] = [];
  readonly impacts: Impact[] = [];
  crashVelocity: Vec | undefined;
  state: 'COUNTDOWN' | 'RUNNING' | 'FINISHED' | 'CRASHED' | 'ABORTED' = 'COUNTDOWN';
  reason: string | null = null;
  nextCp = 0;
  elapsedMs = 0;
  private lastNow: number;
  private accumulator = 0;
  private lastLapMs = 0;
  readonly startsAt: number;
  constructor(readonly config: Config, now: number, countdownMs: number) {
    this.body = body(config.track, config.rules);
    this.startsAt = now + countdownMs; this.lastNow = this.startsAt;
  }
  get active(): boolean { return this.state === 'COUNTDOWN' || this.state === 'RUNNING'; }
  countdownMs(now: number): number { return Math.max(0, this.startsAt - now); }
  get lap(): number { return Math.min(this.config.track.lapCount, this.lapTimesMs.length + 1); }
  get lapElapsedMs(): number { return Math.max(0, this.elapsedMs - this.lastLapMs); }
  abort(reason = 'USER_ABORT'): void { if (this.active) { this.state = 'ABORTED'; this.reason = reason; } }
  advance(now: number, input: Input): void {
    if (!this.active || now < this.startsAt) return;
    this.state = 'RUNNING'; this.elapsedMs = Math.max(0, Math.round(now - this.startsAt));
    this.accumulator += Math.min(.25, Math.max(0, (now - this.lastNow) / 1000)); this.lastNow = now;
    const { track, rules } = this.config;
    while (this.accumulator >= DT && this.active) {
      const stepStart = now - this.startsAt - this.accumulator * 1000;
      const motion = step(this.body, input, track, rules);
      for (const segment of motion.segments) {
        const gates = [...track.checkpoints, track.finishLine].map((gate, index) => ({ index, t: crossing(segment.from, segment.to, gate) }))
          .filter(g => g.t !== null).sort((a, b) => a.t! - b.t!);
        for (const gate of gates) {
          const fraction = segment.startFraction + (segment.endFraction - segment.startFraction) * gate.t!;
          if (motion.fatalReason && fraction >= motion.fatalFraction - rules.eventEpsilon) break;
          if (gate.index !== this.nextCp) continue;
          if (this.nextCp < track.checkpoints.length) { this.nextCp++; continue; }
          const ms = Math.max(this.lastLapMs + 1, Math.round(stepStart + fraction * DT * 1000));
          this.lapTimesMs.push(ms - this.lastLapMs); this.lastLapMs = ms; this.nextCp = 0;
          if (this.lapTimesMs.length === track.lapCount) {
            this.state = 'FINISHED'; this.elapsedMs = ms;
            if (motion.hpBeforeFatal !== undefined) this.body.hp = motion.hpBeforeFatal;
            this.body.position = { x: segment.from.x + (segment.to.x - segment.from.x) * gate.t!, y: segment.from.y + (segment.to.y - segment.from.y) * gate.t! };
            this.body.velocity = { x: 0, y: 0 }; return;
          }
        }
      }
      this.impacts.push(...motion.impacts); if (this.impacts.length > 24) this.impacts.splice(0, this.impacts.length - 24);
      this.accumulator -= DT;
      if (motion.fatalReason) {
        this.state = 'CRASHED'; this.reason = motion.fatalReason;
        this.elapsedMs = Math.max(0, Math.round(stepStart + motion.fatalFraction * DT * 1000));
        this.crashVelocity = { ...this.body.velocity };
        this.body.velocity = { x: 0, y: 0 }; return;
      }
    }
    if (this.elapsedMs >= rules.timeLimitSeconds * 1000) this.abort('TIME_LIMIT');
  }
}
