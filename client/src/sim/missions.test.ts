import { describe, expect, it } from 'vitest';
import type { GalaxyDto, MissionOffer, MissionsMsg } from '../net/protocol';
import { startTab } from '../ui/dockScreen';
import { nearestStation, nextHop } from './galaxy';
import {
  activeHint,
  activeLine,
  destination,
  doneLines,
  objective,
  objectiveSystem,
  offerNote,
  offerTitle,
  timeLeft,
  trackerLines,
  type MissionNames,
} from './missions';

const galaxy: GalaxyDto = {
  systems: [
    { id: 'sol', name: 'Sol', danger: 1, pvp: 'off', station: true, x: 20, y: 55 },
    { id: 'vega', name: 'Vega', danger: 2, pvp: 'border', station: true, x: 50, y: 25 },
    { id: 'tau', name: 'Tau', danger: 3, pvp: 'border', station: false, x: 50, y: 75 },
    { id: 'nova', name: 'Nova', danger: 4, pvp: 'free', station: true, x: 80, y: 50 },
    { id: 'sigma', name: 'Sigma', danger: 5, pvp: 'free', station: false, x: 92, y: 82 },
  ],
  links: [
    { a: 'sol', b: 'vega' },
    { a: 'sol', b: 'tau' },
    { a: 'vega', b: 'nova' },
    { a: 'tau', b: 'nova' },
    { a: 'nova', b: 'sigma' },
  ],
};

const names: MissionNames = {
  system: (id) => galaxy.systems.find((s) => s.id === id)?.name ?? id,
  npc: (type) => (type === 'heavyPirate' ? 'Тяжёлый пират' : 'Пират'),
  item: (id) => (id === 'titanium' ? 'Титан' : id),
  place: (key) =>
    key === 'pl:terra'
      ? 'Новый Порт'
      : `Станция ${galaxy.systems.find((s) => s.id === key.slice(3))?.name ?? key.slice(3)}`,
};

const offer = (o: Partial<MissionOffer> & Pick<MissionOffer, 'kind'>): MissionOffer => ({
  id: '1-0',
  count: 4,
  reward: 300,
  from: 'st:sol',
  ...o,
});

const withActive = (
  active: MissionsMsg['active'],
  tutorial: MissionsMsg['tutorial'] = null,
  mark: MissionsMsg['mark'] = null,
): MissionsMsg => ({
  t: 'missions',
  tutorial,
  active,
  offers: [],
  mark,
});

describe('route helpers', () => {
  it('finds the first jump of the shortest route', () => {
    expect(nextHop(galaxy, 'sol', 'nova')).toBe('vega'); // через Vega и через Tau — по два прыжка, Vega раньше в списке
    expect(nextHop(galaxy, 'sol', 'sigma')).toBe('vega');
    expect(nextHop(galaxy, 'sigma', 'sol')).toBe('nova');
    expect(nextHop(galaxy, 'sol', 'sol')).toBeNull();
    expect(nextHop(galaxy, 'sol', 'nowhere')).toBeNull();
  });

  it('finds the nearest station', () => {
    expect(nearestStation(galaxy, 'vega')).toBe('vega');
    expect(nearestStation(galaxy, 'sigma')).toBe('nova');
    expect(nearestStation(galaxy, 'tau')).toBe('sol');
  });
});

