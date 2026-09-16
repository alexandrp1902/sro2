import { paintNebula } from './nebula';

/** Туманность считается сотни миллисекунд — в отдельном потоке, чтобы игра не замирала при старте. */
self.onmessage = (event: MessageEvent<{ size: number; seed: number }>) => {
  const pixels = paintNebula(event.data.size, event.data.seed);
  self.postMessage(pixels, { transfer: [pixels.buffer] });
};
