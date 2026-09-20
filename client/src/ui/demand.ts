import type { DemandMsg } from '../net/protocol';
import { clock } from '../util/clock';
import type { InvasionLines } from './invasion';

/** Итог висит на табло столько, потом табло гаснет. */
const RESULT_MS = 10000;
/** Без новостей столько — событие считаем забытым (связь рвалась, сервер перезапустили). */
const STALE_MS = 5000;

/** Перечисление товаров словами: «оружие и медикаменты». */
export function goodsLine(goods: string[], name: (good: string) => string): string {
  return goods.map(name).join(' и ');
}

/**
 * Событие спроса (M15.5) на клиенте: строки в ленту, табло и метка системы на карте.
 *
 * Смысл события в том, что туда слетятся все, — поэтому объявляется оно всей галактике и сразу говорит,
 * что именно просят: иначе выигрывал бы тот, кто и так был рядом.
 */
export class DemandBoard {
  private last: DemandMsg | null = null;
  private seenAt = 0;
  /** Отсчёт идёт между рассылками сам — сервер присылает секунды раз в секунду. */
  private secondsAt = 0;

  /** @returns строка для ленты (и тревожная ли она) или null — просто очередной отсчёт. */
  apply(message: DemandMsg, now: number, mySystem: string, name: (good: string) => string): { text: string; alert: boolean } | null {
    const prev = this.last;
    this.last = message;
    this.seenAt = now;
    this.secondsAt = now;
    const here = message.system === mySystem;
    const where = here ? 'здесь' : `${message.placeName} (${message.systemName})`;
    const goods = goodsLine(message.goods, name);
    if (message.state === 'announce') {
      if (prev?.state === 'announce' && prev.place === message.place) return null;
      return { text: `${message.title.toUpperCase()}: ${where} нужны ${goods} — ${message.quota} ед., приём через ${clock(message.secondsLeft)}`, alert: true };
    }
    if (message.state === 'open') {
      if (prev?.state === 'open' && prev.place === message.place) return null;
      return { text: `${message.title}: приём открыт — ${where} берут ${goods} втридорога`, alert: here };
    }
    if (message.state === 'filled') return { text: `${message.title}: спрос закрыт, ${message.placeName} обеспечен`, alert: false };
    // Без склонений: названия товаров приходят из loot.json в именительном, и «остался без Оружие» не напишешь.
    return { text: `${message.title}: срок вышел, ${message.placeName} помощи не дождался`, alert: false };
  }

  /** Система события — для метки на карте галактики; null — нет. */
  system(now: number): string | null {
    const m = this.current(now);
    return m && (m.state === 'announce' || m.state === 'open') ? m.system : null;
  }

  /** Место события; null — нет. */
  place(now: number): string | null {
    const m = this.current(now);
    return m && (m.state === 'announce' || m.state === 'open') ? m.place : null;
  }

  /** Табло; null — прятать. */
  lines(now: number, name: (good: string) => string): InvasionLines | null {
    const m = this.current(now);
    if (!m) return null;
    const left = Math.max(0, m.secondsLeft - (now - this.secondsAt) / 1000);
    const title = `${m.title.toUpperCase()} · ${m.placeName}`;
    const goods = goodsLine(m.goods, name);
    switch (m.state) {
      case 'announce':
        return { title, hint: `Нужны ${goods} · приём через ${clock(left)}`, alert: true };
      case 'open': {
        const mul = m.mul >= 10 ? Math.round(m.mul) : Math.round(m.mul * 10) / 10;
        return { title, hint: `${goods} по ×${mul} · осталось ${m.left} из ${m.quota} · ${clock(left)}`, alert: true };
      }
      case 'filled':
        return { title, hint: 'Спрос закрыт: довезли всё', alert: false };
      default:
        return { title, hint: `Срок вышел: не довезли ${m.left} из ${m.quota}`, alert: false };
    }
  }

  clear(): void {
    this.last = null;
  }

  private current(now: number): DemandMsg | null {
    const m = this.last;
    if (!m) return null;
    const final = m.state === 'filled' || m.state === 'over';
    if (now - this.seenAt > (final ? RESULT_MS : STALE_MS)) {
      this.last = null;
      return null;
    }
    return m;
  }
}
