import { describe, expect, it } from 'vitest';
import { isDoubleTap } from './tapSelect';

describe('isDoubleTap', () => {
  const first = { x: 100, y: 200, time: 1000 };

  it('is a second tap close by and soon after the first', () => {
    expect(isDoubleTap(first, { x: 110, y: 215, time: 1350 })).toBe(true);
  });

  it('is not a tap that comes too late, too far or first', () => {
    expect(isDoubleTap(first, { x: 100, y: 200, time: 1550 })).toBe(false);
    expect(isDoubleTap(first, { x: 160, y: 200, time: 1200 })).toBe(false);
    expect(isDoubleTap(null, first)).toBe(false);
  });
});