describe('mission text', () => {
  it('names offers on the board', () => {
    expect(offerTitle(offer({ kind: 'kill', system: 'vega' }), names)).toBe('Уничтожить: пираты ×4 · Vega');
    expect(offerTitle(offer({ kind: 'kill', system: 'nova', npc: 'heavyPirate', count: 2 }), names)).toBe(
      'Уничтожить: Тяжёлый пират ×2 · Nova',
    );
    expect(offerTitle(offer({ kind: 'collect', item: 'titanium' }), names)).toBe('Собрать: Титан ×4');
    expect(offerTitle(offer({ kind: 'deliver', system: 'nova', place: 'st:nova', count: 6 }), names)).toBe(
      'Доставить груз: Станция Nova · 6 ед.',
    );
    // Адрес — место, а не система: на планете той же системы своя доска и своя репутация (M15).
    expect(offerTitle(offer({ kind: 'deliver', system: 'sol', place: 'pl:terra', count: 6 }), names)).toBe(
      'Доставить груз: Новый Порт · 6 ед.',
    );
  });

  it('tracks progress and says what to do next', () => {
    const kill = { offer: offer({ kind: 'kill', system: 'vega' }), progress: 2 };
    expect(activeLine(kill, names)).toBe('Пираты в Vega: 2/4');
    expect(activeHint(kill, 'sol', false, names)).toBe('летите в Vega');
    expect(activeHint(kill, 'vega', false, names)).toBe('уничтожайте их здесь');

    const collect = { offer: offer({ kind: 'collect', item: 'titanium' }), progress: 4 };
    expect(activeLine(collect, names)).toBe('Титан: 4/4');
    expect(activeHint(collect, 'tau', false, names)).toBe('сдайте на любой станции');
    expect(activeHint({ ...collect, progress: 1 }, 'tau', false, names)).toBe('добудьте в космосе');
  });

  it('puts the tutorial ahead of a mission in the tracker', () => {
    const tutorial = { step: 1, total: 5, id: 'drone' as const, title: 'Уничтожьте учебный дрон', hint: 'огонь' };
    const both = withActive({ offer: offer({ kind: 'kill', system: 'vega' }), progress: 0 }, tutorial);
    expect(trackerLines(both, 'sol', false, names)).toEqual({ title: 'Обучение 2/5: Уничтожьте учебный дрон', hint: 'огонь' });
    expect(trackerLines(withActive(null), 'sol', false, names)).toBeNull();
  });

  it('writes what was done to the feed', () => {
    expect(doneLines({ kind: 'tutorial', reward: 100, title: 'Дрон' })).toEqual(['✓ Дрон · +100 кр']);
    expect(doneLines({ kind: 'tutorial', reward: 0, title: 'Вылет' })).toEqual(['✓ Вылет']);
    expect(doneLines({ kind: 'tutorial', reward: 250, title: 'Прыжок', last: true })).toEqual([
      '✓ Прыжок · +250 кр',
      'Обучение пройдено! Задания — на станции',
    ]);
    expect(doneLines({ kind: 'mission', reward: 400 })).toEqual(['✓ Задание выполнено · +400 кр']);
  });

  it('says why a mission was failed', () => {
    expect(doneLines({ kind: 'failed', reward: 0, reason: 'trader' })).toEqual(['✗ Задание провалено: конвой погиб']);
    expect(doneLines({ kind: 'failed', reward: 0, reason: 'time' })).toEqual(['✗ Задание провалено: срок вышел']);
    // Сервер новее клиента: причину не знаем, но сказать о провале всё равно надо.
    expect(doneLines({ kind: 'failed', reward: 0, reason: 'meteor' })).toEqual(['✗ Задание провалено: работа сорвана']);
  });
});

