import { describe, expect, it } from 'vitest';
import { endText } from './text';

describe('mech battle results', () => {
  it('explains free training retries without claiming a story reward was paid', () => {
    const won = endText(true, 0, false, '0 кр', true);
    const lost = endText(false, 0, false, '0 кр', true);
    expect(won).toContain('Тренировка пройдена');
    expect(won).toContain('бесплатно');
    expect(won).not.toContain('выплачена');
    expect(lost).toContain('Прототип восстановлен');
    expect(lost).toContain('бесплатно');
  });

  it('keeps the story reward for the first campaign victory', () => {
    expect(endText(true, 1500, true, '1 500 кр')).toContain('Награда: 1 500 кр');
    expect(endText(true, 0, false, '0 кр')).toContain('уже выплачена');
  });
});
