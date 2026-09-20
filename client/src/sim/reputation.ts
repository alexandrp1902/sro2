// Зеркало server/Sro.Sim/ReputationRules.cs: ступени отношения, гейт магазина и цены со скидкой (M13).
//
// Распад к нулю здесь намеренно не зеркалится: он считается по часам реального времени и живёт только
// на сервере — клиент получает уже просевшие очки и ничего с ними не делает.

import { tierOf } from './fitting';

export interface RepLevel {
  id: string;
  name: string;
  /** Нижняя граница включительно; у первой ступени это минус предел шкалы. */
  from: number;
  /** Множитель цен снаряжения и ремонта: 0.9 — на 10 % дешевле. */
  price?: number;
  color?: string;
}

export interface RepGate {
  level?: string | null;
  tiers?: number[] | null;
  hulls?: string[] | null;
}

export interface ReputationRules {
  limit: number;
  levels?: RepLevel[] | null;
  gate?: RepGate | null;
}

/** Репутации нет: до welcome и на серверах без reputation.json. */
export const NO_REP: ReputationRules = { limit: 100, levels: null, gate: null };

/** Заглушка для шкалы без уровней: ничего не меняет ни в цене, ни в доступе. */
const PLAIN: RepLevel = { id: 'neutral', name: 'Нейтрал', from: 0, price: 1, color: '#8a93a6' };

function levels(rules: ReputationRules): RepLevel[] {
  return rules.levels ?? [];
}

/** Номер ступени по очкам; 0 — худшая. */
export function levelIndex(rules: ReputationRules, value: number): number {
  const list = levels(rules);
  let index = 0;
  for (let i = 0; i < list.length; i++) if (value >= list[i].from) index = i;
  return index;
}

/** Ступень по очкам — как на сервере: берётся нижняя граница включительно. */
export function levelOf(rules: ReputationRules, value: number): RepLevel {
  const list = levels(rules);
  return list.length > 0 ? list[levelIndex(rules, value)] : PLAIN;
}

/** Ступень по её id; нет такой — нейтральная заглушка. */
export function levelById(rules: ReputationRules, id: string | null | undefined): RepLevel {
  return levels(rules).find((level) => level.id === id) ?? PLAIN;
}

export function levelColor(level: RepLevel): string {
  return level.color ?? PLAIN.color!;
}

/** Дотянул ли пилот до этой ступени. Неизвестная ступень никого не пускает. */
export function atLeast(rules: ReputationRules, value: number, want: string | null | undefined): boolean {
  const list = levels(rules);
  const index = list.findIndex((level) => level.id === want);
  return index >= 0 && levelIndex(rules, value) >= index;
}

/** Множитель цены на этой ступени. */
export function priceMul(level: RepLevel): number {
  return level.price ?? 1;
}

/**
 * Цена с учётом отношения. Округление — то же, что у ShopRules.Round на сервере: от сотни до десятков,
 * мелочь до кредита. Разойтись здесь нельзя: дока показывает цену, которую спишет сервер.
 *
 * Множитель ровно 1 (нейтрал, правил нет) возвращает цену как есть: округление — часть скидки,
 * а не бесплатная добавка, иначе ремонт за 101 кр стоил бы 100 и без всякой репутации.
 */
export function repPrice(basePrice: number, mul: number): number {
  if (mul === 1) return basePrice;
  const value = basePrice * mul;
  return value >= 100 ? Math.round(value / 10) * 10 : Math.round(value);
}

/** Продаётся ли это только своим: старший тир снаряжения или корпус из списка. */
export function gated(rules: ReputationRules, id: string, hull: boolean): boolean {
  const gate = rules.gate;
  if (!gate?.level) return false;
  return hull ? (gate.hulls ?? []).includes(id) : (gate.tiers ?? []).includes(tierOf(id));
}

/** Пускают ли пилота с такими очками к этому товару. */
export function allows(rules: ReputationRules, value: number, id: string, hull: boolean): boolean {
  return !gated(rules, id, hull) || atLeast(rules, value, rules.gate?.level);
}

/**
 * То же, но по названию ступени, а не по очкам: сервер присылает действующую ступень готовой
 * (лучшее из станции и региона), и пересчитывать её из очков на клиенте значило бы повторять
 * региональную арифметику — разойтись в ней проще, чем сойтись.
 */
export function allowsLevel(rules: ReputationRules, level: string | null | undefined, id: string, hull: boolean): boolean {
  if (!gated(rules, id, hull)) return true;
  const list = levels(rules);
  const have = list.findIndex((l) => l.id === level);
  const want = list.findIndex((l) => l.id === rules.gate?.level);
  return have >= 0 && want >= 0 && have >= want;
}

/** Подпись отношения: «Друг (+42)». Ноль пишется без знака. */
export function repLabel(level: RepLevel, value: number): string {
  const sign = value > 0 ? '+' : '';
  return `${level.name} (${sign}${value})`;
}
