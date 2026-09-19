import { describe, expect, it } from 'vitest';
import type { InvasionMsg } from '../net/protocol';
import { InvasionBoard } from './invasion';
import { clock } from '../util/clock';

const msg = (state: InvasionMsg['state'], extra: Partial<InvasionMsg> = {}): InvasionMsg => ({
  t: 'invasion',
  state,
  system: 'vega',
  systemName: 'Vega',
  secondsLeft: 120,
  wave: 0,
  waves: 3,
  remaining: 0,
  nextIn: 0,
  x: 0,
  y: 0,
  reward: 0,
  damage: 0,
  ...extra,
});

describe('clock', () => {
  it('counts down as mm:ss', () => {
    expect(clock(0)).toBe('00:00');
    expect(clock(8.2)).toBe('00:09');
    expect(clock(102)).toBe('01:42');
  });
});

describe('InvasionBoard', () => {
  it('announces once, then counts down on its own between messages', () => {
    const board = new InvasionBoard();
    expect(board.apply(msg('announce'), 0, 'sol')).toEqual({ text: 'ВТОРЖЕНИЕ ПИРАТОВ в системе Vega через 02:00!', alert: true });
    expect(board.apply(msg('announce', { secondsLeft: 119 }), 1000, 'sol')).toBeNull();
    expect(board.lines(1500, 'Me')).toMatchObject({ title: 'ВТОРЖЕНИЕ ПИРАТОВ · Vega', hint: 'До начала 01:59', alert: true });
    expect(board.system(1500)).toBe('vega');
    expect(board.point('vega')).toBeNull();
  });

  it('tells about waves and points to the gathering spot in my system', () => {
    const board = new InvasionBoard();
    board.apply(msg('announce'), 0, 'vega');
    expect(board.apply(msg('wave', { wave: 1, remaining: 3, secondsLeft: 300, x: 100, y: -200 }), 0, 'vega')).toEqual({
      text: 'Вторжение началось в этой системе: волна 1 из 3',
      alert: true,
    });
    expect(board.point('vega')).toEqual({ x: 100, y: -200 });
    expect(board.point('sol')).toBeNull();
    expect(board.lines(0, 'Me')?.hint).toBe('Волна 1/3 · пиратов 3 · 05:00');
    expect(board.apply(msg('wave', { wave: 1, remaining: 0, nextIn: 20, secondsLeft: 250, x: 100, y: -200 }), 0, 'vega')).toEqual({
      text: 'Волна 1 отбита',
      alert: false,
    });
    expect(board.lines(0, 'Me')?.hint).toBe('Волна 1/3 отбита · следующая 00:20 · 04:10');
    expect(board.apply(msg('wave', { wave: 2, remaining: 4, secondsLeft: 230 }), 0, 'vega')).toEqual({ text: 'Волна 2 из 3!', alert: true });
  });

  it('shows the result table with me in it, then fades', () => {
    const board = new InvasionBoard();
    const results = [1, 2, 3, 4, 5, 6, 7].map((i) => ({ name: `P${i}`, damage: 1000 - i * 100, reward: 500 - i * 50 }));
    const line = board.apply(msg('won', { results, reward: 150, damage: 300 }), 0, 'sol');
    expect(line?.text).toContain('Вторжение в системе Vega отбито! Ваша доля: +150');
    const lines = board.lines(0, 'P7');
    expect(lines?.hint).toContain('Отбито! Ваша доля 150');
    expect(lines?.results?.map((r) => r.place)).toEqual([1, 2, 3, 4, 5, 7]);
    expect(lines?.results?.at(-1)).toMatchObject({ name: 'P7', self: true });
    expect(board.system(0)).toBeNull();
    expect(board.lines(13000, 'P7')).toBeNull();
  });
});
