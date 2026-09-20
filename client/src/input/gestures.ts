/**
 * Где прокрутка — дело списка, а не игры: тело дока, карта галактики и окно управления.
 * Сюда же смотрит обработчик колеса в keyboard.ts: колесо над списком крутит список, а не тягу.
 */
export const SCROLLABLE = '.dock-body, .galaxy-card, .controls-card';

/** Событие пришло из прокручиваемого окна (или из поля ввода) — браузер должен обработать его сам. */
export function insideScrollable(target: EventTarget | null): boolean {
  return target instanceof HTMLInputElement || (target instanceof Element && target.closest(SCROLLABLE) !== null);
}

/**
 * iOS Safari: системный зум, двойной тап и «резинка» страницы мешают игровому управлению.
 * Прокрутка разрешена только там, где она нужна: в полях ввода и в списках экрана станции.
 */
export function preventBrowserGestures(): void {
  const block = (e: Event) => e.preventDefault();
  for (const type of ['gesturestart', 'gesturechange', 'gestureend', 'dblclick', 'contextmenu']) {
    document.addEventListener(type, block, { passive: false });
  }
  document.addEventListener(
    'touchmove',
    (e) => {
      if (insideScrollable(e.target)) return;
      e.preventDefault();
    },
    { passive: false },
  );
}
