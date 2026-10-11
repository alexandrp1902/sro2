import type { Input, Rules, Track, Vec } from './types';

export const DT = 1 / 60;
const add = (a: Vec, b: Vec): Vec => ({ x: a.x + b.x, y: a.y + b.y });
const sub = (a: Vec, b: Vec): Vec => ({ x: a.x - b.x, y: a.y - b.y });
const mul = (a: Vec, s: number): Vec => ({ x: a.x * s, y: a.y * s });
const dot = (a: Vec, b: Vec) => a.x * b.x + a.y * b.y;
const length = (a: Vec) => Math.sqrt(dot(a, a));
const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));
export const angle = (a: number) => Math.atan2(Math.sin(a), Math.cos(a));
export const damage = (vn: number) => vn < 2 ? 0 : vn < 7 ? 2 + 2 * (vn - 2) : 12 + 6 * (vn - 7);
export interface Body {
  position: Vec; velocity: Vec; heading: number; omega: number; hp: number;
  boostRemaining: number; cooldownRemaining: number; boostPressId: number; contacts: Set<string>;
}
export interface Segment { from: Vec; to: Vec; startFraction: number; endFraction: number }
export interface Motion { segments: Segment[]; fatalReason: string | null; fatalFraction: number; normalSpeed: number; contactLimit: boolean }
export function body(track: Track, rules: Rules): Body {
  return { position: { ...track.spawn }, velocity: { x: 0, y: 0 }, heading: track.spawnHeading, omega: 0,
    hp: rules.hp, boostRemaining: 0, cooldownRemaining: 0, boostPressId: 0, contacts: new Set() };
}
export function distance(p: Vec, a: Vec, b: Vec): number {
  const ab = sub(b, a);
  return length(sub(p, add(a, mul(ab, clamp(dot(sub(p, a), ab) / dot(ab, ab), 0, 1)))));
}
export function crossing(from: Vec, to: Vec, g: { a: Vec; b: Vec; normal: Vec }): number | null {
  const da = dot(sub(from, g.a), g.normal), db = dot(sub(to, g.a), g.normal);
  if (da >= 0 || db < 0 || db - da < 1e-12) return null;
  const t = -da / (db - da), p = add(from, mul(sub(to, from), t)), line = sub(g.b, g.a);
  const along = dot(sub(p, g.a), line) / dot(line, line);
  return along >= 0 && along <= 1 ? t : null;
}
export function cancelBoost(b: Body, r: Rules): void {
  if (b.boostRemaining <= 0) return;
  b.boostRemaining = 0; b.cooldownRemaining = r.cooldownSeconds;
}
interface Hit { t: number; normal: Vec; id: string; reason: string }
function firstHit(p: Vec, d: Vec, track: Track, radius: number): Hit | null {
  let best: Hit | null = null;
  const candidate = (t: number, n: Vec, id: string, reason: string) => {
    if (t >= -1e-9 && t <= 1 && dot(d, n) < -1e-10 && (!best || t < best.t)) best = { t: Math.max(0, t), normal: n, id, reason };
  };
  const circle = (c: Vec, rad: number, id: string, reason: string) => {
    const v = sub(p, c), a = dot(d, d); if (a < 1e-20) return;
    const k = dot(v, d), disc = k * k - a * (dot(v, v) - rad * rad);
    if (disc < 0) return;
    const t = (-k - Math.sqrt(disc)) / a, at = add(v, mul(d, Math.max(0, t)));
    if (length(at) > 1e-12) candidate(t, mul(at, 1 / length(at)), id, reason);
  };
  for (const w of track.walls) {
    const axis = sub(w.b, w.a), len = length(axis), u = mul(axis, 1 / len), n = { x: -u.y, y: u.x };
    const dist = dot(sub(p, w.a), n), rate = dot(d, n);
    if (Math.abs(rate) > 1e-12) for (const sign of [-1, 1]) {
      const t = (sign * radius - dist) / rate, along = dot(sub(add(p, mul(d, t)), w.a), u);
      if (along >= 0 && along <= len) candidate(t, mul(n, sign), w.id, w.reason);
    }
    circle(w.a, radius, w.id, w.reason); circle(w.b, radius, w.id, w.reason);
  }
  for (const o of track.obstacles) circle(o.center, radius + o.radius, o.id, o.reason);
  return best;
}
export function step(b: Body, input: Input, track: Track, r: Rules): Motion {
  if (input.brake || !input.controlActive) cancelBoost(b, r);
  if (input.boostPressId > b.boostPressId) {
    b.boostPressId = input.boostPressId;
    if (input.controlActive && !input.brake && b.boostRemaining <= 0 && b.cooldownRemaining <= 0) b.boostRemaining = r.boostSeconds;
  }
  const limit = r.turnSlow + (r.turnFast - r.turnSlow) * Math.min(length(b.velocity) / r.engineLimit, 1);
  const target = input.steerMode === 'heading' ? clamp(angle(input.heading - b.heading) * r.headingGain, -limit, limit) : input.turn * limit;
  b.omega += clamp(target - b.omega, -r.angularAccel * DT, r.angularAccel * DT);
  b.heading = angle(b.heading + b.omega * DT);
  const f = { x: Math.cos(b.heading), y: Math.sin(b.heading) }, forward = dot(b.velocity, f), side = sub(b.velocity, mul(f, forward));
  const engine = input.brake ? 0 : r.engineAccel * input.throttle * Math.max(0, 1 - (Math.max(0, forward) / r.engineLimit) ** 2);
  const boost = b.boostRemaining > 0 && !input.brake ? r.boostAccel * Math.max(0, 1 - (Math.max(0, forward) / r.boostLimit) ** 2) : 0;
  b.velocity = add(b.velocity, mul(sub(sub(mul(f, engine + boost), mul(b.velocity, r.dragK)), mul(side, r.lateralK)), DT));
  if (input.brake && length(b.velocity) > 0) b.velocity = mul(b.velocity, Math.max(0, 1 - r.brakeAccel * DT / length(b.velocity)));
  if (b.boostRemaining > 0) {
    b.boostRemaining = Math.max(0, b.boostRemaining - DT);
    if (b.boostRemaining <= 1e-9) { b.boostRemaining = 0; b.cooldownRemaining = r.cooldownSeconds; }
  } else b.cooldownRemaining = Math.max(0, b.cooldownRemaining - DT);
  const segments: Segment[] = [], touching = new Set<string>(); let fraction = 0;
  const result = (fatalReason: string | null, fatalFraction: number, normalSpeed: number, contactLimit = false): Motion =>
    ({ segments, fatalReason, fatalFraction, normalSpeed, contactLimit });
  const updateContacts = () => {
    b.contacts.clear();
    for (const w of track.walls) if (distance(b.position, w.a, w.b) <= r.radius + r.contactSlop * 3) b.contacts.add(w.id);
    for (const o of track.obstacles) if (length(sub(b.position, o.center)) <= r.radius + o.radius + r.contactSlop * 3) b.contacts.add(o.id);
  };
  for (let iteration = 0; iteration < r.maxContacts; iteration++) {
    const from = b.position, delta = mul(b.velocity, DT * (1 - fraction)), hit = firstHit(from, delta, track, r.radius);
    if (!hit) {
      b.position = add(b.position, delta); segments.push({ from, to: b.position, startFraction: fraction, endFraction: 1 });
      updateContacts(); return result(null, 1, 0);
    }
    const end = fraction + (1 - fraction) * hit.t;
    b.position = add(b.position, mul(delta, hit.t)); segments.push({ from, to: b.position, startFraction: fraction, endFraction: end });
    const vn = Math.max(0, -dot(b.velocity, hit.normal));
    const newImpact = !b.contacts.has(hit.id) && !touching.has(hit.id);
    if (newImpact) {
      b.hp -= damage(vn);
      if (vn >= r.fatalSpeed || b.hp <= 0) { b.hp = 0; return result(hit.reason, end, vn); }
    }
    touching.add(hit.id);
    const nv = dot(b.velocity, hit.normal);
    b.velocity = add(mul(sub(b.velocity, mul(hit.normal, nv)), newImpact ? 1 - r.tangentLoss : 1), mul(hit.normal, vn * r.restitution));
    b.position = add(b.position, mul(hit.normal, r.contactSlop)); fraction = end;
    if (fraction >= 1 - 1e-10) { updateContacts(); return result(null, 1, vn); }
  }
  updateContacts(); return result(null, fraction, 0, true);
}
