import type { MissionOffer, MissionsMsg } from '../net/protocol';
import { nearestStation, nextHop, type GalaxyDto } from './galaxy';

/** Имена для текста заданий: сервер шлёт id системы, типа пирата и предмета. */
export interface MissionNames {
  system(id: string): string;
  npc(type: string): string;
  item(id: string): string;
  /** Имя места по ключу («st:vega», «pl:terra»); с M15 в системе их несколько. */
  place(key: string): string;
}

/** Куда сдавать: место назначения, а если его нет — там же, где взяли. */
export function destination(offer: MissionOffer): string {
  return offer.place ?? offer.from;
}

/** Это поселение на планете, а не станция: от этого «сядьте» вместо «пристыкуйтесь». */
function onAPlanet(key: string | null | undefined): boolean {
  return !!key?.startsWith('pl:');
}

type Active = NonNullable<MissionsMsg['active']>;
type Done = NonNullable<MissionsMsg['done']>;

/** Остаток срока «м:сс»; null — срока нет или он вышел. */
export function timeLeft(until: number | undefined, now: number): string | null {
  if (!until) return null;
  const left = Math.floor(until - now / 1000);
  if (left <= 0) return null;
  return `${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`;
}

/** «крупные метеориты» или просто «метеориты»: размер называем только когда он важен. */
function rocks(size: string | null | undefined): string {
  if (size === 'large') return 'крупные метеориты';
  if (size === 'medium') return 'средние метеориты';
  if (size === 'small') return 'мелкие метеориты';
  return 'метеориты';
}

/** Строка задания на доске станции: «Уничтожить: пираты ×4 · Vega». */
export function offerTitle(offer: MissionOffer, names: MissionNames): string {
  switch (offer.kind) {
    case 'kill':
      return `Уничтожить: ${offer.npc ? names.npc(offer.npc) : 'пираты'} ×${offer.count} · ${names.system(offer.system ?? '')}`;
    case 'collect':
      return `Собрать: ${names.item(offer.item ?? '')} ×${offer.count}`;
    case 'deliver':
      return `Доставить груз: ${names.place(destination(offer))} · ${offer.count} ед.`;
    case 'escort':
      return `Сопровождение: конвой к вратам на ${names.system(offer.system ?? '')}`;
    case 'patrol':
      return `Патруль с рейнджерами: ${offer.count} точки маршрута`;
    case 'courier':
      return `Важное письмо: ${names.place(destination(offer))}`;
    case 'hunt':
      return `Охота: ${rocks(offer.size)} ×${offer.count} · ${names.system(offer.system ?? '')}`;
    case 'defend':
      return `Оборона поселения: ${offer.count} волны с орбиты`;
  }
}

/** Подробность под строкой на доске: где и как сдаётся. */
export function offerNote(offer: MissionOffer, names: MissionNames): string {
  switch (offer.kind) {
    case 'kill':
      return 'награда — сразу за последнего';
    case 'collect':
      return 'сдать в любом доке';
    case 'deliver':
      return `${names.system(offer.system ?? '')} · груз займёт ${offer.count} ед. трюма`;
    case 'escort':
      return `держитесь рядом; в пути засад: ${offer.count}`;
    case 'patrol':
      return 'где-то на маршруте засада; звено ждёт вас';
    case 'courier':
      return offer.seconds
        ? `срок ${Math.round(offer.seconds / 60)} мин; место в трюме не занимает`
        : 'место в трюме не занимает';
    case 'hunt':
      return 'таран не в счёт: камень надо расстрелять';
    case 'defend':
      return `${names.place(destination(offer))} · держитесь рядом: пропустите троих — работа сорвана`;
  }
}

/** Главная строка трекера: «Пираты в Vega: 2/4». */
export function activeLine(active: Active, names: MissionNames, now = Date.now()): string {
  const { offer, progress } = active;
  switch (offer.kind) {
    case 'kill':
      return `${offer.npc ? names.npc(offer.npc) : 'Пираты'} в ${names.system(offer.system ?? '')}: ${progress}/${offer.count}`;
    case 'collect':
      return `${names.item(offer.item ?? '')}: ${progress}/${offer.count}`;
    case 'deliver':
      return `Груз в ${names.system(offer.system ?? '')} · ${offer.count} ед.`;
    case 'escort':
      return `Конвой к вратам на ${names.system(offer.system ?? '')}: засады ${progress}/${offer.count}`;
    case 'patrol':
      return `Патруль: точка ${Math.min(progress + 1, offer.count)}/${offer.count}`;
    case 'courier': {
      const left = timeLeft(active.until, now);
      return `Письмо в ${names.system(offer.system ?? '')}${left ? ` · ${left}` : ''}`;
    }
    case 'hunt': {
      const name = rocks(offer.size);
      return `${name[0].toUpperCase()}${name.slice(1)} в ${names.system(offer.system ?? '')}: ${progress}/${offer.count}`;
    }
    case 'defend':
      return `Оборона ${names.place(destination(offer))}: волна ${Math.min(progress + 1, offer.count)}/${offer.count}`;
  }
}

