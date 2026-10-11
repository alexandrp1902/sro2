import { keymap } from '../input/keymap';
import type { Input } from './types';

/** Throttle is selected speed, independent of steering. */
export class RaceInput {
  value: Input = { seq: 0, steerMode: 'turn', heading: 0, turn: 0, throttle: 0, brake: false, controlActive: true };
  private held = new Set<string>();
  private stickPointer: number | null = null;
  private brakePointer: number | null = null;
  private abort: AbortController | null = null;
  private captures = new Map<number, HTMLElement>();
  private knob: HTMLElement | null = null;
  constructor(private changed: () => void, private menu: () => void, private running: () => boolean) {}
  private holds(action: 'thrust' | 'brake' | 'left' | 'right', code: string): boolean {
    const defaults = { thrust: ['KeyW', 'ArrowUp'], brake: ['KeyS', 'ArrowDown'], left: ['KeyA', 'ArrowLeft'], right: ['KeyD', 'ArrowRight'] };
    return defaults[action].includes(code) || keymap.holds(action, code);
  }
  bind(root: HTMLElement, stick: HTMLElement, knob: HTMLElement, brake: HTMLButtonElement): void {
    this.unbind(); this.knob = knob;
    this.abort = new AbortController(); const signal = this.abort.signal;
    const reset = (active: boolean) => { this.safe(active); this.changed(); };
    window.addEventListener('keydown', e => {
      e.stopImmediatePropagation();
      if (e.code !== 'Escape' && e.code !== 'Space' && !(['thrust', 'brake', 'left', 'right'] as const).some(a => this.holds(a, e.code))) return;
      if (e.code === 'Escape') { e.preventDefault(); if (!e.repeat) this.menu(); return; }
      e.preventDefault(); if (!this.running()) return;
      this.value.controlActive = true;
      const first = !this.held.has(e.code); this.held.add(e.code);
      if (first && this.holds('thrust', e.code)) this.setSpeed(this.value.throttle + .06);
      if (first && this.holds('brake', e.code)) this.setSpeed(this.value.throttle - .06);
      if (this.holds('left', e.code) || this.holds('right', e.code)) this.value.steerMode = 'turn';
      this.updateKeys(); this.changed();
    }, { capture: true, signal });
    window.addEventListener('keyup', e => {
      if (!this.held.has(e.code)) return;
      e.stopImmediatePropagation(); e.preventDefault(); this.held.delete(e.code);
      this.updateKeys(); this.changed();
    }, { capture: true, signal });
    window.addEventListener('blur', () => reset(false), { signal });
    document.addEventListener('visibilitychange', () => { if (document.hidden) reset(false); }, { signal });
    window.addEventListener('focus', () => { this.value.controlActive = true; this.changed(); }, { signal });
    window.addEventListener('resize', () => reset(document.hasFocus()), { signal });
    root.addEventListener('wheel', e => e.preventDefault(), { passive: false, signal });
    root.addEventListener('contextmenu', e => e.preventDefault(), { signal });
    root.addEventListener('pointerdown', e => { if (e.pointerType === 'touch') root.classList.add('race-touch'); }, { signal });
    const capture = (e: PointerEvent, node: HTMLElement) => {
      node.setPointerCapture(e.pointerId); this.captures.set(e.pointerId, node); e.preventDefault();
      this.value.controlActive = true;
    };
    const moveStick = (e: PointerEvent) => {
      if (e.pointerId !== this.stickPointer || !this.running()) return;
      const box = stick.getBoundingClientRect(), radius = box.width * .4;
      const x = e.clientX - box.left - box.width / 2, y = e.clientY - box.top - box.height / 2;
      const magnitude = Math.hypot(x, y), scale = magnitude > radius ? radius / magnitude : 1;
      this.setSpeed(Math.min(1, magnitude / radius));
      if (magnitude > 4) this.value.heading = Math.atan2(y, x);
      this.value.steerMode = 'heading'; this.value.turn = 0;
      knob.style.transform = `translate(${x * scale}px, ${y * scale}px)`; this.changed();
    };
    stick.addEventListener('pointerdown', e => {
      if (!this.running() || this.stickPointer !== null) return;
      this.stickPointer = e.pointerId; capture(e, stick); moveStick(e);
    }, { signal });
    stick.addEventListener('pointermove', moveStick, { signal });
    brake.addEventListener('pointerdown', e => {
      if (!this.running() || this.brakePointer !== null) return;
      this.brakePointer = e.pointerId; capture(e, brake); this.updateKeys(); this.changed();
    }, { signal });
    for (const node of [stick, brake]) {
      node.addEventListener('pointerup', e => {
        if (e.pointerId === this.stickPointer) { this.stickPointer = null; }
        if (e.pointerId === this.brakePointer) { this.brakePointer = null; this.updateKeys(); this.changed(); }
        this.captures.delete(e.pointerId);
      }, { signal });
      node.addEventListener('pointercancel', () => reset(false), { signal });
      node.addEventListener('lostpointercapture', e => { if (this.captures.has(e.pointerId)) reset(false); }, { signal });
    }
  }
  update(dt: number): void {
    if (!this.running()) return;
    const up = [...this.held].some(k => this.holds('thrust', k));
    const down = [...this.held].some(k => this.holds('brake', k));
    if (up !== down) this.setSpeed(this.value.throttle + (up ? 1 : -1) * dt * .55);
  }
  private setSpeed(value: number): void {
    this.value.throttle = Math.max(0, Math.min(1, value));
  }
  private updateKeys(): void {
    this.value.brake = this.held.has('Space') || this.brakePointer !== null;
    this.value.turn = Number([...this.held].some(k => this.holds('right', k))) - Number([...this.held].some(k => this.holds('left', k)));
  }
  safe(active: boolean): void {
    this.held.clear(); this.release(); this.setSpeed(0);
    if (this.knob) this.knob.style.transform = '';
    this.value = { ...this.value, turn: 0, steerMode: 'turn', brake: false, controlActive: active };
  }
  private release(): void {
    this.stickPointer = this.brakePointer = null;
    const captures = [...this.captures]; this.captures.clear();
    for (const [id, node] of captures) if (node.hasPointerCapture(id)) node.releasePointerCapture(id);
  }
  unbind(): void { this.abort?.abort(); this.abort = null; this.safe(true); this.knob = null; }
}
