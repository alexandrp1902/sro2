import type { RepChangeDto } from '../net/protocol';
import type { RosterEvent } from '../net/roster';

const SHOW_MS = 4000;
const FADE_MS = 400;
const MAX_ITEMS = 4;

/** Настоящее время — без рода: «Рейнджер-123 входит в систему». */
const VERBS = {
  joined: 'входит в систему',
  lost: 'теряет связь',
  back: 'снова на связи',
  left: 'покидает систему',
} as const;

export function describe(event: RosterEvent): string {
  return event.kind === 'renamed' ? `${event.from} теперь ${event.name}` : `${event.name} ${VERBS[event.kind]}`;
}

export function describeKill(killer: string, victim: string): string {
  return `${killer} уничтожает ${victim}`;
}

/** Корабль сгорел в жаре звезды — убийцы нет. */
export function describeBurn(victim: string): string {
  return `${victim} сгорает у звезды`;
}

/** Короткие уведомления сервера: он шлёт код, текст живёт здесь. */
const NOTICES: Record<string, string> = {
  cargoFull: 'Недостаточно места в трюме',
  cargoLost: 'Трюм потерян — груз остался в космосе на месте гибели',
  unloaded: 'Груз продан',
  jettisoned: 'Груз за бортом',
  tooFar: 'Слишком далеко',
  noCredits: 'Не хватает кредитов',
  notSold: 'Здесь этого не продают — ищите в другом регионе',
  noFuel: 'Не хватает топлива на прыжок',
  gateFar: 'Подлетите ближе к вратам',
  jumpCancelled: 'Прыжок сорван',
  jumpHit: 'Прыжок сбит — по вам попали',
  noPower: 'Не хватает энергии генератора',
  badClass: 'Класс не подходит к слоту',
  badSlot: 'Сюда это не встаёт',
  rangers: 'Рейнджеры вступились за торговца — уходите!',
  noGoods: 'Этим здесь не торгуют',
  noStock: 'На складе станции столько нет',
  dockClosed: 'Док закрыт: здесь вас считают врагом',
  needRep: 'Это продают только своим — здесь вас ещё не знают',
  missionAway: 'Вы отстаёте от конвоя — возвращайтесь!',
  ambush: 'Засада на курсе конвоя!',
  wing: 'Звено рейнджеров вышло с вами',
};

/** @returns текст уведомления или null, если код незнакомый (сервер новее клиента). */
/** За что двигают репутацию — по коду с сервера (M13). */
const REP_REASONS: Record<string, string> = {
  missionDone: 'задание выполнено',
  missionAbandon: 'задание брошено',
  missionFail: 'задание провалено',
  pirate: 'пират уничтожен',
  sos: 'помощь торговцу',
  invasion: 'вторжение отбито',
  traderAttack: 'атака торговца',
  traderKill: 'торговец уничтожен',
  rangerAttack: 'атака рейнджера',
  rangerKill: 'рейнджер уничтожен',
  playerKill: 'убийство пилота',
};

/**
 * Строка ленты об изменении репутации: «−15 Vega: атака торговца».
 * @param name имя места; нет — берём id из ключа
 */
export function describeRepChange(change: RepChangeDto, name?: string | null): string {
  const where = name ?? change.key.slice(change.key.indexOf(':') + 1);
  const sign = change.delta > 0 ? '+' : '';
  const why = REP_REASONS[change.code] ?? change.code;
  return `${sign}${change.delta} ${where}: ${why}`;
}

export function describeNotice(code: string): string | null {
  return NOTICES[code] ?? null;
}

/** Лента событий системы вверху экрана: сообщения живут несколько секунд. */
export class Feed {
  constructor(private readonly root: HTMLElement) {}

  push(events: RosterEvent[]): void {
    for (const event of events) this.add(describe(event));
  }

  /** alert — тревога (SOS): строка заметнее остальных. */
  add(text: string, alert = false): void {
    const item = document.createElement('div');
    item.className = alert ? 'feed-item feed-alert' : 'feed-item';
    item.textContent = text;
    this.root.append(item);
    while (this.root.children.length > MAX_ITEMS) this.root.firstElementChild!.remove();

    window.setTimeout(() => {
      item.classList.add('feed-out');
      window.setTimeout(() => item.remove(), FADE_MS);
    }, SHOW_MS);
  }
}
