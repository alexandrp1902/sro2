import { MOVE_ACTIONS, keymap as defaultKeymap, mouseCode, type KeyAction, type Keymap } from './keymap';

/**
 * Кнопки мыши как горячие клавиши (окно «Управление»): ПКМ рядом с пробелом — тот же «взять / док / огонь».
 *
 * Ловим только по игровому полю: правый клик по доку, карте или окну не должен ничего запускать —
 * случайный клик мимо кнопки магазина не выбросит корабль в космос. Контекстное меню браузера уже
 * подавлено глобально (input/gestures.ts), отдельно гасить его здесь не нужно.
 *
 * ЛКМ не назначается: ею выбирают цель (input/tapSelect.ts).
 */
export function bindMouseButtons(
  canvas: HTMLElement,
  actions: {
    /** Действие-нажатие: огонь, смена цели, карта, ближайший предмет, масштаб. */
    press(action: KeyAction): void;
    /** Действие-удержание (газ, тормоз, поворот): код синтетический, как у клавиши. */
    hold(code: string, down: boolean): void;
  },
  keys: Keymap = defaultKeymap,
): void {
  /** Кнопки, которые держат: отпустить надо и за пределами поля, иначе газ залипнет. */
  const held = new Set<string>();

  const release = (code: string): void => {
    if (held.delete(code)) actions.hold(code, false);
  };

  canvas.addEventListener('pointerdown', (e) => {
    if (keys.capturing || e.pointerType !== 'mouse' || e.button === 0) return;
    const code = mouseCode(e.button);
    const action = keys.actionFor({ code, shiftKey: e.shiftKey });
    if (!action) return;
    e.preventDefault();
    if (MOVE_ACTIONS.includes(action)) {
      if (!held.has(code)) {
        held.add(code);
        actions.hold(code, true);
      }
      return;
    }
    actions.press(action);
  });

  // Отпускание и потеря фокуса — на окне: кнопку могли отпустить над панелью HUD или вообще вне вкладки.
  window.addEventListener('pointerup', (e) => release(mouseCode(e.button)));
  window.addEventListener('pointercancel', (e) => release(mouseCode(e.button)));
  window.addEventListener('blur', () => {
    for (const code of [...held]) release(code);
  });
}