/** Что делать дальше: вторая строка трекера. */
export function activeHint(active: Active, here: string | null, docked: boolean, names: MissionNames): string {
  const { offer, progress } = active;
  switch (offer.kind) {
    case 'kill':
      return here === offer.system ? 'уничтожайте их здесь' : `летите в ${names.system(offer.system ?? '')}`;
    case 'collect':
      if (progress < offer.count) return 'добудьте в космосе';
      return docked ? 'сдайте на вкладке «Задания»' : 'сдайте на любой станции';
    case 'deliver':
      return here === offer.system
        ? onAPlanet(destination(offer))
          ? 'садитесь в поселении'
          : 'пристыкуйтесь к станции'
        : `летите в ${names.system(offer.system ?? '')}`;
    case 'escort':
      return docked ? 'вылетайте: конвой ждёт' : 'держитесь рядом с конвоем';
    case 'patrol':
      return docked ? 'вылетайте: звено ждёт' : 'подойдите к точке маршрута';
    case 'courier':
      return here === offer.system
        ? onAPlanet(destination(offer))
          ? 'садитесь в поселении'
          : 'пристыкуйтесь к станции'
        : `летите в ${names.system(offer.system ?? '')}`;
    case 'hunt':
      return here === offer.system ? 'расстреливайте камни' : `летите в ${names.system(offer.system ?? '')}`;
    case 'defend':
      return docked ? 'вылетайте: налёт уже идёт' : 'держитесь у поселения и бейте налётчиков';
  }
}

/** Почему задание провалено — строкой для ленты (M14). */
const FAIL_REASONS: Record<string, string> = {
  raid: 'поселение разграблено',
  trader: 'конвой погиб',
  away: 'вы отстали от конвоя',
  wing: 'звено уничтожено',
  dead: 'вы погибли',
  left: 'вы покинули систему',
  time: 'срок вышел',
};

/** Строка в ленту о сделанном: «✓ Уничтожьте учебный дрон · +100 кр». */
export function doneLines(done: Done): string[] {
  if (done.kind === 'failed') {
    return [`✗ Задание провалено: ${FAIL_REASONS[done.reason ?? ''] ?? 'работа сорвана'}`];
  }
  const reward = done.reward > 0 ? ` · +${done.reward} кр` : '';
  if (done.kind === 'mission') return [`✓ Задание выполнено${reward}`];
  const lines = [`✓ ${done.title ?? 'Шаг обучения'}${reward}`];
  if (done.last) lines.push('Обучение пройдено! Задания — на станции');
  return lines;
}

/**
 * На что указывает маркер цели. Врата — следующего прыжка по пути (to = null — любые, ближайшие).
 * У живых заданий M14 цель называет сервер: конвой ходит сам, а точки маршрута знает только комната.
 * null — показывать нечего: в доке, задание «добыть» ещё не собрано, заданий нет.
 */
export type Objective =
  | { kind: 'drone' }
  | { kind: 'loot' }
  | { kind: 'station' }
  | { kind: 'pirate'; npc: string | null }
  | { kind: 'gate'; to: string | null }
  | { kind: 'ship'; id: number }
  | { kind: 'point'; x: number; y: number }
  | { kind: 'meteor'; size: string | null };

export function objective(
  missions: MissionsMsg | null,
  here: string | null,
  galaxy: GalaxyDto | null,
  docked: boolean,
): Objective | null {
  if (!missions || !here || docked) return null;
  const gateTo = (to: string): Objective | null => {
    const hop = galaxy ? nextHop(galaxy, here, to) : null;
    return hop ? { kind: 'gate', to: hop } : null;
  };
  const tutorial = missions.tutorial;
  if (tutorial) {
    switch (tutorial.id) {
      case 'drone':
        return { kind: 'drone' };
      case 'grab':
        return { kind: 'loot' };
      case 'sell':
        return { kind: 'station' };
      case 'jump':
        return { kind: 'gate', to: null };
      default:
        return null;
    }
  }
  const active = missions.active;
  if (!active) return null;
  const { offer } = active;
  switch (offer.kind) {
    case 'kill':
      return here === offer.system ? { kind: 'pirate', npc: offer.npc ?? null } : gateTo(offer.system ?? '');
    case 'deliver':
    case 'courier':
      return here === offer.system ? { kind: 'station' } : gateTo(offer.system ?? '');
    case 'hunt':
      return here === offer.system ? { kind: 'meteor', size: offer.size ?? null } : gateTo(offer.system ?? '');
    case 'escort':
    case 'patrol':
    case 'defend': {
      // У живых заданий цель называет сервер: конвой ходит сам, а поселение едет по орбите.
      const mark = missions.mark;
      if (!mark) return null;
      return mark.ship ? { kind: 'ship', id: mark.ship } : { kind: 'point', x: mark.x, y: mark.y };
    }
    case 'collect': {
      if (active.progress < offer.count) return null;
      const station = galaxy ? nearestStation(galaxy, here) : here;
      if (!station) return null;
      return station === here ? { kind: 'station' } : gateTo(station);
    }
  }
}

/** Система цели для карты галактики; null — цель здесь или её нет. */
export function objectiveSystem(missions: MissionsMsg | null, here: string | null, galaxy: GalaxyDto | null): string | null {
  const active = missions?.tutorial ? null : missions?.active;
  if (!active || !here) return null;
  const { offer } = active;
  // Конвой и патруль целиком укладываются в эту систему: на карте галактики им указывать не на что.
  if (offer.kind === 'escort' || offer.kind === 'patrol') return null;
  if (offer.kind === 'collect') {
    if (active.progress < offer.count || !galaxy) return null;
    const station = nearestStation(galaxy, here);
    return station === here ? null : station;
  }
  return offer.system === here ? null : (offer.system ?? null);
}

/** Строки трекера цели: обучение важнее задания. null — прятать. */
export function trackerLines(
  missions: MissionsMsg | null,
  here: string | null,
  docked: boolean,
  names: MissionNames,
): { title: string; hint: string } | null {
  if (!missions) return null;
  const tutorial = missions.tutorial;
  if (tutorial) {
    return { title: `Обучение ${tutorial.step + 1}/${tutorial.total}: ${tutorial.title}`, hint: tutorial.hint };
  }
  if (!missions.active) return null;
  return { title: activeLine(missions.active, names), hint: activeHint(missions.active, here, docked, names) };
}