describe('M14 missions', () => {
  const now = 1_000_000;
  const at = (secondsLeft: number) => now / 1000 + secondsLeft;

  it('counts the deadline down and drops it when it runs out', () => {
    expect(timeLeft(at(125), now)).toBe('2:05');
    expect(timeLeft(at(9), now)).toBe('0:09');
    expect(timeLeft(at(-1), now)).toBeNull();
    expect(timeLeft(0, now)).toBeNull();
    expect(timeLeft(undefined, now)).toBeNull();
  });

  it('names every new kind on the board', () => {
    expect(offerTitle(offer({ kind: 'escort', system: 'vega', count: 2 }), names)).toBe(
      'Сопровождение: конвой к вратам на Vega',
    );
    expect(offerTitle(offer({ kind: 'patrol', system: 'sol', npc: 'ranger', count: 4 }), names)).toBe(
      'Патруль с рейнджерами: 4 точки маршрута',
    );
    expect(offerTitle(offer({ kind: 'courier', system: 'nova', place: 'st:nova', count: 1 }), names)).toBe(
      'Важное письмо: Станция Nova',
    );
    expect(offerTitle(offer({ kind: 'hunt', system: 'vega', count: 8 }), names)).toBe('Охота: метеориты ×8 · Vega');
    expect(offerTitle(offer({ kind: 'hunt', system: 'vega', count: 3, size: 'large' }), names)).toBe(
      'Охота: крупные метеориты ×3 · Vega',
    );
    expect(offerNote(offer({ kind: 'courier', seconds: 300 }), names)).toContain('срок 5 мин');
    expect(offerNote(offer({ kind: 'escort', count: 2 }), names)).toContain('засад: 2');
  });

  it('tracks progress and says what to do next', () => {
    const escort = withActive({ offer: offer({ kind: 'escort', system: 'vega', count: 2 }), progress: 1 });
    expect(activeLine(escort.active!, names)).toBe('Конвой к вратам на Vega: засады 1/2');
    expect(activeHint(escort.active!, 'sol', false, names)).toBe('держитесь рядом с конвоем');
    expect(activeHint(escort.active!, 'sol', true, names)).toBe('вылетайте: конвой ждёт');

    const patrol = withActive({ offer: offer({ kind: 'patrol', system: 'sol', count: 4 }), progress: 2 });
    expect(activeLine(patrol.active!, names)).toBe('Патруль: точка 3/4');
    // На последней точке счётчик не убегает за край.
    expect(activeLine({ offer: offer({ kind: 'patrol', count: 4 }), progress: 4 }, names)).toBe('Патруль: точка 4/4');

    const courier = withActive({ offer: offer({ kind: 'courier', system: 'nova', place: 'st:nova' }), progress: 0, until: at(310) });
    expect(activeLine(courier.active!, names, now)).toBe('Письмо в Nova · 5:10');
    expect(activeHint(courier.active!, 'nova', false, names)).toBe('пристыкуйтесь к станции');

    const hunt = withActive({ offer: offer({ kind: 'hunt', system: 'vega', count: 8, size: 'large' }), progress: 3 });
    expect(activeLine(hunt.active!, names)).toBe('Крупные метеориты в Vega: 3/8');
    expect(activeHint(hunt.active!, 'vega', false, names)).toBe('расстреливайте камни');
    expect(activeHint(hunt.active!, 'sol', false, names)).toBe('летите в Vega');
  });

  it('takes the marker of a live mission from the server', () => {
    const escort = (mark: MissionsMsg['mark']) =>
      withActive({ offer: offer({ kind: 'escort', system: 'vega', count: 2 }), progress: 0 }, null, mark);
    expect(objective(escort({ ship: 77, x: 0, y: 0 }), 'sol', galaxy, false)).toEqual({ kind: 'ship', id: 77 });
    expect(objective(escort(null), 'sol', galaxy, false)).toBeNull();

    const patrol = withActive({ offer: offer({ kind: 'patrol', system: 'sol', count: 4 }), progress: 1 }, null, {
      ship: 0,
      x: 500,
      y: -200,
    });
    expect(objective(patrol, 'sol', galaxy, false)).toEqual({ kind: 'point', x: 500, y: -200 });
    // Конвой и звено не выходят из системы — на карте галактики им указывать не на что.
    expect(objectiveSystem(escort({ ship: 77, x: 0, y: 0 }), 'sol', galaxy)).toBeNull();
    expect(objectiveSystem(patrol, 'sol', galaxy)).toBeNull();
  });

  it('routes the letter and the hunt like any other errand', () => {
    const courier = withActive({ offer: offer({ kind: 'courier', system: 'nova', place: 'st:nova' }), progress: 0 });
    expect(objective(courier, 'sol', galaxy, false)).toEqual({ kind: 'gate', to: 'vega' });
    expect(objective(courier, 'nova', galaxy, false)).toEqual({ kind: 'station' });
    expect(objectiveSystem(courier, 'sol', galaxy)).toBe('nova');

    const hunt = withActive({ offer: offer({ kind: 'hunt', system: 'vega', size: 'large' }), progress: 0 });
    expect(objective(hunt, 'vega', galaxy, false)).toEqual({ kind: 'meteor', size: 'large' });
    expect(objective(hunt, 'sol', galaxy, false)).toEqual({ kind: 'gate', to: 'vega' });
  });
});

