import type { KillDto, ShotDto, SnapshotMsg } from './protocol';

export type CombatEvent = { kind: 'shot'; tick: number; shot: ShotDto } | { kind: 'kill'; tick: number; kill: KillDto };

/** События, отставшие от часов рендера больше чем на столько тиков (вкладка была в фоне), не проигрываются. */
const MAX_LAG_TICKS = 10;
const MAX_QUEUE = 400;

/**
 * Выстрелы и уничтожения для эффектов. Чужие проигрываются, когда часы интерполяции доходят до их тика: трассер
 * летит между нарисованными кораблями, и полоска корпуса падает вместе с попаданием. События своего корабля —
 * сразу по приходу, чтобы огонь ощущался отзывчиво.
 */
export class CombatEvents {
  private queue: CombatEvent[] = [];

  /** @returns события своего корабля — их проигрывать сразу */
  push(snapshot: SnapshotMsg, ownId: number): CombatEvent[] {
    const now: CombatEvent[] = [];
    const tick = snapshot.tick;
    for (const shot of snapshot.shots ?? []) {
      const event: CombatEvent = { kind: 'shot', tick, shot };
      if (shot.from === ownId || shot.to === ownId) now.push(event);
      else this.queue.push(event);
    }
    for (const kill of snapshot.kills ?? []) {
      const event: CombatEvent = { kind: 'kill', tick, kill };
      if (kill.id === ownId) now.push(event);
      else this.queue.push(event);
    }
    if (this.queue.length > MAX_QUEUE) this.queue.splice(0, this.queue.length - MAX_QUEUE);
    return now;
  }

  /** @returns события, до которых дошли часы рендера; сильно отставшие выбрасываются */
  take(renderTick: number): CombatEvent[] {
    if (Number.isNaN(renderTick)) return [];
    const due: CombatEvent[] = [];
    let keep = 0;
    for (const event of this.queue) {
      if (event.tick > renderTick) this.queue[keep++] = event;
      else if (event.tick >= renderTick - MAX_LAG_TICKS) due.push(event);
    }
    this.queue.length = keep;
    return due;
  }

  clear(): void {
    this.queue = [];
  }
}
