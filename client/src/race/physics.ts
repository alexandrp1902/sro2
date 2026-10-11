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
  contacts: Set<string>;
}
export interface Segment { from: Vec; to: Vec; startFraction: number; endFraction: number }
export interface Impact { position: Vec; normal: Vec; velocity: Vec; speed: number }
export interface Motion { segments: Segment[]; impacts: Impact[]; fatalReason: string | null; fatalFraction: number; normalSpeed: number; contactLimit: boolean; hpBeforeFatal?: number }
export function body(track: Track, rules: Rules): Body {
  return { position: { ...track.spawn }, velocity: { x: 0, y: 0 }, heading: track.spawnHeading, omega: 0,
    hp: rules.hp, contacts: new Set() };
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
interface Hit { t: number; normal: Vec; id: string; reason: string }
// Static canyon geometry is indexed once; a vehicle only checks nearby rock banks.
const wallIndexes = new WeakMap<Track, Map<string, Track['walls']>>();
const CELL = 12;
function nearbyWalls(track: Track, p: Vec, d: Vec, radius: number): Track['walls'] {
  let index = wallIndexes.get(track);
  if (!index) {
    index = new Map();
    for (const wall of track.walls) {
      for (let x = Math.floor(Math.min(wall.a.x, wall.b.x) / CELL); x <= Math.floor(Math.max(wall.a.x, wall.b.x) / CELL); x++) {
        for (let y = Math.floor(Math.min(wall.a.y, wall.b.y) / CELL); y <= Math.floor(Math.max(wall.a.y, wall.b.y) / CELL); y++) {
          const key = `${x}:${y}`, bucket = index.get(key) ?? [];
          bucket.push(wall); index.set(key, bucket);
        }
      }
    }
    wallIndexes.set(track, index);
  }
  const walls = new Set<Track['walls'][number]>();
  for (let x = Math.floor((Math.min(p.x, p.x + d.x) - radius) / CELL); x <= Math.floor((Math.max(p.x, p.x + d.x) + radius) / CELL); x++) {
    for (let y = Math.floor((Math.min(p.y, p.y + d.y) - radius) / CELL); y <= Math.floor((Math.max(p.y, p.y + d.y) + radius) / CELL); y++) {
      for (const wall of index.get(`${x}:${y}`) ?? []) walls.add(wall);
    }
  }
  return [...walls];
}
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
  for (const w of nearbyWalls(track, p, d, radius)) {
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
  const limit = r.turnSlow + (r.turnFast - r.turnSlow) * Math.min(length(b.velocity) / r.engineLimit, 1);
  const target = input.steerMode === 'heading' ? clamp(angle(input.heading - b.heading) * r.headingGain, -limit, limit) : input.turn * limit;
  b.omega += clamp(target - b.omega, -r.angularAccel * DT, r.angularAccel * DT);
  b.heading = angle(b.heading + b.omega * DT);
  const f = { x: Math.cos(b.heading), y: Math.sin(b.heading) }, forward = dot(b.velocity, f), side = sub(b.velocity, mul(f, forward));
  const speed = length(b.velocity), desired = input.throttle * r.engineLimit;
  const engine = input.brake || !input.controlActive ? 0 : clamp((desired - speed) * 2 + speed * r.dragK, 0, r.engineAccel);
  b.velocity = add(b.velocity, mul(sub(sub(mul(f, engine), mul(b.velocity, r.dragK)), mul(side, r.lateralK)), DT));
  const deceleration = input.brake ? r.brakeAccel : input.controlActive ? Math.min(r.coastBrakeAccel, Math.max(0, speed - desired) * 2) : 0;
  if (deceleration > 0 && length(b.velocity) > 0) b.velocity = mul(b.velocity, Math.max(0, 1 - deceleration * DT / length(b.velocity)));
  const segments: Segment[] = [], impacts: Impact[] = [], touching = new Set<string>(); let fraction = 0;
  const result = (fatalReason: string | null, fatalFraction: number, normalSpeed: number, contactLimit = false): Motion =>
    ({ segments, impacts, fatalReason, fatalFraction, normalSpeed, contactLimit });
  const updateContacts = () => {
    b.contacts.clear();
    for (const w of nearbyWalls(track, b.position, { x: 0, y: 0 }, r.radius + r.contactSlop * 3)) if (distance(b.position, w.a, w.b) <= r.radius + r.contactSlop * 3) b.contacts.add(w.id);
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
      if (vn > .2) impacts.push({ position: sub(b.position, mul(hit.normal, r.radius)),
        normal: { ...hit.normal }, velocity: { ...b.velocity }, speed: vn });
      const hpBeforeFatal = b.hp;
      b.hp -= damage(vn);
      if (vn >= r.fatalSpeed || b.hp <= 0) { b.hp = 0; return { ...result(hit.reason, end, vn), hpBeforeFatal }; }
    }
    touching.add(hit.id);
    const nv = dot(b.velocity, hit.normal);
    b.velocity = add(mul(sub(b.velocity, mul(hit.normal, nv)), newImpact ? 1 - r.tangentLoss : 1), mul(hit.normal, vn * r.restitution));
    b.position = add(b.position, mul(hit.normal, r.contactSlop)); fraction = end;
    if (fraction >= 1 - 1e-10) { updateContacts(); return result(null, 1, vn); }
  }
  updateContacts(); return result(null, fraction, 0, true);
}
