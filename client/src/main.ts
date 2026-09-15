import './style.css';
import { Application, Container } from 'pixi.js';
import { SPAWN } from './game/layout';
import { FixedLoop } from './game/loop';
import { cycle, nearest, pickArrow, pickAt } from './game/targeting';
import { Controls } from './input/controls';
import { FireControl, bindCombatKeys } from './input/fire';
import { preventBrowserGestures } from './input/gestures';
import { KeyboardControls, bindKeyboard } from './input/keyboard';
import { Stick } from './input/stick';
import { TapSelect } from './input/tapSelect';
import { Zoom } from './input/zoom';
import { CombatEvents, type CombatEvent } from './net/combatEvents';
import { Connection } from './net/connection';
import { Prediction } from './net/prediction';
import type { ShipDto, SnapshotMsg } from './net/protocol';
import { RemoteShips } from './net/remoteShips';
import { Roster } from './net/roster';
import { resolveServerUrl } from './net/serverUrl';
import { Camera } from './render/camera';
import { CombatFx, type FxAnchor } from './render/combatFx';
import { PlayerOverlay } from './render/playerOverlay';
import { ShipView, engineGlow } from './render/ship';
import { Starfield } from './render/starfield';
import { WeaponArc } from './render/weaponArc';
import { createWorldView } from './render/world';
import { Zones } from './render/zones';
import { assess, cooldownTicks } from './sim/combat';
import { DEFAULT_HULL, Hulls } from './sim/hulls';
import { DT, directionAngle, localVelocity, type MoveInput } from './sim/movement';
import { DEFAULT_WEAPON, Weapons } from './sim/weapons';
import { CombatHud } from './ui/combatHud';
import { DevOverlay } from './ui/devOverlay';
import { Feed, describeKill } from './ui/feed';
import { FlightHud } from './ui/flightHud';
import { PilotForm } from './ui/pilotForm';
import { StatusHud } from './ui/statusHud';
import { playerName, setPlayerName } from './util/playerName';
import { storage } from './util/storage';

