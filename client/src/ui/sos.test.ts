import { describe, expect, it } from 'vitest';
import type { SosMsg } from '../net/protocol';
import { SosBoard } from './sos';

function sos(state: SosMsg['state'], reward = 0, x = 100): SosMsg {
  return { t: 'sos', id: 7, name: 'Торговец', x, y: 50, state, reward };
}

describe('SosBoard', () => {
  it('announces a call once and then only tracks where the trader is', () => {
    const board = new SosBoard();
    expect(board.apply(sos('on'), 0)).toBe('SOS! Торговец атакован — помогите, он заплатит');
    expect(board.apply(sos('on', 0, 300), 1000)).toBeNull();
    expect(board.active(1000, () => null)).toEqual([{ x: 300, y: 50 }]);
  });

  it('prefers the live radar position', () => {
    const board = new SosBoard();
    board.apply(sos('on'), 0);
    expect(board.active(0, () => ({ x: 1, y: 2 }))).toEqual([{ x: 1, y: 2 }]);
  });

  it('tells the helper what they were paid', () => {
    const board = new SosBoard();
    board.apply(sos('on'), 0);
    expect(board.apply(sos('saved', 150), 10)).toMatch(/^Торговец спасён и благодарит вас: \+150/);
    expect(board.active(10, () => null)).toEqual([]);
  });

  it('ends without a reward for bystanders and on death', () => {
    const board = new SosBoard();
    board.apply(sos('on'), 0);
    expect(board.apply(sos('saved'), 10)).toBe('Торговец отбился — SOS снят');
    board.apply(sos('on'), 20);
    expect(board.apply(sos('lost'), 30)).toBe('Торговец погиб — SOS снят');
  });

  it('forgets a call the server stopped repeating', () => {
    const board = new SosBoard();
    board.apply(sos('on'), 0);
    expect(board.active(5000, () => null)).toEqual([]);
    // Снова зовёт — это новый SOS, о нём снова пишут.
    expect(board.apply(sos('on'), 5000)).not.toBeNull();
  });
});