describe('objective', () => {
  it('follows the tutorial step', () => {
    const step = (id: 'undock' | 'drone' | 'grab' | 'sell' | 'jump') =>
      objective(withActive(null, { step: 0, total: 5, id, title: '', hint: '' }), 'sol', galaxy, false);
    expect(step('undock')).toBeNull();
    expect(step('drone')).toEqual({ kind: 'drone' });
    expect(step('grab')).toEqual({ kind: 'loot' });
    expect(step('sell')).toEqual({ kind: 'station' });
    expect(step('jump')).toEqual({ kind: 'gate', to: null });
  });

  it('points at the next gate until the target system, then at the target', () => {
    const deliver = withActive({ offer: offer({ kind: 'deliver', system: 'nova', place: 'st:nova' }), progress: 0 });
    expect(objective(deliver, 'sol', galaxy, false)).toEqual({ kind: 'gate', to: 'vega' });
    expect(objective(deliver, 'nova', galaxy, false)).toEqual({ kind: 'station' });
    expect(objectiveSystem(deliver, 'sol', galaxy)).toBe('nova');
    expect(objectiveSystem(deliver, 'nova', galaxy)).toBeNull();

    const kill = withActive({ offer: offer({ kind: 'kill', system: 'vega', npc: 'heavyPirate' }), progress: 0 });
    expect(objective(kill, 'vega', galaxy, false)).toEqual({ kind: 'pirate', npc: 'heavyPirate' });
    expect(objective(kill, 'vega', galaxy, true)).toBeNull(); // в доке маркера нет
  });

  it('sends a finished collect mission to the nearest station', () => {
    const collect = (progress: number) => withActive({ offer: offer({ kind: 'collect', item: 'titanium' }), progress });
    expect(objective(collect(1), 'tau', galaxy, false)).toBeNull();
    expect(objective(collect(4), 'tau', galaxy, false)).toEqual({ kind: 'gate', to: 'sol' });
    expect(objective(collect(4), 'vega', galaxy, false)).toEqual({ kind: 'station' });
    expect(objectiveSystem(collect(4), 'sigma', galaxy)).toBe('nova');
  });
});

describe('startTab', () => {
  it('opens the dock on missions when something waits there', () => {
    const tutorial = { step: 0, total: 5, id: 'undock' as const, title: '', hint: '' };
    expect(startTab(withActive(null, tutorial), {})).toBe('missions');
    const collect = withActive({ offer: offer({ kind: 'collect', item: 'titanium' }), progress: 0 });
    expect(startTab(collect, { titanium: 3 })).toBe('cargo');
    expect(startTab(collect, { titanium: 4 })).toBe('missions');
    expect(startTab(null, {})).toBe('cargo');
  });
});

describe('M15 deliveries', () => {
  it('tells you to land when the address is a settlement', () => {
    const toPlanet = withActive({ offer: offer({ kind: 'deliver', system: 'sol', place: 'pl:terra' }), progress: 0 });
    const toStation = withActive({ offer: offer({ kind: 'deliver', system: 'sol', place: 'st:sol' }), progress: 0 });

    expect(activeHint(toPlanet.active!, 'sol', false, names)).toBe('садитесь в поселении');
    expect(activeHint(toStation.active!, 'sol', false, names)).toBe('пристыкуйтесь к станции');
    // Пока не долетели — разницы нет: сперва надо попасть в систему.
    expect(activeHint(toPlanet.active!, 'vega', false, names)).toBe('летите в Sol');
  });

  it('falls back to the employer when a mission has no address of its own', () => {
    expect(destination(offer({ kind: 'kill', system: 'vega' }))).toBe('st:sol');
    expect(destination(offer({ kind: 'deliver', system: 'nova', place: 'st:nova' }))).toBe('st:nova');
  });
});
