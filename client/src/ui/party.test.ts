import { describe, expect, it } from 'vitest';
import type { PartyMemberDto } from '../net/protocol';
import { PartyBoard, combinedBar, describeBounty, describePartyEvent, partyCompact, partyMarks, partyTitle } from './party';

const member = (id: number, name: string, extra: Partial<PartyMemberDto> = {}): PartyMemberDto => ({
  id,
  name,
  system: 'sol',
  systemName: 'Sol',
  x: 0,
  y: 0,
  hp: 100,
  maxHp: 400,
  sh: 50,
  maxSh: 150,
  online: true,
  dead: false,
  docked: false,
  ...extra,
});

describe('PartyBoard', () => {
  it('knows its members, not counting me', () => {
    const board = new PartyBoard();
    expect(board.isMember(2, 1)).toBe(false);
    board.apply({ t: 'partyState', leader: 1, members: [member(1, 'Me'), member(2, 'Bob')] });
    expect(board.size).toBe(2);
    expect(board.isMember(2, 1)).toBe(true);
    expect(board.isMember(1, 1)).toBe(false);
    expect(board.isMember(3, 1)).toBe(false);
    board.clear();
    expect(board.size).toBe(0);
    expect(board.ids.size).toBe(0);
  });

  it('lists me first, with distance here and the system elsewhere', () => {
    const board = new PartyBoard();
    board.apply({
      t: 'partyState',
      leader: 2,
      members: [
        member(2, 'Bob', { x: 700, y: 0 }),
        member(1, 'Me'),
        member(3, 'Carol', { system: 'vega', systemName: 'Vega', online: false }),
        member(4, 'Dan', { docked: true }),
      ],
    });
    const rows = board.rows({ id: 1, system: 'sol', x: 0, y: 0 }, 700);
    expect(rows.map((r) => r.name)).toEqual(['Me', 'Bob', 'Carol', 'Dan']);
    expect(rows[0]).toMatchObject({ self: true, where: '', leader: false });
    expect(rows[1]).toMatchObject({ leader: true, where: '1.0с', status: '' });
    expect(rows[2]).toMatchObject({ where: 'Vega', status: 'нет связи' });
    expect(rows[3]).toMatchObject({ where: 'Sol', status: 'в доке' });
  });

  it('numbers members by join order, not by the order of rows', () => {
    const board = new PartyBoard();
    board.apply({
      t: 'partyState',
      leader: 2,
      members: [member(2, 'Bob'), member(1, 'Me'), member(3, 'Carol')],
      maxSize: 10,
    });
    // Я в панели первый, но номер у меня второй: у товарищей номера не должны зависеть от того, кто смотрит.
    expect(board.rows({ id: 1, system: 'sol', x: 0, y: 0 }, 700).map((r) => [r.name, r.n])).toEqual([
      ['Me', 2],
      ['Bob', 1],
      ['Carol', 3],
    ]);
    expect(board.maxSize).toBe(10);
  });
});

describe('party panel', () => {
  it('counts the group against its limit', () => {
    expect(partyTitle(7, 10)).toBe('Группа · 7/10');
    expect(partyTitle(2, 0)).toBe('Группа');
  });

  it('goes compact past five rows', () => {
    expect(partyCompact(5)).toBe(false);
    expect(partyCompact(6)).toBe(true);
  });

  it('shows hull and shield as one bar when compact', () => {
    expect(combinedBar({ hp: 100, maxHp: 400, sh: 50, maxSh: 150 } as never)).toEqual({ value: 150, max: 550 });
  });
});

describe('partyMarks', () => {
  const me = { id: 1, system: 'sol', x: 0, y: 0 };
  const members = [
    member(1, 'Me'),
    member(2, 'Bob', { x: 700, y: 100 }),
    member(3, 'Carol', { system: 'vega', systemName: 'Vega' }),
    member(4, 'Dan', { docked: true }),
    member(5, 'Eve', { dead: true }),
    member(6, 'Fred', { x: -300, y: 400 }),
  ];

  it('keeps only the living, undocked members of my own system', () => {
    expect(partyMarks(members, me, () => null)).toEqual([
      { id: 2, n: 2, x: 700, y: 100 },
      { id: 6, n: 6, x: -300, y: 400 },
    ]);
  });

  it('prefers the live position of a ship on the radar', () => {
    const marks = partyMarks(members, me, (id) => (id === 2 ? { x: 10, y: 20 } : null));
    expect(marks[0]).toEqual({ id: 2, n: 2, x: 10, y: 20 });
    expect(marks[1]).toMatchObject({ id: 6, x: -300, y: 400 });
  });
});

describe('party texts', () => {
  it('describes events', () => {
    expect(describePartyEvent({ t: 'partyEvent', code: 'joined' })).toBe('Вы в группе');
    expect(describePartyEvent({ t: 'partyEvent', code: 'joined', name: 'Bob' })).toBe('Bob вступает в группу');
    expect(describePartyEvent({ t: 'partyEvent', code: 'full' })).toBe('В группе нет мест');
    expect(describePartyEvent({ t: 'partyEvent', code: 'busy', name: 'Bob' })).toBe('Bob уже в группе');
  });

  it('describes a bounty, split or not', () => {
    const credits = (n: number) => `${n.toLocaleString('ru-RU')} кр`;
    expect(describeBounty({ t: 'bounty', amount: 60, shared: 1, name: 'Пират Ур.1' })).toBe(`+${credits(60)} за Пират Ур.1`);
    expect(describeBounty({ t: 'bounty', amount: 90, shared: 2, name: 'Пират Ур.2' })).toBe(
      `+${credits(90)} за Пират Ур.2 (делёж на 2)`,
    );
  });
});
