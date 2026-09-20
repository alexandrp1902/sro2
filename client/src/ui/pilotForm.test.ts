import { describe, expect, it } from 'vitest';
import type { NameFreeMsg } from '../net/protocol';
import { careerCards, describeDenied } from './pilotForm';

const answer = (free: boolean): NameFreeMsg => ({
  t: 'nameFree',
  name: 'Новичок',
  free,
  career: 'ranger',
  careers: [
    { id: 'ranger', name: 'Рейнджер', hint: 'Боевой корабль', enabled: true },
    { id: 'trader', name: 'Торговец', hint: 'Грузовой трюм', enabled: true },
    { id: 'pirate', name: 'Пират', hint: 'Скоро', enabled: false },
  ],
});

describe('careerCards', () => {
  it('ник свободен — показываем все три карточки, рейнджер выбран', () => {
    const cards = careerCards(answer(true), null);
    expect(cards.map((c) => c.id)).toEqual(['ranger', 'trader', 'pirate']);
    expect(cards.find((c) => c.chosen)?.id).toBe('ranger');
  });

  it('ник занят — это вход в старый аккаунт, и выбирать нечего', () => {
    expect(careerCards(answer(false), null)).toEqual([]);
  });

  it('про ник ещё не спрашивали — карточек нет', () => {
    expect(careerCards(null, 'trader')).toEqual([]);
  });

  it('выбор игрока побеждает путь по умолчанию', () => {
    expect(careerCards(answer(true), 'trader').find((c) => c.chosen)?.id).toBe('trader');
  });

  it('запертый путь выбрать нельзя: остаётся тот, что по умолчанию', () => {
    // Серая кнопка — только подсказка; отказать всё равно должен сервер.
    expect(careerCards(answer(true), 'pirate').find((c) => c.chosen)?.id).toBe('ranger');
    expect(careerCards(answer(true), 'pirate').find((c) => c.id === 'pirate')?.enabled).toBe(false);
  });

  it('сервер без путей — ряда нет', () => {
    expect(careerCards({ ...answer(true), careers: [] }, null)).toEqual([]);
  });
});

describe('describeDenied', () => {
  it('закрытый путь объясняется словами', () => {
    expect(describeDenied('badCareer')).toBe('Этот путь пока закрыт');
  });
});
