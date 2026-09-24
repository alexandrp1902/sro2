import { describe, expect, it } from 'vitest';
import { dialogCaption, monogram } from './dialog';

describe('M20a dialog card', () => {
  it('builds a monogram out of the name', () => {
    expect(monogram('Ева Морен')).toBe('ЕМ');
    expect(monogram('Капитан Холт')).toBe('КХ');
    // Одно слово — две его первые буквы; кавычки и скобки не в счёт.
    expect(monogram('Перехват')).toBe('ПЕ');
    expect(monogram('Звено «Клык»')).toBe('ЗК');
    expect(monogram('«Клык»')).toBe('КЛ');
    expect(monogram('   ')).toBe('?');
  });

  it('joins the role and the campaign without empty separators', () => {
    expect(dialogCaption('инженер рудника', 'Тихая война')).toBe('инженер рудника · Тихая война');
    // Журнал открывается без кампании в подписи: там название уже стоит заголовком.
    expect(dialogCaption('', 'Тихая война')).toBe('Тихая война');
    expect(dialogCaption('инженер рудника', '')).toBe('инженер рудника');
    expect(dialogCaption('', '')).toBe('');
  });
});
