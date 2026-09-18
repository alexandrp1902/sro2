/** Где палец может прокручивать: списки экрана станции. */
const SCROLLABLE = '.dock-body';

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
      if (e.target instanceof HTMLInputElement) return;
      if (e.target instanceof Element && e.target.closest(SCROLLABLE)) return;
      e.preventDefault();
    },
    { passive: false },
  );
}
