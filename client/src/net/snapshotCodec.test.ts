import { describe, expect, it } from 'vitest';
import vectors from '../../../shared/test-vectors/snapshot.json';
import { SnapshotDecoder } from './snapshotCodec';

/** Кадры и ожидаемые снапшоты сгенерировал сервер (SnapshotCodecTests): декодер клиента обязан собрать то же самое. */
function bytes(base64: string): Uint8Array {
  return Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
}

function sorted<T extends { id: number }>(list: T[] | undefined): T[] {
  return [...(list ?? [])].sort((a, b) => a.id - b.id);
}

describe('SnapshotDecoder', () => {
  it('rebuilds the same snapshots as the server from the shared frames', () => {
    const decoder = new SnapshotDecoder();
    expect(vectors.frames.length).toBeGreaterThan(2);
    for (const { frame, expected } of vectors.frames) {
      const snapshot = decoder.decode(bytes(frame));
      expect(snapshot.tick).toBe(expected.tick);
      expect(sorted(snapshot.ships)).toEqual(expected.ships);
      expect(sorted(snapshot.loot)).toEqual(expected.loot);
      expect(sorted(snapshot.meteors)).toEqual(expected.meteors);
      expect(snapshot.shots ?? null).toEqual(expected.shots);
      expect(snapshot.kills ?? null).toEqual(expected.kills);
      expect(snapshot.picks ?? null).toEqual(expected.picks);
    }
    expect(decoder.desyncs).toBe(0);
  });

  it('counts a delta for an unknown entity as a desync', () => {
    const decoder = new SnapshotDecoder();
    // Второй кадр — дельта: без первого (ключевого) декодеру не с чем её сложить.
    decoder.decode(bytes(vectors.frames[1].frame));
    expect(decoder.desyncs).toBeGreaterThan(0);
  });

  it('starts over on a keyframe', () => {
    const decoder = new SnapshotDecoder();
    for (const { frame } of vectors.frames) decoder.decode(bytes(frame));
    const last = vectors.frames[vectors.frames.length - 1];
    const again = decoder.decode(bytes(last.frame));
    expect(sorted(again.ships)).toEqual(last.expected.ships);
  });

  it('rejects something that is not a snapshot frame', () => {
    expect(() => new SnapshotDecoder().decode(new Uint8Array([0x91, 0x02]))).toThrow();
  });
});
