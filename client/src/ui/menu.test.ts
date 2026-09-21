import { describe, expect, it } from 'vitest';
import { menuItems } from './menu';
import { logoutLines } from './confirm';

describe('menuItems', () => {
  it('на ПК есть и управление, и выход', () => {
    expect(menuItems(false)).toEqual([
      { id: 'controls', label: 'Управление' },
      { id: 'logout', label: 'Выход' },
    ]);
  });

  it('на телефоне управления нет, а выход есть', () => {
    // M10.5: переназначать нечего — кнопки на экране. Но выйти надо уметь отовсюду.
    expect(menuItems(true)).toEqual([{ id: 'logout', label: 'Выход' }]);
  });

  it('смена пароля — только вошедшему по нику и паролю', () => {
    // M15.7: гость играет без аккаунта, менять ему нечего.
    expect(menuItems(true, true)).toEqual([
      { id: 'password', label: 'Сменить пароль' },
      { id: 'logout', label: 'Выход' },
    ]);
    expect(menuItems(false, true).map((i) => i.id)).toEqual(['controls', 'password', 'logout']);
    expect(menuItems(false, false).map((i) => i.id)).toEqual(['controls', 'logout']);
  });
});

describe('logoutLines', () => {
  it('в полёте предупреждает, что корабль останется в космосе', () => {
    const lines = logoutLines(false);
    expect(lines.title).toBe('Выйти из игры?');
    expect(lines.text).toBe('Корабль останется в космосе, пока сервер его не уберёт, — около минуты. Его могут сбить.');
    expect(lines.yes).toBe('Выйти');
    expect(lines.no).toBe('Отмена');
  });

  it('в доке корабль просто остаётся в доке', () => {
    expect(logoutLines(true).text).toBe('Корабль останется в доке.');
  });
});
