import { describe, expect, it } from 'vitest';
import type { NameFreeMsg } from '../net/protocol';
import { careerCards, describeDenied, formProblem, nameNote, startTab } from './pilotForm';

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

describe('startTab', () => {
  it('первый заход — сразу «Регистрация»: входить такому игроку некуда', () => {
    expect(startTab({ url: 'sro.example.com', name: '', loggedIn: false })).toBe('register');
  });

  it('ник помним с прошлого раза — «Вход»', () => {
    expect(startTab({ url: 'sro.example.com', name: 'Новичок', loggedIn: false })).toBe('login');
  });

  it('окно поверх идущей игры — «Вход», даже если ник ещё не знаем', () => {
    expect(startTab({ url: 'sro.example.com', name: '', loggedIn: true })).toBe('login');
  });
});

describe('formProblem', () => {
  const full = { name: 'Новичок', password: 'secret', confirm: 'secret' };

  it('пустые поля называются по одному', () => {
    expect(formProblem('login', { ...full, name: '' })).toBe('Введите логин');
    expect(formProblem('login', { ...full, password: '' })).toBe('Введите пароль');
  });

  it('на вкладке «Вход» повтор пароля не спрашивают', () => {
    expect(formProblem('login', { ...full, confirm: '' })).toBeNull();
  });

  it('при регистрации пароль повторяют, и повтор должен совпасть', () => {
    expect(formProblem('register', { ...full, confirm: '' })).toBe('Повторите пароль');
    expect(formProblem('register', { ...full, confirm: 'secrat' })).toBe('Пароли не совпадают');
  });

  it('всё на месте — отправляем', () => {
    expect(formProblem('register', full)).toBeNull();
    expect(formProblem('login', full)).toBeNull();
  });
});

describe('nameNote', () => {
  it('вкладка «Вход», а пилота с таким ником нет — предупреждаем', () => {
    expect(nameNote('login', answer(true))).toContain('Такого пилота нет');
  });

  it('вкладка «Регистрация», а ник занят — тоже', () => {
    expect(nameNote('register', answer(false))).toContain('уже занят');
  });

  it('ник отвечает вкладке — молчим', () => {
    expect(nameNote('login', answer(false))).toBe('');
    expect(nameNote('register', answer(true))).toBe('');
  });

  it('про ник ещё не спрашивали — молчим', () => {
    expect(nameNote('register', null)).toBe('');
  });
});

describe('describeDenied', () => {
  it('закрытый путь объясняется словами', () => {
    expect(describeDenied('badCareer')).toBe('Этот путь пока закрыт');
  });

  it('отказы вкладок отправляют на другую вкладку', () => {
    expect(describeDenied('noAccount')).toContain('«Регистрация»');
    expect(describeDenied('nameTaken')).toContain('«Вход»');
  });
});
