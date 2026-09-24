import { describe, expect, it } from 'vitest';
import { menuItems } from './menu';
import { logoutLines } from './confirm';
import { tipLines } from './tips';

describe('menuItems', () => {
  it('на ПК есть звук, управление и выход', () => {
    expect(menuItems(false)).toEqual([
      { id: 'audio', label: 'Звук' },
      { id: 'controls', label: 'Управление' },
      { id: 'logout', label: 'Выход' },
    ]);
  });

  it('звук есть и на телефоне, и первым пунктом', () => {
    // M17: у вкладки браузера нет своего ползунка в телефоне — приглушить игру надо уметь отсюда.
    expect(menuItems(true)[0]).toEqual({ id: 'audio', label: 'Звук' });
    expect(menuItems(false)[0]).toEqual({ id: 'audio', label: 'Звук' });
  });

  it('на телефоне управления нет, зато есть отладка и выход', () => {
    // M10.5: переназначать нечего — кнопки на экране. Но выйти надо уметь отовсюду.
    // Строки полёта в мобильном HUD больше нет, и dev-панель открывается только отсюда.
    expect(menuItems(true)).toEqual([
      { id: 'audio', label: 'Звук' },
      { id: 'dev', label: 'Отладка' },
      { id: 'logout', label: 'Выход' },
    ]);
  });

  it('советы и смена пароля — только вошедшему по нику и паролю', () => {
    // M15.7: гость играет без аккаунта, менять ему нечего. M18: и обучения у него нет — советы после него тоже.
    expect(menuItems(true, true)).toEqual([
      { id: 'audio', label: 'Звук' },
      { id: 'tips', label: 'Советы' },
      { id: 'password', label: 'Сменить пароль' },
      { id: 'dev', label: 'Отладка' },
      { id: 'logout', label: 'Выход' },
    ]);
    expect(menuItems(false, true).map((i) => i.id)).toEqual(['audio', 'controls', 'tips', 'password', 'logout']);
    expect(menuItems(false, false).map((i) => i.id)).toEqual(['audio', 'controls', 'logout']);
  });

  it('журнал кампании появляется, только когда есть о чём рассказывать', () => {
    // M20a: пилот, который сюжета ещё не видел, не должен находить в меню пустой журнал.
    expect(menuItems(false, true, false).map((i) => i.id)).not.toContain('story');
    expect(menuItems(false, true, true).map((i) => i.id)).toEqual([
      'audio',
      'controls',
      'story',
      'tips',
      'password',
      'logout',
    ]);
    // Гостю кампания доступна так же: она просто не переживёт его выход, как и всё остальное.
    expect(menuItems(true, false, true).map((i) => i.id)).toEqual(['audio', 'story', 'dev', 'logout']);
  });
});

describe('tipLines', () => {
  it('три совета: доска, груз, карта — и про клавишу карты только на ПК', () => {
    expect(tipLines(false).map((t) => t.title)).toEqual(['Доска заданий', 'Груз и слухи', 'Карта и курс']);
    expect(tipLines(false)[2].text).toMatch(/^M или клик/);
    expect(tipLines(true)[2].text).toMatch(/^Тап/);
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
