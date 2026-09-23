/**
 * «Что дальше» (M18): карточка на три строки после последнего шага обучения. Показывается один раз —
 * сервер присылает «обучение пройдено» ровно однажды, — а потом живёт в бургере пунктом «Советы».
 * Устроена как карточка подтверждения, только кнопка одна и не красная: здесь нечего бояться.
 */

export interface Tip {
  title: string;
  text: string;
}

/**
 * Три совета: где взять работу, где узнать цены, как проложить путь. Чистая: тексты проверяют тесты.
 * @param coarse Палец, а не мышь: про клавишу карты на телефоне говорить незачем.
 */
export function tipLines(coarse: boolean): Tip[] {
  return [
    {
      title: 'Доска заданий',
      text: 'Первая вкладка дока. Доставка, охота, сопровождение — чем опаснее система, тем выше награда.',
    },
    {
      title: 'Груз и слухи',
      text: 'Вкладка «Груз» в доке: что здесь делают — дёшево, что скупают — дорого. Слухи подскажут, где спрос.',
    },
    {
      title: 'Карта и курс',
      text: coarse
        ? 'Тап по миникарте — карта галактики. Выберите систему: курс поведёт к нужным вратам.'
        : 'M или клик по миникарте — карта галактики. Выберите систему: курс поведёт к нужным вратам.',
    },
  ];
}

export class TipsCard {
  private readonly list: HTMLElement;

  constructor(private readonly root: HTMLElement) {
    const title = document.createElement('div');
    title.className = 'tips-title sro-dialog__title';
    title.textContent = 'Что дальше';
    this.list = document.createElement('div');
    this.list.className = 'tips-list';
    const buttons = document.createElement('div');
    buttons.className = 'sro-dialog__actions';
    const ok = document.createElement('button');
    ok.type = 'button';
    ok.className = 'tips-ok sro-btn';
    ok.textContent = 'Понятно';
    ok.addEventListener('click', () => {
      ok.blur();
      this.hide();
    });
    buttons.append(ok);
    root.append(title, this.list, buttons);
    root.hidden = true;
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  show(coarse: boolean): void {
    this.list.replaceChildren(
      ...tipLines(coarse).map((tip) => {
        const row = document.createElement('div');
        row.className = 'tips-row';
        const head = document.createElement('div');
        head.className = 'tips-row__title';
        head.textContent = tip.title;
        const text = document.createElement('div');
        text.className = 'tips-row__text sro-dialog__text';
        text.textContent = tip.text;
        row.append(head, text);
        return row;
      }),
    );
    this.root.hidden = false;
  }

  hide(): void {
    this.root.hidden = true;
  }
}
