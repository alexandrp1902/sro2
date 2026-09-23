/**
 * Что сейчас играет: покой, бой, док или гибель. Чистый модуль — правило перехода проверяется тестами.
 *
 * Главное здесь — несимметричность. Бой включается от одного признака и мгновенно, а выключается только
 * через семь секунд тишины: между залпами пауза в полторы-две секунды обычное дело, и без выдержки музыка
 * дёргалась бы туда-сюда весь бой.
 */

export type Mood = 'calm' | 'combat' | 'dock' | 'dead';

/** Сколько тишины должно пройти, чтобы бой кончился. */
export const COMBAT_HOLD_MS = 7000;

/** Ближе этого враждебный NPC считается угрозой, даже если ещё не выстрелил. */
export const BATTLE_RANGE = 1400;

export interface MoodSignals {
  now: number;
  /** Когда по мне стреляли в последний раз, мс по часам кадра; 0 — не стреляли. */
  lastHostileShot: number;
  /** Когда я сам стрелял по кораблю. Стрельба по метеоритам сюда не идёт: добыча минералов — не бой. */
  lastOwnShot: number;
  /** Сколько враждебных кораблей держат меня целью. */
  threats: number;
  /** Сколько ракет летит в меня. */
  incoming: number;
  docked: boolean;
  dead: boolean;
}

export function nextMood(s: MoodSignals): Mood {
  if (s.dead) return 'dead';
  if (s.docked) return 'dock';
  const fresh = (at: number) => at > 0 && s.now - at < COMBAT_HOLD_MS;
  if (s.threats > 0 || s.incoming > 0 || fresh(s.lastHostileShot) || fresh(s.lastOwnShot)) return 'combat';
  return 'calm';
}
