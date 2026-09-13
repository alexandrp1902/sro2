/** iOS Safari: системный зум, двойной тап и «резинка» страницы мешают игровому управлению. */
export function preventBrowserGestures(): void {
  const block = (e: Event) => e.preventDefault();
  for (const type of ['gesturestart', 'gesturechange', 'gestureend', 'dblclick', 'contextmenu']) {
    document.addEventListener(type, block, { passive: false });
  }
  document.addEventListener(
    'touchmove',
    (e) => {
      if (!(e.target instanceof HTMLInputElement)) e.preventDefault();
    },
    { passive: false },
  );
}
