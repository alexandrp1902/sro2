import { keymap } from '../input/keymap';
import type { Input } from './types';

export class RaceInput {
  value: Input = { seq: 0, steerMode: 'turn', heading: 0, turn: 0, throttle: 0, brake: false, boostPressId: 0, controlActive: true };
  private held = new Set<string>();
  private brakePointer: number | null = null;
  private stickPointer: number | null = null;
  private keyboardBrake = false;
  private abort: AbortController | null = null;
  private captures = new Map<number, HTMLElement>();
  constructor(private changed: () => void, private menu: () => void, private running: () => boolean) {}
  bind(root: HTMLElement, stick: HTMLElement, knob: HTMLElement, brake: HTMLButtonElement, boost: HTMLButtonElement): void {
    this.unbind(); this.abort = new AbortController(); const signal = this.abort.signal;
    const reset = (active: boolean) => { this.safe(active); this.release(); knob.style.transform = ''; this.changed(); };
    window.addEventListener('keydown', e => {
      e.stopImmediatePropagation();
      if (e.code === 'Escape') { e.preventDefault(); if (!e.repeat) this.menu(); return; }
      if (e.target instanceof HTMLElement && e.target.closest('button,input')) {
        if (e.code === 'Enter') return;
      }
      e.preventDefault(); if (!this.running()) return;
      this.value.controlActive = true;
      this.held.add(e.code);
      if (keymap.holds('thrust', e.code)) this.value.throttle = 1;
      if (keymap.holds('brake', e.code)) this.value.throttle = 0;
      if (e.code === 'Space' || keymap.holds('fire', e.code)) this.keyboardBrake = true;
      if (e.code === 'ShiftLeft' || e.code === 'ShiftRight') { if (!e.repeat && !this.value.brake) this.value.boostPressId++; }
      this.value.steerMode = 'turn'; this.updateKeys(); this.changed();
    }, { capture: true, signal });
    window.addEventListener('keyup', e => {
      e.stopImmediatePropagation(); this.held.delete(e.code);
      if (e.code === 'Space' || keymap.holds('fire', e.code)) this.keyboardBrake = false;
      this.updateKeys(); this.changed();
    }, { capture: true, signal });
    window.addEventListener('blur', () => reset(false), { signal });
    document.addEventListener('visibilitychange', () => { if (document.hidden) reset(false); }, { signal });
    window.addEventListener('focus', () => { this.value.controlActive = true; this.changed(); }, { signal });
    window.addEventListener('resize', () => reset(document.hasFocus()), { signal });
    root.addEventListener('wheel', e => e.preventDefault(), { passive: false, signal });
    root.addEventListener('contextmenu', e => e.preventDefault(), { signal });
    const capture = (e: PointerEvent, node: HTMLElement) => {
      node.setPointerCapture(e.pointerId); this.captures.set(e.pointerId, node); e.preventDefault();
      this.value.controlActive = true;
    };
    const move = (e: PointerEvent) => {
      if (e.pointerId !== this.stickPointer || !this.running()) return;
      const box = stick.getBoundingClientRect(), radius = box.width * .4;
      const x = e.clientX - box.left - box.width / 2, y = e.clientY - box.top - box.height / 2;
      const magnitude = Math.hypot(x, y), scale = magnitude > radius ? radius / magnitude : 1;
      this.value.throttle = Math.min(1, magnitude / radius);
      if (magnitude > 4) this.value.heading = Math.atan2(y, x);
      this.value.steerMode = 'heading'; this.value.turn = 0;
      knob.style.transform = `translate(${x * scale}px, ${y * scale}px)`; this.changed();
    };
    stick.addEventListener('pointerdown', e => {
      if (!this.running() || this.stickPointer !== null) return;
      this.stickPointer = e.pointerId; capture(e, stick); move(e);
    }, { signal });
    stick.addEventListener('pointermove', move, { signal });
    stick.addEventListener('pointerup', e => { if (e.pointerId === this.stickPointer) { this.stickPointer = null; this.captures.delete(e.pointerId); } }, { signal });
    stick.addEventListener('dblclick', () => { this.value.throttle = 0; knob.style.transform = ''; this.changed(); }, { signal });
    brake.addEventListener('pointerdown', e => {
      if (!this.running() || this.brakePointer !== null) return;
      this.brakePointer = e.pointerId; capture(e, brake); this.value.brake = true; this.changed();
    }, { signal });
    brake.addEventListener('pointerup', e => {
      if (e.pointerId !== this.brakePointer) return;
      this.brakePointer = null; this.captures.delete(e.pointerId); this.value.brake = this.keyboardBrake; this.changed();
    }, { signal });
    for (const node of [stick, brake]) {
      node.addEventListener('pointercancel', () => reset(false), { signal });
      node.addEventListener('lostpointercapture', e => {
        if (this.captures.has(e.pointerId)) reset(false);
      }, { signal });
    }
    boost.addEventListener('pointerdown', e => {
      e.preventDefault(); if (!this.running() || this.value.brake || boost.disabled) return;
      this.value.boostPressId++; this.changed();
    }, { signal });
  }
  private updateKeys(): void {
    this.value.brake = this.keyboardBrake || this.brakePointer !== null;
    this.value.turn = Number([...this.held].some(k => keymap.holds('right', k))) - Number([...this.held].some(k => keymap.holds('left', k)));
  }
  safe(active: boolean): void {
    this.held.clear(); this.keyboardBrake = false;
    this.release();
    this.value = { ...this.value, throttle: 0, turn: 0, steerMode: 'turn', brake: false, controlActive: active };
  }
  private release(): void {
    this.brakePointer = this.stickPointer = null;
    const captures = [...this.captures]; this.captures.clear();
    for (const [id, node] of captures) if (node.hasPointerCapture(id)) node.releasePointerCapture(id);
  }
  unbind(): void { this.abort?.abort(); this.abort = null; this.safe(true); this.release(); }
}
