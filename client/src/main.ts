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
import { Roster } from './net/roster';
import { resolveServerUrl } from './net/serverUrl';
import { Camera } from './render/camera';
import { PlayerOverlay } from './render/playerOverlay';
import { ShipView, engineGlow } from './render/ship';
import { Starfield } from './render/starfield';
import { createWorldView } from './render/world';
import { DEFAULT_HULL, Hulls } from './sim/hulls';
import { directionAngle, localVelocity, type MoveInput } from './sim/movement';
import { DevOverlay } from './ui/devOverlay';
import { Feed } from './ui/feed';
import { FlightHud } from './ui/flightHud';
import { PilotForm } from './ui/pilotForm';
import { StatusHud } from './ui/statusHud';
import { playerName, setPlayerName } from './util/playerName';
import { storage } from './util/storage';

const HULL_KEY = 'sro.hull';
const OWN_COLOR = 0x7fd4ff;
/** Ниже этой скорости «корабль тормозит» в статусе не показываем. */
const STOPPED_SPEED = 1;

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

  const savedHull = storage.get(HULL_KEY);
  const prediction = new Prediction(hulls, savedHull && hulls.has(savedHull) ? savedHull : DEFAULT_HULL, SPAWN);

  const serverUrl = resolveServerUrl();
  const connection = serverUrl ? new Connection(serverUrl, playerName(), () => prediction.hullId) : null;

  const starfield = new Starfield();
  const world = new Container();
  const remote = new RemoteShips(hulls, connection?.roster ?? new Roster());
  const ownShip = new ShipView(OWN_COLOR);
  const overlay = new PlayerOverlay();
  world.addChild(createWorldView(), remote.view, ownShip.view);
  app.stage.addChild(starfield.view, world, overlay.view);
  const camera = new Camera();

  const feed = new Feed(document.getElementById('feed')!);
  const pilotForm = new PilotForm(document.getElementById('connect')!, (name) => {
    if (name === playerName()) return;
    setPlayerName(name);
    connection?.rename(name);
  });
  const status = new StatusHud(document.getElementById('status')!, () => pilotForm.show(serverUrl, playerName()));

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
      if (message.resumed) feed.add('Снова на связи — корабль ждал на месте');
    };
    connection.onConfig = (message) => hulls.set(message.hulls);
    connection.onSnapshot = (message) => {
      latestSnapshot = message;
      remote.push(message, performance.now());
    };
    connection.onRosterEvents = (events) => feed.push(events);
    connection.connect();
  } else {
    pilotForm.show(null, playerName());
  }

  const isOnline = () => connection?.state === 'online';
  // Связи нет, а сервер задан: корабль тормозит, как его копия на сервере, — после возврата не будет рывка.
  const isBraking = () => connection !== null && !isOnline();
  const flightInput = (): MoveInput => {
    const input = controls.input();
    if (isBraking()) input.throttle = 0;
    return input;
  };

  const loop = new FixedLoop(() => {
    keyboard.apply(prediction.curr, hulls.get(prediction.hullId));
    prediction.step(
      flightInput(),
      isOnline() ? (seq, input) => connection!.send({ t: 'input', seq, dx: input.dx, dy: input.dy, th: input.throttle }) : null,
    );
  });

  let lastFrame = performance.now();
  let wasOnline = false;
  app.ticker.add(() => {
    const now = performance.now();
    const frameSeconds = Math.min(0.1, (now - lastFrame) / 1000);
    lastFrame = now;

    const online = isOnline();
    if (wasOnline && !online) {
      prediction.resetNet(); // тормозим локально, при подключении примем состояние сервера
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
    const input = flightInput();
    const desired = input.throttle > 0 && controls.source === 'stick' ? directionAngle(input.dx, input.dy) : null;
    ownShip.update(state.x, state.y, state.rot, hull, engineGlow(prediction.curr, input.throttle, hull), desired);
    remote.update(now, online ? connection!.playerId : -1);

    camera.follow(state.x, state.y, zoom.value).apply(world, app.screen.width, app.screen.height);
    starfield.update(camera.x, camera.y, app.screen.width, app.screen.height);
    overlay.update(remote.visible(), camera, app.screen.width, app.screen.height);

    const speed = Math.hypot(prediction.curr.vx, prediction.curr.vy);
    const velocity = localVelocity(prediction.curr);
    status.update(connection, app.ticker.FPS, isBraking() && speed > STOPPED_SPEED);
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
      interpMs: remote.clock.delayMs,
      jitterMs: remote.clock.jitterMs,
      extrapolations: remote.extrapolations,
    });
  });
}

/** Угол в градусах по компасу экрана: 0 — вверх, 90 — вправо. */
function toCompass(angle: number): number {
  return ((angle * 180) / Math.PI + 360) % 360;
}

main();
