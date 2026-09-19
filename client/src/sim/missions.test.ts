import { describe, expect, it } from 'vitest';
import type { GalaxyDto, MissionOffer, MissionsMsg } from '../net/protocol';
import { startTab } from '../ui/dockScreen';
import { nearestStation, nextHop } from './galaxy';
import { activeHint, activeLine, doneLines, objective, objectiveSystem, offerTitle, trackerLines, type MissionNames } from './missions';

const galaxy: GalaxyDto = {
  systems: [
    { id: 'sol', name: 'Sol', danger: 1, pvp: 'off', station: true, x: 20, y: 55 },
    { id: 'vega', name: 'Vega', danger: 2, pvp: 'border', station: true, x: 50, y: 25 },
    { id: 'tau', name: 'Tau', danger: 3, pvp: 'border', station: false, x: 50, y: 75 },
    { id: 'nova', name: 'Nova', danger: 4, pvp: 'free', station: true, x: 80, y: 50 },
    { id: 'sigma', name: 'Sigma', danger: 5, pvp: 'free', station: false, x: 92, y: 82 },
  ],
  links: [
    { a: 'sol', b: 'vega', cost: 20 },
    { a: 'sol', b: 'tau', cost: 30 },
    { a: 'vega', b: 'nova', cost: 30 },
    { a: 'tau', b: 'nova', cost: 25 },
    { a: 'nova', b: 'sigma', cost: 25 },
  ],
};

const names: MissionNames = {
  system: (id) => galaxy.systems.find((s) => s.id === id)?.name ?? id,
  npc: (type) => (type === 'heavyPirate' ? 'Тяжёлый пират' : 'Пират'),
  item: (id) => (id === 'titanium' ? 'Титан' : id),
};

const offer = (o: Partial<MissionOffer> & Pick<MissionOffer, 'kind'>): MissionOffer => ({
  id: '1-0',
  count: 4,
  reward: 300,
  from: 'sol',
  ...o,
});

const withActive = (active: MissionsMsg['active'], tutorial: MissionsMsg['tutorial'] = null): MissionsMsg => ({
  t: 'missions',
  tutorial,
  active,
  offers: [],
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
    expect(offerTitle(offer({ kind: 'deliver', system: 'nova', count: 6 }), names)).toBe('Доставить груз в Nova · 6 ед.');
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
    const deliver = withActive({ offer: offer({ kind: 'deliver', system: 'nova' }), progress: 0 });
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
