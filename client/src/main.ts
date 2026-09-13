import './style.css';
import { Application, Container } from 'pixi.js';
import { SPAWN } from './game/layout';
import { FixedLoop } from './game/loop';
import { Controls } from './input/controls';
import { preventBrowserGestures } from './input/gestures';
import { KeyboardControls, bindKeyboard } from './input/keyboard';
import { Stick } from './input/stick';
import { Zoom } from './input/zoom';
import { Connection } from './net/connection';
import { Prediction } from './net/prediction';
import type { SnapshotMsg } from './net/protocol';
import { RemoteShips } from './net/remoteShips';
import { resolveServerUrl } from './net/serverUrl';
import { Camera } from './render/camera';
import { ShipView, engineGlow } from './render/ship';
import { Starfield } from './render/starfield';
import { createWorldView } from './render/world';
import { DEFAULT_HULL, Hulls } from './sim/hulls';
import { directionAngle, localVelocity } from './sim/movement';
import { ConnectForm } from './ui/connectForm';
import { DevOverlay } from './ui/devOverlay';
import { FlightHud } from './ui/flightHud';
import { StatusHud } from './ui/statusHud';
import { playerName } from './util/playerName';
import { storage } from './util/storage';

const HULL_KEY = 'sro.hull';
const OWN_COLOR = 0x7fd4ff;

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

  const hulls = new Hulls();
  const controls = new Controls();
  const keyboard = new KeyboardControls(controls);
  bindKeyboard(keyboard);
  new Stick(document.getElementById('stick')!, controls);
  const zoom = new Zoom(app.canvas);

  const starfield = new Starfield();
  const world = new Container();
  const remote = new RemoteShips(hulls);
  const ownShip = new ShipView(OWN_COLOR);
  world.addChild(createWorldView(), remote.view, ownShip.view);
  app.stage.addChild(starfield.view, world);
  const camera = new Camera();

  const savedHull = storage.get(HULL_KEY);
  const prediction = new Prediction(hulls, savedHull && hulls.has(savedHull) ? savedHull : DEFAULT_HULL, SPAWN);

  const serverUrl = resolveServerUrl();
  const connection = serverUrl ? new Connection(serverUrl, playerName(), () => prediction.hullId) : null;
  const connectForm = new ConnectForm(document.getElementById('connect')!);
  const status = new StatusHud(document.getElementById('status')!, () => connectForm.show(serverUrl));

  const selectHull = (id: string) => {
    storage.set(HULL_KEY, id);
    prediction.hullId = id; // сервер подтвердит в снапшоте
    if (connection?.state === 'online') connection.send({ t: 'hull', id });
  };
  const dev = new DevOverlay(document.getElementById('dev')!, hulls, selectHull, connection?.lag ?? null);
  const flight = new FlightHud(document.getElementById('flight')!, () => dev.toggle());

  // За кадр сверяемся только с самым свежим снапшотом; чужим кораблям нужен весь поток.
  let latestSnapshot: SnapshotMsg | null = null;
  if (connection) {
    connection.onWelcome = (message) => {
      hulls.set(message.hulls);
      prediction.resetNet();
      remote.clear();
    };
    connection.onConfig = (message) => hulls.set(message.hulls);
    connection.onSnapshot = (message) => {
      latestSnapshot = message;
      remote.push(message, performance.now());
    };
    connection.connect();
  } else {
    connectForm.show(null);
  }

  const isOnline = () => connection?.state === 'online';
  const loop = new FixedLoop(() => {
    prediction.step(
      controls.input(),
      isOnline() ? (seq, input) => connection!.send({ t: 'input', seq, dx: input.dx, dy: input.dy, th: input.throttle }) : null,
    );
  });

  let lastFrame = performance.now();
  let wasOnline = false;
  app.ticker.add(() => {
    const now = performance.now();
    const frameSeconds = Math.min(0.1, (now - lastFrame) / 1000);
    lastFrame = now;
    keyboard.update(now);

    const online = isOnline();
    if (wasOnline && !online) {
      prediction.resetNet(); // летим дальше локально, при подключении примем состояние сервера
      remote.clear();
    }
    wasOnline = online;
    if (latestSnapshot && online) {
      const own = latestSnapshot.ships.find((ship) => ship.id === connection!.playerId);
      if (own) prediction.reconcile(own);
    }
    latestSnapshot = null;

    const alpha = loop.advance(now);
    const state = prediction.render(alpha, frameSeconds);
    const hull = hulls.get(prediction.hullId);
    const input = controls.input();
    const desired = input.throttle > 0 ? directionAngle(input.dx, input.dy) : null;
    ownShip.update(state.x, state.y, state.rot, hull, engineGlow(prediction.curr, input.throttle, hull), desired);
    remote.update(now, online ? connection!.playerId : -1);

    camera.follow(state.x, state.y, zoom.value).apply(world, app.screen.width, app.screen.height);
    starfield.update(camera.x, camera.y, app.screen.width, app.screen.height);

    const speed = Math.hypot(prediction.curr.vx, prediction.curr.vy);
    const velocity = localVelocity(prediction.curr);
    status.update(connection, app.ticker.FPS, !prediction.isSynced);
    flight.update(hull.name, speed, input.throttle);
    dev.update({
      tick: connection?.lastTick ?? 0,
      tickRate: connection?.snapshotRate ?? 0,
      pingMs: connection?.rttMs ?? 0,
      speed,
      forward: velocity.forward,
      lateral: velocity.lateral,
      throttle: input.throttle,
      desiredDeg: toCompass(directionAngle(input.dx, input.dy)),
      headingDeg: toCompass(prediction.curr.rot),
      pending: prediction.pendingCount,
      correction: prediction.lastCorrection,
      peakCorrection: prediction.takePeakCorrection(),
      snaps: prediction.snaps,
      online: prediction.isSynced,
      hullId: prediction.hullId,
    });
  });
}

/** Угол в градусах по компасу экрана: 0 — вверх, 90 — вправо. */
function toCompass(angle: number): number {
  return ((angle * 180) / Math.PI + 360) % 360;
}

main();
