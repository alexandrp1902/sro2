import { describe, expect, it } from 'vitest';
import { dialogCaption, monogram, portraitUrl } from './dialog';

/** Файлы ищутся так же, как их потом найдёт сборщик (см. audio/manifest.test.ts). */
const portraitFiles = new Set(Object.keys(import.meta.glob('../../public/portraits/*.webp')).map((p) => p.replace('../../public/', '')));

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

  it('shows the drawn portrait of a known speaker, and a monogram for everyone else', () => {
    // Пачка M: Ева, Холт и Дан говорят в «Тихой войне» — у каждого есть файл в public.
    for (const who of ['Ева Морен', 'Капитан Холт', 'Дан']) {
      const url = portraitUrl(who);
      expect(url).toMatch(/^portraits\/\w+\.webp$/);
      expect(portraitFiles.has(url!), who).toBe(true);
    }
    expect(portraitUrl('Интендант Коста')).toBeNull();
    expect(portraitUrl('Звено «Клык»')).toBeNull();
  });

  it('joins the role and the campaign without empty separators', () => {
    expect(dialogCaption('инженер рудника', 'Тихая война')).toBe('инженер рудника · Тихая война');
    // Журнал открывается без кампании в подписи: там название уже стоит заголовком.
    expect(dialogCaption('', 'Тихая война')).toBe('Тихая война');
    expect(dialogCaption('инженер рудника', '')).toBe('инженер рудника');
    expect(dialogCaption('', '')).toBe('');
  });
});
