/**
 * Бургер-меню (M15.5): одно на док и на полёт. Живёт в своём корне рядом с #dock, а не внутри него, —
 * экран дока перерисовывается целиком после каждой покупки, и меню внутри него умирало бы на полуслове.
 */

export type MenuAction = 'controls' | 'password' | 'dev' | 'logout';

export interface MenuItem {
  id: MenuAction;
  label: string;
}

/**
 * Что в меню. Чистая: её и проверяют тесты — DOM для этого не нужен.
 *
 * @param coarse Палец, а не мышь: переназначать клавиши не на чем, и пункта «Управление» нет (M10.5).
 *   Зато нужна «Отладка»: на телефоне dev-панель открывали тапом по строке полёта, а её в космосе больше нет.
 * @param account Пилот вошёл по нику и паролю (M15.7); гостю менять нечего — у него и аккаунта нет.
 */
export function menuItems(coarse: boolean, account = false): MenuItem[] {
  const items: MenuItem[] = [];
  if (!coarse) items.push({ id: 'controls', label: 'Управление' });
  if (account) items.push({ id: 'password', label: 'Сменить пароль' });
  if (coarse) items.push({ id: 'dev', label: 'Отладка' });
  items.push({ id: 'logout', label: 'Выход' });
  return items;
}

/** Палец, а не мышь. */
export const coarsePointer = (): boolean =>
  typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches;

export class BurgerMenu {
  private readonly card: HTMLElement;
  /** Пилот вошёл по нику и паролю — тогда в меню есть «Сменить пароль». */
  account = false;

  constructor(
    private readonly root: HTMLElement,
    private readonly onPick: (id: MenuAction) => void,
  ) {
    this.card = document.createElement('div');
    this.card.className = 'menu-card sro-pane sro-menu';
    root.append(this.card);
    root.hidden = true;
    // Щелчок мимо карточки закрывает меню — как у окна «Управление» и карты галактики.
    root.addEventListener('pointerdown', (e) => {
      if (e.target === root) this.hide();
    });
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  /** Открыть под кнопкой, которая его вызвала. */
  showAt(anchor: DOMRect): void {
    this.render();
    this.root.hidden = false;
    // Карточка прижимается к правому краю кнопки: так она не уезжает за край узкого экрана.
    this.card.style.top = `${Math.round(anchor.bottom + 6)}px`;
    this.card.style.right = `${Math.max(8, Math.round(window.innerWidth - anchor.right))}px`;
  }

  toggleAt(anchor: DOMRect): void {
    if (this.open) this.hide();
    else this.showAt(anchor);
  }

  hide(): void {
    this.root.hidden = true;
  }

  private render(): void {
    const items = menuItems(coarsePointer(), this.account).map((item) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'menu-item sro-menu__item';
      button.textContent = item.label;
      button.addEventListener('click', () => {
        button.blur();
        this.hide();
        this.onPick(item.id);
      });
      return button;
    });
    this.card.replaceChildren(...items);
  }
}
