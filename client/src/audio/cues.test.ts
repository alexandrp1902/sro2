import { describe, expect, it } from 'vitest';
import weaponsJson from '../../../shared/weapons.json';
import type { WeaponParams } from '../sim/combat';
import {
  GUARD_WEAPON,
  RAM_WEAPON,
  SPLASH_WEAPON,
  cueGain,
  flightMs,
  hasCue,
  impactVoice,
  killVoice,
  launchVoice,
  shotVoice,
} from './cues';
import type { ShotDto } from '../net/protocol';

const catalog = weaponsJson as unknown as Record<string, WeaponParams>;
const shot = (over: Partial<ShotDto> = {}): ShotDto => ({ from: 1, to: 2, w: 'pulse', hit: true, dmg: 100, sh: 0, ch: 70, ...over });

describe('звук выстрела', () => {
  it('у каждой пушки каталога есть свой звук', () => {
    for (const [id, weapon] of Object.entries(catalog)) {
      const voice = shotVoice(id, weapon, false);
      expect(hasCue(voice.cue), `${id} → ${voice.cue}`).toBe(true);
    }
  });

  it('вид звука идёт от kind, а не от имени пушки', () => {
    expect(shotVoice('pulse', catalog.pulse, false).cue).toBe('shot-bolt');
    expect(shotVoice('laser', catalog.laser, false).cue).toBe('shot-beam');
    expect(shotVoice('plasma', catalog.plasma, false).cue).toBe('shot-orb');
    expect(shotVoice('railgun', catalog.railgun, false).cue).toBe('shot-rail');
    expect(shotVoice('ion', catalog.ion, false).cue).toBe('shot-ion');
  });

  it('калибр слышен: мелкий ствол выше крупного', () => {
    const small = shotVoice('a', { ...catalog.pulse, class: 'S' }, false).rate;
    const medium = shotVoice('a', { ...catalog.pulse, class: 'M' }, false).rate;
    const large = shotVoice('a', { ...catalog.pulse, class: 'L' }, false).rate;
    expect(small).toBeGreaterThan(medium);
    expect(medium).toBeGreaterThan(large);
  });

  it('незнакомая пушка звучит как импульсная — сервер может быть новее клиента', () => {
    expect(shotVoice('чего-то новое', null, false).cue).toBe('shot-bolt');
    expect(shotVoice('чего-то новое', { ...catalog.pulse, kind: 'квазар' }, false).cue).toBe('shot-bolt');
  });

  it('таран, осколки и противоракета звучат сами по себе', () => {
    // Каталог им не задаётся вовсе: у этих псевдо-пушек параметров нет, и спрашивать их нельзя.
    expect(shotVoice(RAM_WEAPON, null, false).cue).toBe('ram');
    expect(shotVoice(SPLASH_WEAPON, null, false).cue).toBe('splash');
    expect(shotVoice(GUARD_WEAPON, null, false).cue).toBe('guard');
  });

  it('выстрел ракетницы в снапшоте — это прилёт ракеты, а не пуск', () => {
    expect(shotVoice('missiles', catalog.missiles, false).cue).toBe('splash');
    expect(launchVoice(catalog.missiles, true).cue).toBe('launch-missile');
    expect(launchVoice(catalog.torpedoes, true).cue).toBe('launch-torpedo');
  });

  it('свой выстрел важнее чужого', () => {
    expect(shotVoice('pulse', catalog.pulse, true).priority).toBeGreaterThan(shotVoice('pulse', catalog.pulse, false).priority);
  });
});

describe('звук попадания', () => {
  it('отбитое защитой — не промах и не попадание', () => {
    expect(impactVoice(shot({ blk: true, dmg: 0, hit: true }), false)?.cue).toBe('block');
  });

  it('щит и корпус звучат по-разному', () => {
    expect(impactVoice(shot({ dmg: 40, sh: 40 }), false)?.cue).toBe('hit-shield');
    expect(impactVoice(shot({ dmg: 90, sh: 40 }), false)?.cue).toBe('hit-hull');
  });

  it('свист мимо слышно только по себе', () => {
    expect(impactVoice(shot({ hit: false, dmg: 0 }), true)?.cue).toBe('whizz');
    expect(impactVoice(shot({ hit: false, dmg: 0 }), false)).toBeNull();
  });

  it('попадание по мне важнее попадания по соседу', () => {
    const mine = impactVoice(shot(), true)!.priority;
    expect(mine).toBeGreaterThan(impactVoice(shot(), false)!.priority);
  });
});

describe('взрыв', () => {
  it('крупный корпус звучит ниже мелкого', () => {
    expect(killVoice(80, false).rate).toBeLessThan(killVoice(24, false).rate);
  });

  it('свой корабль — отдельный звук', () => {
    expect(killVoice(40, true).cue).toBe('death');
    expect(killVoice(40, false).cue).toBe('explode');
  });

  it('взрыв громче выстрела', () => {
    expect(cueGain('explode')).toBeGreaterThan(cueGain('shot-bolt'));
  });
});

describe('задержка удара', () => {
  it('луч бьёт мгновенно, снаряд — с опозданием', () => {
    expect(flightMs('laser', catalog.laser)).toBe(0);
    expect(flightMs('plasma', catalog.plasma)).toBeGreaterThan(flightMs('railgun', catalog.railgun));
  });

  it('у тарана, осколков и прилетевшей ракеты снаряда не было', () => {
    expect(flightMs(RAM_WEAPON, null)).toBe(0);
    expect(flightMs(SPLASH_WEAPON, null)).toBe(0);
    expect(flightMs('missiles', catalog.missiles)).toBe(0);
  });
});