const HULL_KEY = 'sro.hull';
const WEAPON_KEY = 'sro.weapon';
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
  const el = (id: string) => document.getElementById(id)!;

  const hulls = new Hulls();
  const weapons = new Weapons();
  const controls = new Controls();
  const keyboard = new KeyboardControls(controls);
  bindKeyboard(keyboard);
  const stick = new Stick(el('stick'), controls);
  const zoom = new Zoom(app.canvas);

  const savedHull = storage.get(HULL_KEY);
  const prediction = new Prediction(hulls, savedHull && hulls.has(savedHull) ? savedHull : DEFAULT_HULL, SPAWN);
  const savedWeapon = storage.get(WEAPON_KEY);
  let weaponId = savedWeapon && weapons.has(savedWeapon) ? savedWeapon : DEFAULT_WEAPON;

  const serverUrl = resolveServerUrl();
  const connection = serverUrl
    ? new Connection(serverUrl, playerName(), () => prediction.hullId, () => weaponId)
    : null;
  const roster = connection?.roster ?? new Roster();

  const starfield = new Starfield();
  const world = new Container();
  const remote = new RemoteShips(hulls, roster);
  const ownShip = new ShipView(OWN_COLOR);
  const weaponArc = new WeaponArc();
  const fx = new CombatFx(weapons);
  const overlay = new PlayerOverlay();
  const zones = new Zones();
  world.addChild(createWorldView(), zones.view, weaponArc.view, remote.view, ownShip.view, fx.view);
  app.stage.addChild(starfield.view, world, overlay.view);
  const camera = new Camera();

  const feed = new Feed(el('feed'));
  const pilotForm = new PilotForm(el('connect'), (name) => {
    if (name === playerName()) return;
    setPlayerName(name);
    connection?.rename(name);
  });
  const status = new StatusHud(el('status'), () => pilotForm.show(serverUrl, playerName()));

  const isOnline = () => connection?.state === 'online';
  const ownId = () => (isOnline() ? connection!.playerId : -1);

  // Цель (GDD §9) выбирает клиент, сервер помнит последнюю присланную.
  let targetId = 0;
  const setTarget = (id: number) => {
    if (id === targetId) return;
    targetId = id;
    if (isOnline()) connection!.send({ t: 'target', id });
  };
  const combatHud = new CombatHud(el('ship'), el('target'), el('death'), () => setTarget(0));

  const fire = new FireControl(el('fire'));
  // Атака без цели берёт ближайший корабль: сначала в секторе, потом просто в дальности.
  fire.onPress = () => {
    const current = remote.get(targetId);
    if (current && !current.dead) return;
    const id = nearest(prediction.curr, remote.visible(), weapons.get(weaponId));
    if (id !== null) setTarget(id);
    else if (isOnline()) feed.add('Нет цели в радиусе огня');
  };
  fire.onChange = (on) => {
    if (isOnline()) connection!.send({ t: 'fire', on });
  };
  bindCombatKeys(fire, {
    next: () => {
      const id = cycle(prediction.curr, remote.visible(), targetId);
      if (id !== null) setTarget(id);
    },
    clear: () => setTarget(0),
  });
  // Тап мимо кораблей цель не сбрасывает: промах пальцем в бою не должен её терять.
  // Корабль за краем экрана выбирается тапом по его стрелке или подписи у края.
  new TapSelect(app.canvas, (x, y, touch) => {
    const view = { x: camera.x, y: camera.y, zoom: camera.zoom, width: app.screen.width, height: app.screen.height };
    const id = pickAt(x, y, remote.visible(), view, touch) ?? pickArrow(x, y, overlay.edgeArrows(), touch);
    if (id !== null) setTarget(id);
  });

  const selectHull = (id: string) => {
    storage.set(HULL_KEY, id);
    prediction.hullId = id; // сервер подтвердит в снапшоте
    if (isOnline()) connection!.send({ t: 'hull', id });
  };
  const selectWeapon = (id: string) => {
    storage.set(WEAPON_KEY, id);
    weaponId = id; // сервер подтвердит в снапшоте
    if (isOnline()) connection!.send({ t: 'weapon', id });
  };
  const dev = new DevOverlay(el('dev'), hulls, selectHull, connection?.lag ?? null, weapons, selectWeapon);
  const flight = new FlightHud(el('flight'), () => dev.toggle());

  // Бой: события из снапшотов, эффекты и статистика своих выстрелов для dev-панели.
  const combat = new CombatEvents();
  const fireStats = { shots: 0, hits: 0, chanceSum: 0 };
  let ownDto: ShipDto | null = null;
  let killedBy = '';
  let ownAnchor: FxAnchor = { x: SPAWN.x, y: SPAWN.y, size: hulls.get(prediction.hullId).size };
  const locate = (id: number): FxAnchor | null => {
    if (id === ownId()) return ownAnchor;
    const ship = remote.get(id);
    return ship ? { x: ship.x, y: ship.y, size: ship.size } : null;
  };
  const nameOf = (id: number) => roster.get(id)?.name ?? '?';
  const play = (event: CombatEvent, now: number) => {
    if (event.kind === 'kill') {
      const at = locate(event.kill.id);
      if (at) fx.explosion(at.x, at.y, at.size, now);
      return;
    }
    const shot = event.shot;
    fx.shot(shot, now, locate);
    if (shot.from === ownId()) fire.reloadFrom(now, cooldownTicks(weapons.get(shot.w)) * DT * 1000);
  };

  // За кадр сверяемся только с самым свежим снапшотом; чужим кораблям нужен весь поток.
  let latestSnapshot: SnapshotMsg | null = null;
  if (connection) {
    connection.onWelcome = (message) => {
      hulls.set(message.hulls);
      weapons.set(message.weapons);
      zones.set(message.npcs);
      prediction.resetNet();
      remote.clear();
      combat.clear();
      ownDto = null;
      if (message.resumed) feed.add('Снова на связи — корабль ждал на месте');
      // Сервер после переподключения не помнит, во что мы целились и держим ли атаку.
      if (targetId !== 0) connection.send({ t: 'target', id: targetId });
      if (fire.held) connection.send({ t: 'fire', on: true });
    };
    connection.onConfig = (message) => {
      hulls.set(message.hulls);
      weapons.set(message.weapons);
      zones.set(message.npcs);
    };
    connection.onSnapshot = (message) => {
      const now = performance.now();
      const own = connection.playerId;
      latestSnapshot = message;
      remote.push(message, now);
      for (const event of combat.push(message, own)) play(event, now);
      for (const shot of message.shots ?? []) {
        if (shot.from !== own) continue;
        fireStats.shots++;
        if (shot.hit) fireStats.hits++;
        fireStats.chanceSum += shot.ch;
      }
      for (const kill of message.kills ?? []) {
        feed.add(describeKill(nameOf(kill.by), nameOf(kill.id)));
        if (kill.id === own) killedBy = nameOf(kill.by);
      }
    };
    connection.onRosterEvents = (events) => feed.push(events);
    connection.connect();
  } else {
    pilotForm.show(null, playerName());
  }

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
  let wasDead = false;
  app.ticker.add(() => {
    const now = performance.now();
    const frameSeconds = Math.min(0.1, (now - lastFrame) / 1000);
    lastFrame = now;

    const online = isOnline();
    if (wasOnline && !online) {
      prediction.resetNet(); // тормозим локально, при подключении примем состояние сервера
      remote.clear();
      combat.clear();
      ownDto = null;
    }
    wasOnline = online;
    if (latestSnapshot && online) {
      const own = latestSnapshot.ships.find((ship) => ship.id === connection!.playerId);
      if (own) {
        prediction.reconcile(own);
        ownDto = own;
        weaponId = own.w;
      }
    }
    latestSnapshot = null;

    // Уничтожены: стик в центр и огонь отпущен — после респауна корабль не улетит и не начнёт палить сам.
    const dead = prediction.isDead;
    if (dead && !wasDead) {
      stick.reset();
      controls.setThrottle(0);
      fire.release();
    }
    if (!dead && wasDead) killedBy = '';
    wasDead = dead;

    const alpha = loop.advance(now);
    const state = prediction.render(alpha, frameSeconds);
    const hull = hulls.get(prediction.hullId);
    const input = flightInput();
    const desired = input.throttle > 0 && controls.source === 'stick' ? directionAngle(input.dx, input.dy) : null;
    ownShip.view.visible = !dead;
    ownShip.update(state.x, state.y, state.rot, hull, engineGlow(prediction.curr, input.throttle, hull), desired);
    ownAnchor = { x: state.x, y: state.y, size: hull.size };

    remote.update(now, online ? connection!.playerId : -1);
    for (const event of combat.take(remote.renderTick)) play(event, now);

    // Цель ушла из системы (вышла, сервер перезапустился) — снимаем.
    if (targetId !== 0 && remote.inLatest(targetId) === false) setTarget(0);
    const target = targetId !== 0 ? remote.get(targetId) : undefined;
    const weapon = weapons.get(weaponId);
    const aim = target ? assess(state, weapon, target, hulls.get(target.hull)) : null;
    const tick = connection?.lastTick ?? 0;
    const protectedSeconds = ownDto?.pu ? Math.max(0, (ownDto.pu - tick) * DT) : 0;
    const me = ownId();
    let attackers = 0;
    for (const ship of remote.visible()) if (ship.kind === 'pirate' && ship.targetId === me) attackers++;

    camera.follow(state.x, state.y, zoom.value).apply(world, app.screen.width, app.screen.height);
    starfield.update(camera.x, camera.y, app.screen.width, app.screen.height);
    weaponArc.update(state.x, state.y, state.rot, target && !dead ? weapon : null, aim?.state === 'ready');
    overlay.update(
      remote.visible(),
      camera,
      app.screen.width,
      app.screen.height,
      target && aim ? { id: target.id, state: aim.state } : null,
      { x: state.x, y: state.y, size: hull.size, protected: !dead && protectedSeconds > 0 },
      me,
      dev.visible,
    );
    fx.update(now, camera.zoom, locate);
    fire.render(now, !target ? 'none' : aim?.state === 'ready' ? 'ready' : 'blocked');

    combatHud.update(
      ownDto && online ? { hp: ownDto.hp, maxHp: hull.hp, sh: ownDto.sh, maxSh: hull.shield, protectedSeconds, attackers } : null,
      target && aim
        ? {
            name: target.name,
            hullName: hulls.get(target.hull).name,
            hp: target.hp,
            maxHp: target.maxHp,
            sh: target.sh,
            maxSh: target.maxSh,
            aim,
          }
        : null,
      dead && ownDto?.rt ? { by: killedBy, seconds: Math.max(0, (ownDto.rt - tick) * DT) } : null,
    );

    const speed = Math.hypot(prediction.curr.vx, prediction.curr.vy);
    const velocity = localVelocity(prediction.curr);
    status.update(connection, app.ticker.FPS, isBraking() && speed > STOPPED_SPEED);
    flight.update(hull.name, speed, input.throttle);
    dev.update({
      tick,
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
      weaponId,
      interpMs: remote.clock.delayMs,
      jitterMs: remote.clock.jitterMs,
      extrapolations: remote.extrapolations,
      shots: fireStats.shots,
      hits: fireStats.hits,
      chanceSum: fireStats.chanceSum,
    });
  });
}

/** Угол в градусах по компасу экрана: 0 — вверх, 90 — вправо. */
function toCompass(angle: number): number {
  return ((angle * 180) / Math.PI + 360) % 360;
}

main();
