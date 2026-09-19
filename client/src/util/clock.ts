/** Обратный отсчёт «мм:сс»: 8 → «00:08», 102 → «01:42». Доли секунды округляются вверх. */
export function clock(seconds: number): string {
  const total = Math.max(0, Math.ceil(seconds));
  return `${String(Math.floor(total / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`;
}
