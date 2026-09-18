import type { MeteorThreat } from '../render/meteorView';

/** Ближе этого — «сворачивай сейчас»: рамка экрана пульсирует, телефон вибрирует. */
export const CRITICAL_SECONDS = 1.5;
const VIBRATE_MS = 120;

export interface AlarmText {
  text: string;
  critical: boolean;
}

/** Плашка тревоги: самый близкий камень и сколько их. @returns null — тревоги нет. */
export function describeAlarm(threats: readonly MeteorThreat[]): AlarmText | null {
  if (threats.length === 0) return null;
  const soonest = Math.min(...threats.map((t) => t.seconds));
  const what = threats.length > 1 ? `МЕТЕОРИТЫ ×${threats.length}` : 'МЕТЕОРИТ';
  return { text: `${what} · ${soonest.toFixed(1)} с`, critical: soonest < CRITICAL_SECONDS };
}

/**
 * Предупреждение о таране в HUD: плашка с отсчётом до касания, а в последние полторы секунды — пульс рамки экрана
 * и короткая вибрация (где она есть; iOS Safari её не умеет). Вибрация — раз на камень, иначе телефон жужжит без конца.
 */
export class MeteorAlarm {
  private readonly vibrated = new Set<number>();
  private shown = '';

  constructor(
    private readonly banner: HTMLElement,
    private readonly edge: HTMLElement,
  ) {}

  update(threats: readonly MeteorThreat[]): void {
    const alarm = describeAlarm(threats);
    this.banner.hidden = !alarm;
    this.edge.hidden = !alarm?.critical;
    if (alarm) {
      if (alarm.text !== this.shown) {
        this.shown = alarm.text;
        this.banner.textContent = alarm.text;
      }
      this.banner.dataset.critical = String(alarm.critical);
    }

    for (const id of this.vibrated) {
      if (!threats.some((t) => t.id === id)) this.vibrated.delete(id);
    }
    for (const threat of threats) {
      if (threat.seconds >= CRITICAL_SECONDS || this.vibrated.has(threat.id)) continue;
      this.vibrated.add(threat.id);
      navigator.vibrate?.(VIBRATE_MS);
    }
  }
}
