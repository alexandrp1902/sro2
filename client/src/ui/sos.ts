import type { SosMsg } from '../net/protocol';
import { formatCredits } from '../sim/shop';

/** Без нового «где я» столько — SOS гаснет сам: сервер шлёт его раз в секунду, а торговец мог уйти с перезаливкой. */
const STALE_MS = 3000;

interface Call {
  name: string;
  x: number;
  y: number;
  seenAt: number;
}

/**
 * SOS торговцев системы: кто сейчас зовёт на помощь и где — для мигающей метки на миникарте, — и строка в ленту
 * о начале и конце. Помогавший пилот узнаёт из конца, сколько ему заплатили.
 */
export class SosBoard {
  private readonly calls = new Map<number, Call>();

  /** @returns строка для ленты или null — это лишь очередное «где я». */
  apply(message: SosMsg, now: number): string | null {
    const known = this.calls.has(message.id);
    if (message.state === 'on') {
      this.calls.set(message.id, { name: message.name, x: message.x, y: message.y, seenAt: now });
      return known ? null : `SOS! ${message.name} атакован — помогите, он заплатит`;
    }
    this.calls.delete(message.id);
    if (message.state === 'lost') return `${message.name} погиб — SOS снят`;
    if (message.reward > 0) return `${message.name} спасён и благодарит вас: +${formatCredits(message.reward)}`;
    return `${message.name} отбился — SOS снят`;
  }

  /** Кто зовёт сейчас; живое место торговца в радаре точнее, чем из последнего SOS. */
  active(now: number, live: (id: number) => { x: number; y: number } | null): { x: number; y: number }[] {
    const out: { x: number; y: number }[] = [];
    for (const [id, call] of this.calls) {
      if (now - call.seenAt > STALE_MS) {
        this.calls.delete(id);
        continue;
      }
      out.push(live(id) ?? { x: call.x, y: call.y });
    }
    return out;
  }

  /** Другая система или связь пропала: чужие SOS больше не наши. */
  clear(): void {
    this.calls.clear();
  }
}
