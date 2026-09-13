import './style.css';
import { Application, Graphics } from 'pixi.js';
import { preventBrowserGestures } from './input/gestures';
import { Connection } from './net/connection';
import { resolveServerUrl } from './net/serverUrl';
import { Starfield } from './render/starfield';
import { ConnectForm } from './ui/connectForm';
import { StatusHud } from './ui/statusHud';
import { playerName } from './util/playerName';

async function main(): Promise<void> {
  preventBrowserGestures();

  const app = new Application();
  await app.init({
    resizeTo: window,
    background: '#05060a',
    antialias: true,
    resolution: Math.min(window.devicePixelRatio || 1, 2),
    autoDensity: true,
  });
  document.getElementById('game')!.appendChild(app.canvas);

  const starfield = new Starfield();
  const ship = createPlaceholderShip();
  app.stage.addChild(starfield.view, ship);

  const serverUrl = resolveServerUrl();
  const connection = serverUrl ? new Connection(serverUrl, playerName()) : null;
  const connectForm = new ConnectForm(document.getElementById('connect')!);
  const hud = new StatusHud(document.getElementById('status')!, () => connectForm.show(serverUrl));

  if (connection) connection.connect();
  else connectForm.show(null);

  // M0: камера медленно дрейфует, чтобы было видно параллакс и плавность рендера.
  const drift = { x: 30, y: -45 };
  const camera = { x: 0, y: 0 };
  app.ticker.add((ticker) => {
    const dt = ticker.deltaMS / 1000;
    camera.x += drift.x * dt;
    camera.y += drift.y * dt;
    starfield.update(camera.x, camera.y, app.screen.width, app.screen.height);
    ship.position.set(app.screen.width / 2, app.screen.height / 2);
    ship.rotation = Math.atan2(drift.x, -drift.y); // нос по направлению дрейфа
    hud.update(connection, app.ticker.FPS);
  });
}

/** Временный корабль: нос смотрит вверх при rotation = 0. */
function createPlaceholderShip(): Graphics {
  return new Graphics()
    .poly([0, -18, 12, 14, 0, 8, -12, 14])
    .fill(0x7fd4ff)
    .stroke({ width: 1.5, color: 0xffffff, alpha: 0.6 });
}

main();
