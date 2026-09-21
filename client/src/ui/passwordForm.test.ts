import { describe, expect, it } from 'vitest';
import { passwordProblem } from './passwordForm';

describe('passwordProblem', () => {
  it('пропускает годную смену', () => {
    expect(passwordProblem('старый', 'новыйпароль', 'новыйпароль')).toBeNull();
  });

  it('требует текущий пароль: без него сервер всё равно откажет', () => {
    expect(passwordProblem('', 'новыйпароль', 'новыйпароль')).toBe('Введите текущий пароль');
  });

  it('держит те же границы длины, что сервер', () => {
    expect(passwordProblem('старый', 'abc', 'abc')).toBe('Новый пароль — от 4 до 64 символов');
    expect(passwordProblem('старый', 'abcd', 'abcd')).toBeNull();
    const long = 'a'.repeat(65);
    expect(passwordProblem('старый', long, long)).toBe('Новый пароль — от 4 до 64 символов');
  });

  it('ловит опечатку в повторе', () => {
    expect(passwordProblem('старый', 'новыйпароль', 'новыйпарол')).toBe('Пароли не совпадают');
  });

  it('не даёт сменить пароль на тот же самый', () => {
    expect(passwordProblem('одинаковый', 'одинаковый', 'одинаковый')).toBe('Новый пароль совпадает со старым');
  });
});
