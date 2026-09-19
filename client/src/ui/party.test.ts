import { describe, expect, it } from 'vitest';
import type { PartyMemberDto } from '../net/protocol';
import { PartyBoard, describeBounty, describePartyEvent } from './party';

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
