import type { MissionOffer, MissionsMsg } from '../net/protocol';
import { nearestStation, nextHop, type GalaxyDto } from './galaxy';

/** Имена для текста заданий: сервер шлёт id системы, типа пирата и предмета. */
export interface MissionNames {
  system(id: string): string;
  npc(type: string): string;
  item(id: string): string;
}

type Active = NonNullable<MissionsMsg['active']>;
type Done = NonNullable<MissionsMsg['done']>;

/** Строка задания на доске станции: «Уничтожить: пираты ×4 · Vega». */
export function offerTitle(offer: MissionOffer, names: MissionNames): string {
  switch (offer.kind) {
    case 'kill':
      return `Уничтожить: ${offer.npc ? names.npc(offer.npc) : 'пираты'} ×${offer.count} · ${names.system(offer.system ?? '')}`;
    case 'collect':
      return `Собрать: ${names.item(offer.item ?? '')} ×${offer.count}`;
    case 'deliver':
      return `Доставить груз в ${names.system(offer.system ?? '')} · ${offer.count} ед.`;
  }
}

/** Подробность под строкой на доске: где и как сдаётся. */
export function offerNote(offer: MissionOffer): string {
  switch (offer.kind) {
    case 'kill':
      return 'награда — сразу за последнего';
    case 'collect':
      return 'сдать на любой станции';
    case 'deliver':
      return `груз займёт ${offer.count} ед. трюма`;
  }
}

/** Главная строка трекера: «Пираты в Vega: 2/4». */
export function activeLine(active: Active, names: MissionNames): string {
  const { offer, progress } = active;
  switch (offer.kind) {
    case 'kill':
      return `${offer.npc ? names.npc(offer.npc) : 'Пираты'} в ${names.system(offer.system ?? '')}: ${progress}/${offer.count}`;
    case 'collect':
      return `${names.item(offer.item ?? '')}: ${progress}/${offer.count}`;
    case 'deliver':
      return `Груз в ${names.system(offer.system ?? '')} · ${offer.count} ед.`;
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
      return here === offer.system ? 'пристыкуйтесь к станции' : `летите в ${names.system(offer.system ?? '')}`;
  }
}

/** Строка в ленту о сделанном: «✓ Уничтожьте учебный дрон · +100 кр». */
export function doneLines(done: Done): string[] {
  const reward = done.reward > 0 ? ` · +${done.reward} кр` : '';
  if (done.kind === 'mission') return [`✓ Задание выполнено${reward}`];
  const lines = [`✓ ${done.title ?? 'Шаг обучения'}${reward}`];
  if (done.last) lines.push('Обучение пройдено! Задания — на станции');
  return lines;
}

/**
 * На что указывает маркер цели. Врата — следующего прыжка по пути (to = null — любые, ближайшие).
 * null — показывать нечего: в доке, задание «добыть» ещё не собрано, заданий нет.
 */
export type Objective =
  | { kind: 'drone' }
  | { kind: 'loot' }
  | { kind: 'station' }
  | { kind: 'pirate'; npc: string | null }
  | { kind: 'gate'; to: string | null };

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
      return here === offer.system ? { kind: 'station' } : gateTo(offer.system ?? '');
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
