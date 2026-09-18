import { describe, expect, it } from 'vitest';
import { describeNotice } from './feed';

describe('describeNotice', () => {
  it('translates the codes the server sends', () => {
    expect(describeNotice('cargoFull')).toBe('Недостаточно места в трюме');
    expect(describeNotice('unloaded')).toBe('Груз продан');
    expect(describeNotice('tooFar')).toBe('Слишком далеко');
    expect(describeNotice('noCredits')).toBe('Не хватает кредитов');
  });

  it('ignores an unknown code instead of showing it raw', () => {
    // Сервер новее клиента: лучше промолчать, чем написать в ленту «somethingNew».
    expect(describeNotice('somethingNew')).toBeNull();
    expect(describeNotice('')).toBeNull();
  });
});
