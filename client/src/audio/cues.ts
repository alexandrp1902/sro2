import type { ShotDto } from '../net/protocol';
import type { WeaponParams } from '../sim/combat';
import meta from './sfxMeta.json';

/**
 * Что чем звучит: боевое событие превращается в звук из банка (tools/sfx.py). Чистый модуль — ни Web Audio,
 * ни PixiJS, поэтому правила проверяются тестами без браузера.
 *
 * Таблица решений здесь та же, что у картинки в render/combatFx.ts (shotStyle): один выстрел не может
 * выглядеть лучом, а звучать снарядом. Псевдо-пушки тарана, осколков и противоракеты живут своей жизнью —
 * их в каталоге оружия нет, и спрашивать про них weapons.get нельзя: он молча вернёт импульсную.
 */

export type SfxCue = keyof typeof meta.cues;

/** Псевдо-пушки из ShotDto. Зеркало констант render/combatFx.ts — там же объяснено, откуда они берутся. */
export const RAM_WEAPON = 'ram';
export const SPLASH_WEAPON = 'splash';
export const GUARD_WEAPON = 'guard';

export const isPseudoWeapon = (w: string): boolean => w === RAM_WEAPON || w === SPLASH_WEAPON || w === GUARD_WEAPON;

/** Голос: что играть, с какой высотой и громкостью и насколько его жалко потерять при перегрузке. */
export interface Voice {
  cue: SfxCue;
  /** Скорость воспроизведения: она же высота. 1 — как записано. */
  rate: number;
  /** Множитель поверх громкости самого звука из манифеста. */
  gain: number;
  priority: number;
}

/**
 * Чем важнее звук, тем он выше. Тревога о ракете не теряется никогда, чужой выстрел на краю радара —
 * первый кандидат на вылет.
 */
export const PRIORITY = {
  alarm: 100,
  ownDeath: 90,
  ownExplosion: 80,
  hitMe: 70,
  myShot: 60,
  explosion: 50,
  hit: 40,
  shot: 30,
  minor: 10,
} as const;

/** Калибр слышен: мелкая пушка выше и короче, крупная ниже и тяжелее. */
const CLASS_RATE: Record<string, number> = { S: 1.18, M: 1.0, L: 0.82 };

const KIND_CUE: Record<string, SfxCue> = {
  bolt: 'shot-bolt',
  beam: 'shot-beam',
  orb: 'shot-orb',
  rail: 'shot-rail',
  ion: 'shot-ion',
  flak: 'shot-flak',
};

export const hasCue = (name: string): name is SfxCue => name in meta.cues;

/** Громкость звука из манифеста: ею разведены между собой взрыв, выстрел и щелчок. */
export const cueGain = (cue: SfxCue): number => meta.cues[cue].gain;
export const cueLength = (cue: SfxCue): number => meta.cues[cue].ms;
export const cueVariants = (cue: SfxCue): number => meta.cues[cue].n;

/**
 * Сколько снаряд летит до цели, мс: звук удара обязан совпасть с картинкой. Зеркало FLIGHT_MS
 * в render/combatFx.ts — там же объяснено, почему у луча ноль.
 */
const FLIGHT_MS: Record<string, number> = { bolt: 150, orb: 320, beam: 0, rail: 90, ion: 260, flak: 120 };

/** Задержка между выстрелом и ударом. У тарана, осколков и прилетевшей ракеты снаряда не было вовсе. */
export function flightMs(w: string, weapon: WeaponParams | null): number {
  if (w === GUARD_WEAPON) return FLIGHT_MS.flak;
  if (isPseudoWeapon(w) || weapon?.missile) return 0;
  return FLIGHT_MS[weapon?.kind ?? 'bolt'] ?? FLIGHT_MS.bolt;
}

/**
 * Звук самого выстрела.
 *
 * @param w id пушки из ShotDto — он же ключ shared/weapons.json или псевдо-пушка
 * @param weapon параметры пушки; null — пушки в каталоге нет (сервер новее клиента)
 */
export function shotVoice(w: string, weapon: WeaponParams | null, mine: boolean): Voice {
  const priority = mine ? PRIORITY.myShot : PRIORITY.shot;
  if (w === RAM_WEAPON) return { cue: 'ram', rate: 1, gain: 1, priority: PRIORITY.explosion };
  if (w === SPLASH_WEAPON) return { cue: 'splash', rate: 1, gain: 1, priority: PRIORITY.explosion };
  if (w === GUARD_WEAPON) return { cue: 'guard', rate: 1, gain: 1, priority: PRIORITY.minor };
  // Выстрел ракетницы в снапшоте — это прилёт ракеты, а не пуск: ствол своё уже отговорил,
  // и слышен маленький взрыв у цели. Пуск звучит там, где ракета появляется (render/missiles.ts).
  if (weapon?.missile) return { cue: 'splash', rate: 1.1, gain: 1, priority: PRIORITY.explosion };
  const cue = (weapon && KIND_CUE[weapon.kind]) ?? 'shot-bolt';
  return { cue, rate: CLASS_RATE[weapon?.class ?? 'M'] ?? 1, gain: 1, priority };
}

/** Пуск ракеты: торпеда тяжелее и длиннее обычной. */
export function launchVoice(weapon: WeaponParams | null, mine: boolean): Voice {
  const torpedo = weapon?.missile?.sprite === 'torpedo';
  return {
    cue: torpedo ? 'launch-torpedo' : 'launch-missile',
    rate: 1,
    gain: 1,
    priority: mine ? PRIORITY.myShot : PRIORITY.explosion,
  };
}

/**
 * Звук в точке попадания: он играет не в момент выстрела, а когда снаряд долетел, —
 * иначе на дальней дистанции удар слышен раньше, чем виден.
 *
 * @param atMe стреляли по моему кораблю
 * @returns null — слышать нечего (чужой промах)
 */
export function impactVoice(shot: ShotDto, atMe: boolean): Voice | null {
  const priority = atMe ? PRIORITY.hitMe : PRIORITY.hit;
  if (shot.blk) return { cue: 'block', rate: 1, gain: 1, priority };
  if (!shot.hit) {
    // Свист мимо — только по себе: в бою на десяток кораблей чужие промахи дают сплошное шипение.
    return atMe ? { cue: 'whizz', rate: 1, gain: 1, priority: PRIORITY.minor } : null;
  }
  const hull = shot.dmg - shot.sh;
  return { cue: hull > 0 ? 'hit-hull' : 'hit-shield', rate: 1, gain: 1, priority };
}

/**
 * Взрыв корабля: крупный корпус звучит ниже и дольше.
 *
 * @param size размер корпуса, как его рисует игра (hull.size)
 * @param own погиб мой корабль — тогда это отдельный, более весомый звук
 */
export function killVoice(size: number, own: boolean): Voice {
  if (own) return { cue: 'death', rate: 1, gain: 1, priority: PRIORITY.ownDeath };
  const rate = Math.min(1.45, Math.max(0.72, 42 / Math.max(size, 8)));
  return { cue: 'explode', rate, gain: 1, priority: PRIORITY.explosion };
}
