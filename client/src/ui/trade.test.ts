import { describe, expect, it } from 'vitest';
import type { TradeOfferDto } from '../net/protocol';
import type { CargoState } from './cargoHud';
import {
  clampCredits,
  clampGive,
  describeTradeEvent,
  holdAfter,
  holdLine,
  isTradeWarning,
  readyProblem,
  theirRows,
  tradeFits,
  tradeRows,
} from './trade';

/** Руда объёмная: на ней видно, что трюм считается нетто. */
const volume = (item: string): number => (item === 'ore' ? 4 : 1);

const cargo = (extra: Partial<CargoState> = {}): CargoState => ({
  used: 10,
  max: 20,
  items: { metal: 6, ore: 1 },
  credits: 500,
  reserved: 0,
  ...extra,
});

const offer = (extra: Partial<TradeOfferDto> = {}): TradeOfferDto => ({
  id: 1,
  name: 'Bob',
  credits: 0,
  items: {},
  ready: false,
  ...extra,
});

describe('trade hold', () => {
  it('counts the hold net: what leaves makes room for what comes', () => {
    const own = offer({ items: { ore: 1 } }); // −4
    const their = offer({ items: { ore: 1 } }); // +4
    expect(holdAfter(cargo(), own, their, volume)).toBe(10);
    expect(holdAfter(cargo(), own, null, volume)).toBe(6);
    expect(holdAfter(cargo(), null, their, volume)).toBe(14);
  });

  it('knows when it will not fit — and that giving first makes it fit', () => {
    const full = cargo({ used: 20 });
    expect(tradeFits(full, null, offer({ items: { metal: 1 } }), volume)).toBe(false);
    expect(tradeFits(full, offer({ items: { ore: 1 } }), offer({ items: { metal: 1 } }), volume)).toBe(true);
  });

  it('says what stops the deal, credits first', () => {
    expect(readyProblem(cargo(), null, null, volume)).toBeNull();
    expect(readyProblem(cargo(), offer({ credits: 900 }), null, volume)).toBe('Столько кредитов у вас нет');
    expect(readyProblem(cargo({ used: 20 }), null, offer({ items: { metal: 1 } }), volume)).toBe('В трюм это не влезет');
  });

  it('writes the hold line in whole units', () => {
    expect(holdLine(13.6, 20)).toBe('Трюм после обмена: 14 / 20');
  });
});

describe('trade offer', () => {
  it('never offers more than there is', () => {
    expect(clampGive(6, 9)).toBe(6);
    expect(clampGive(6, -2)).toBe(0);
    expect(clampCredits(500, 900)).toBe(500);
    expect(clampCredits(500, -5)).toBe(0);
  });

  it('lists the hold, and keeps a promise whose cargo is gone', () => {
    const rows = tradeRows(cargo({ items: { metal: 2 } }), offer({ items: { ore: 1 } }), (item) => item);
    expect(rows).toEqual([
      { item: 'metal', name: 'metal', have: 2, give: 0 },
      // Руду успели продать, а обещание осталось: строка не должна пропадать вместе с грузом.
      { item: 'ore', name: 'ore', have: 0, give: 1 },
    ]);
  });

  it('shows only what the other side put on the table', () => {
    expect(theirRows(offer({ items: { metal: 3 } }), (item) => item)).toEqual([
      { item: 'metal', name: 'metal', have: 3, give: 3 },
    ]);
    expect(theirRows(null, (item) => item)).toEqual([]);
  });
});

describe('trade texts', () => {
  it('describes every event', () => {
    expect(describeTradeEvent({ t: 'tradeEvent', code: 'opened', name: 'Bob' })).toBe('Обмен с Bob');
    expect(describeTradeEvent({ t: 'tradeEvent', code: 'done' })).toBe('Обмен состоялся');
    expect(describeTradeEvent({ t: 'tradeEvent', code: 'tooFar' })).toBe('Обмен сорван: слишком далеко друг от друга');
    expect(describeTradeEvent({ t: 'tradeEvent', code: 'busy', name: 'Bob' })).toBe('Bob уже меняется с кем-то');
  });

  it('tells a refusal from good news', () => {
    expect(isTradeWarning('done')).toBe(false);
    expect(isTradeWarning('opened')).toBe(false);
    expect(isTradeWarning('noRoom')).toBe(true);
    expect(isTradeWarning('stale')).toBe(true);
  });
});
