/** localStorage, который не падает в приватном режиме Safari и при заблокированных данных сайта. */
export const storage = {
  get(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key: string, value: string): void {
    try {
      localStorage.setItem(key, value);
    } catch {
      // данные просто не запомнятся
    }
  },
};
