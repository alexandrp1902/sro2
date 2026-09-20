import { describe, expect, it } from 'vitest';
import type { DemandMsg } from '../net/protocol';
import { DemandBoard } from './demand';
import { demandLine } from './dockScreen';

const NAMES: Record<string, string> = { medicine: 'Медикаменты', arms: 'Оружие' };
const name = (good: string) => NAMES[good] ?? good;

const msg = (state: DemandMsg['state'], extra: Partial<DemandMsg> = {}): DemandMsg => ({
  t: 'demand',
  state,
  system: 'edge',
  systemName: 'Край',
  place: 'pl:edgeAsh',
  placeName: 'Пепельный Приют',
  case: 'revolt',
  title: 'Восстание',
  goods: ['arms', 'medicine'],
  secondsLeft: 90,
  left: 180,
  quota: 180,
  mul: 4.5,
  ...extra,
});

describe('DemandBoard', () => {
  it('объявление называет место, товары и срок — и только один раз', () => {
    const board = new DemandBoard();
    const first = board.apply(msg('announce'), 0, 'sol', name);
    expect(first?.text).toBe('ВОССТАНИЕ: Пепельный Приют (Край) нужны Оружие и Медикаменты — 180 ед., приём через 01:30');
    expect(first?.alert).toBe(true);
    // Отсчёт идёт раз в секунду: каждую секунду в ленту писать нечего.
    expect(board.apply(msg('announce', { secondsLeft: 89 }), 1000, 'sol', name)).toBeNull();
  });

  it('в своей системе говорит «здесь»', () => {
    const board = new DemandBoard();
    expect(board.apply(msg('announce'), 0, 'edge', name)?.text).toContain('здесь');
  });

  it('открытие приёмки — отдельная строка, и она тревожная, только если это твоя система', () => {
    const board = new DemandBoard();
    board.apply(msg('announce'), 0, 'edge', name);
    const open = board.apply(msg('open'), 1000, 'edge', name);
    expect(open?.text).toBe('Восстание: приём открыт — здесь берут Оружие и Медикаменты втридорога');
    expect(open?.alert).toBe(true);
  });

  it('квота выбрана и срок вышел читаются по-разному', () => {
    const board = new DemandBoard();
    expect(board.apply(msg('filled', { left: 0 }), 0, 'sol', name)?.text).toBe(
      'Восстание: спрос закрыт, Пепельный Приют обеспечен',
    );
    expect(board.apply(msg('over', { left: 42 }), 0, 'sol', name)?.text).toBe(
      'Восстание: срок вышел, Пепельный Приют помощи не дождался',
    );
  });

  it('табло показывает множитель и остаток квоты', () => {
    const board = new DemandBoard();
    board.apply(msg('open', { left: 74, mul: 3.14, secondsLeft: 600 }), 0, 'edge', name);
    const lines = board.lines(0, name);
    expect(lines?.title).toBe('ВОССТАНИЕ · Пепельный Приют');
    expect(lines?.hint).toBe('Оружие и Медикаменты по ×3.1 · осталось 74 из 180 · 10:00');
  });

  it('без новостей событие забывается: связь рвалась или сервер перезапустили', () => {
    const board = new DemandBoard();
    board.apply(msg('open'), 0, 'edge', name);
    expect(board.system(1000)).toBe('edge');
    expect(board.system(60000)).toBeNull();
  });

  it('метки на карте нет, пока событие не объявлено', () => {
    expect(new DemandBoard().system(0)).toBeNull();
  });
});

describe('demandLine', () => {
  it('строка над рынком в доке называет товары, множитель и остаток', () => {
    expect(demandLine({ case: 'revolt', title: 'Восстание', goods: ['arms', 'medicine'], mul: 3.14, left: 74, quota: 180 }, name)).toBe(
      'Восстание: берут Оружие и Медикаменты по ×3.1 — осталось 74 из 180',
    );
  });

  it('события нет — строки нет', () => {
    expect(demandLine(null, name)).toBeNull();
    expect(demandLine(undefined, name)).toBeNull();
  });
});
