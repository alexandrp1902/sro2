import { Application } from 'pixi.js';

/**
 * Холст игры. Chrome на части Android-телефонов отдаёт WebGL в проверке, но не создаёт контекст (GPU в чёрном
 * списке, контекст потерян) — тогда Pixi бросает исключение, и игра не запускалась вовсе. Пробуем по очереди:
 * WebGL со сглаживанием, без него, WebGPU, Canvas 2D.
 *
 * @param resizeTo за чьим размером следить: окно для космоса, свой хост для наземного боя (M21)
 */
export async function createApp(resizeTo: Window | HTMLElement = window): Promise<Application> {
  const base = {
    resizeTo,
    background: '#08090b',
    resolution: Math.min(window.devicePixelRatio || 1, 2),
    autoDensity: true,
  };
  const attempts = [
    { preference: 'webgl', antialias: true },
    { preference: 'webgl', antialias: false },
    { preference: 'webgpu', antialias: false },
    { preference: 'canvas', antialias: false },
  ] as const;
  let error: unknown;
  for (const attempt of attempts) {
    const app = new Application();
    try {
      await app.init({ ...base, ...attempt, preference: [attempt.preference] });
      return app;
    } catch (e) {
      error = e;
      console.warn(`renderer ${attempt.preference} failed`, e);
    }
  }
  throw error;
}
