import type { MechRefusal } from '../net/protocol';
import type { MechPart, MechSide, Refusal } from './rules';

/** Подписи наземного боя (M21): чистые функции, чтобы их проверял тест, а не глаз. */

export const PART_LABEL: Record<MechPart, string> = {
  body: 'Корпус',
  left: 'Левая рука',
  right: 'Правая рука',
  chassis: 'Шасси',
};

export const SIDE_LABEL: Record<MechSide, string> = {
  front: 'фронт',
  left: 'левый бок',
  right: 'правый бок',
  rear: 'тыл',
};

const REFUSAL_TEXT: Record<MechRefusal, string> = {
  noRelay: 'Ретранслятор здесь не отвечает',
  noBattle: 'Связь с машиной потеряна — бой уже кончен',
  notYourTurn: 'Сейчас ход противника',
  alreadyMoved: 'Мех уже переместился в этот ход',
  unreachable: 'Туда не дойти за один ход',
  noTarget: 'Эту цель не атаковать',
  outOfRange: 'Цель вне дальности оружия',
  noLine: 'Стена закрывает линию огня',
  armDown: 'Оружие разбито — стрелять нечем',
  badPart: 'Эта часть уже выведена из строя',
  badAct: 'Команда не распознана',
  over: 'Бой уже окончен',
  staleRevision: 'Состояние изменилось — данные обновлены',
  mechBusy: 'Идёт наземный бой',
  workshopLocked: 'Мастерская откроется после первой сюжетной победы на Нова-Прайм',
  ownership: 'Деталь или машина недоступна: проверьте владельца и установленные экземпляры',
  overweight: 'Масса превышает грузоподъёмность шасси',
  incomplete: 'Нужны корпус, шасси, обе руки и хотя бы одно оружие',
  mechLimit: 'Можно хранить не больше четырёх мехов',
  resources: 'Недостаточно кредитов или ресурсов',
  badAssembly: 'Недопустимая сборка или имя',
  squadRequired: 'Выберите требуемое число готовых машин',
  cargoFull: 'В трюме недостаточно места',
  saveFailed: 'Не удалось сохранить результат. Повторите запрос',
  badRepair: 'Ремонт недоступен: нужна живая повреждённая часть и неиспользованный ремонтный модуль',
};

export function refusalText(code: MechRefusal | string): string {
  return REFUSAL_TEXT[code as MechRefusal] ?? 'Ход не принят';
}

/** Почему атака недоступна — подпись под серой кнопкой, чтобы не гадать. */
export function forecastBlocker(refusal: Refusal | undefined): string | null {
  switch (refusal) {
    case 'noGun': return 'Оружие разбито';
    case 'outOfRange': return 'Вне дальности — подойдите или отойдите';
    case 'noLine': return 'Стена на линии огня';
    default: return null;
  }
}

/** «Ход: 4 клетки» — с правильным окончанием. */
export function cells(n: number): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  const word = mod10 === 1 && mod100 !== 11 ? 'клетка'
    : mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14) ? 'клетки'
    : 'клеток';
  return `${n} ${word}`;
}

export function roundChip(round: number, turn: 'player' | 'enemy', over: boolean): string {
  if (over) return `Раунд ${round} · бой окончен`;
  return `Раунд ${round} · ${turn === 'player' ? 'ваш ход' : 'ход противника'}`;
}

export function objectiveLine(goal: string, killed: number, total: number): string {
  return `${goal} · ${killed}/${total}`;
}

export function endTitle(won: boolean): string {
  return won ? 'Противник уничтожен' : 'Ретранслятор потерял связь';
}

export function endText(won: boolean, reward: number, first: boolean, rewardText: string, training = false): string {
  if (training) return won
    ? 'Тренировка пройдена. Можно повторить бесплатно и попробовать другую тактику.'
    : 'Тренировка завершена. Прототип восстановлен — можно попробовать снова бесплатно.';
  if (!won) return 'Машину вытащили, повреждения чинятся сами. Повтор — бесплатно.';
  if (first && reward > 0) return `Первая вылазка удалась. Награда: ${rewardText}.`;
  return 'Бой выигран. Награда за вылазку уже выплачена.';
}
