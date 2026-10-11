import type { Application } from 'pixi.js';
import type { MechMessage } from '../net/connection';
import type { ClientMessage, MechEndMsg, MechEvent } from '../net/protocol';
import { createApp } from '../render/app';
import { formatCredits } from '../sim/shop';
import type { ConfirmLines } from '../ui/confirm';
import { loadMechArt } from './rig';
import {
  DEFAULT_RULES,
  MechField,
  PARTS,
  alive,
  armItem,
  forecast,
  gunOf,
  packEffect,
  reachOf,
  unitDef,
  type MechBattleView,
  type MechPart,
  type MechRules,
  type MechUnitView,
} from './rules';
import { MechScene } from './scene';
import {partName} from './workshop';
import {
  PART_LABEL,
  SIDE_LABEL,
  cells,
  endText,
  endTitle,
  forecastBlocker,
  objectiveLine,
  refusalText,
  roundChip,
} from './text';

export type MechActMessage = Extract<ClientMessage, { t: 'mechAct' }>;

/** Звуки боя: экран не знает про движок звука, main.ts переводит их в свои. */
export type MechSound =
  /** Выстрел и следом его удар: hit — попал, block — принял щит, atMe — стреляли по мне. */
  | { kind: 'shot'; sound: string; mine: boolean; hit: boolean; block: boolean; atMe: boolean }
  | { kind: 'down'; mine: boolean };

export interface MechHandlers {
  send(message: MechActMessage): void;
  confirm(lines: ConfirmLines, yes: () => void): void;
  /** Экран закрыт кнопкой «В док». */
  closed?(): void;
  sound?(sound: MechSound): void;
}

const TOAST_MS = 2600;

/**
 * Экран наземного боя (M21): своё Pixi-приложение поверх дока и DOM-панели по принятому концепту — слева свой
 * мех, справа цель и прогноз, снизу поворот и «Завершить ход». Сообщения сервера проигрываются по очереди:
 * сначала анимация событий хода, потом новое состояние, — поэтому ввод заперт, пока очередь не пуста.
 */
export class MechScreen {
  private app: Application | null = null;
  private scene: MechScene | null = null;
  private rules: MechRules = DEFAULT_RULES;
  private state: MechBattleView | null = null;
  private chain: Promise<void> = Promise.resolve();
  private waiting = false;
  private online = true;
  private session: string | undefined;
  private savePending = false;
  private revision = 0;
  private yourAccount: string | null=null;
  private deadline: string | null=null;
  private hand: string | null = null;
  private playing = false;
  private ended: MechEndMsg | null = null;
  private selected: string | null = null;
  private aimed = false;
  private part: MechPart | null = null;
  private preview: number | null = null;
  private pendingDir: number | null = null;
  private map: string[] = [];
  private toastTimer = 0;

  private readonly canvasHost: HTMLElement;
  private readonly head: HTMLElement;
  private readonly own: HTMLElement;
  private readonly target: HTMLElement;
  private readonly tray: HTMLElement;
  private readonly toast: HTMLElement;
  private readonly endCard: HTMLElement;

  constructor(
    private readonly root: HTMLElement,
    private readonly handlers: MechHandlers,
  ) {
    root.classList.add('mech');
    root.hidden = true;
    this.canvasHost = div('mech-canvas');
    this.head = div('mech-head sro-pane');
    this.own = div('mech-own sro-pane');
    this.target = div('mech-target sro-pane');
    this.target.hidden = true;
    this.tray = div('mech-tray');
    this.toast = div('mech-toast sro-pane sro-pane--warn');
    this.toast.hidden = true;
    this.endCard = div('mech-end sro-pane sro-dialog');
    this.endCard.hidden = true;
    root.append(this.canvasHost, this.head, this.own, this.target, this.tray, this.toast, this.endCard);
    window.addEventListener('keydown', (e) => {
      if (!this.open || e.key !== 'Escape') return;
      this.select(null);
    });
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  /** Идёт бой: музыке — боевая тема. */
  get inCombat(): boolean {
    return this.open && this.ended === null;
  }

  /** Сообщение сервера — в очередь: анимация предыдущего хода должна доиграть. */
  apply(message: MechMessage): void {
    if (message.t === 'mechRefused') {
      if (message.code === 'saveFailed') this.savePending = true;
      this.waiting = false;
      this.say(refusalText(message.code));
      this.render();
      return;
    }
    this.chain = this.chain
      .then(() => this.handle(message))
      .catch((e) => console.error('mech message failed', e));
  }

  close(): void {
    this.root.hidden = true;
    this.app?.ticker.stop();
    this.state = null;
    this.ended = null;
    this.select(null);
    this.handlers.closed?.();
  }

  private async handle(message: Exclude<MechMessage, { t: 'mechRefused' }>): Promise<void> {
    switch (message.t) {
      case 'mechState':
        if (message.session === this.session && (message.revision ?? 0) < this.revision) return;
        if (message.session !== this.session) this.savePending = false;
        this.session = message.session;
        this.revision = message.revision ?? 0;
        this.yourAccount=message.yourAccount??null;
        this.root.dataset.territory=String(!!this.yourAccount);
        this.deadline=message.deadline??null;
        await this.show();
        if (message.rules) this.rules = message.rules;
        this.setState(message.battle);
        if (!gunOf(this.rules, this.me()!, this.hand)) this.hand = null;
        return;
      case 'mechEvents':
        this.playing = true;
        this.render();
        try {
          await this.play(message.events);
        } finally {
          this.playing = false;
        }
        return;
      case 'mechEnd':
        if (message.result && this.session && message.result.session !== this.session) return;
        this.ended = message;
        this.savePending = false;
        this.waiting = false;
        this.select(null);
        this.showEnd(message);
        this.render();
        return;
    }
  }

  private async show(): Promise<void> {
    if (this.root.hidden) {
      this.root.hidden = false;
      this.ended = null;
      this.endCard.hidden = true;
    }
    if (!this.app) {
      this.app = await createApp(this.canvasHost);
      this.canvasHost.appendChild(this.app.canvas);
      this.scene = new MechScene(this.app);
      this.scene.onCell = (x, y) => this.tap(x, y);
      this.scene.onHover = (x, y) => this.hover(x, y);
      await loadMechArt();
    }
    this.app.ticker.start();
  }

  private setState(state: MechBattleView): void {
    const scene = this.scene!;
    const first = this.state === null;
    const mapChanged = state.map.join('\n') !== this.map.join('\n');
    this.state = state;
    this.waiting = false;
    this.preview = null;
    this.pendingDir = null;
    if (mapChanged) {
      this.map = [...state.map];
      scene.setMap(state.map,state.biome??'desert');
    }
    scene.setUnits(state.units,this.rules);
    scene.setObjective(this.rules.missions?.[state.mission],state);
    if (first || mapChanged) {
      scene.fit(coarse() ? 46 : 56);
      const me = this.me();
      if (me) scene.centerOn(me.x, me.y);
    }
    if (this.selected && !state.units.some((u) => u.id === this.selected && alive(u))) this.select(null);
    this.render();
  }

  // ------------------------------------------------------------------ события хода

  private async play(events: readonly MechEvent[]): Promise<void> {
    const scene = this.scene!;
    for (let i = 0; i < events.length; i++) {
      const e = events[i];
      const next = events[i + 1];
      const mine = this.sideOf(e.unit) === 'player';
      switch (e.kind) {
        case 'move':
          await scene.walk(e.unit, e.path ?? [], e.dir);
          break;
        case 'face':
          scene.face(e.unit, e.dir);
          await sleep(140);
          break;
        case 'shot': {
          scene.face(e.unit, e.dir);
          const sound = (e.weapon && this.rules.weapons?.[e.weapon]?.sound) ?? 'bolt';
          const atMe = this.sideOf(e.target ?? '') === 'player';
          this.handlers.sound?.({ kind: 'shot', sound, mine, hit: next?.kind === 'hit', block: next?.kind === 'block', atMe });
          await scene.tracer(e.unit, e.target ?? '', next?.kind === 'miss');
          break;
        }
        case 'miss':
          void scene.float(e.target ?? '', 'Промах', 'miss');
          await sleep(420);
          break;
        case 'skill':
        case 'skillFailed':
          this.say(`${this.rules.skills?.[e.weapon ?? '']?.name ?? e.weapon}: ${e.kind === 'skill' ? 'сработал' : 'движение потрачено'}`, false);
          await sleep(180); break;
        case 'repair':
          if (e.part) { const u=this.state?.units.find(u=>u.id===e.unit); if (u) {const i=PARTS.indexOf(e.part);u.hp[i]=Math.min(u.max[i],u.hp[i]+e.dmg);this.scene?.wound(u.id,e.part,u.hp[i]);} }
          this.say(`Ремонт +${e.dmg} HP`, false); break;
        case 'block':
          this.hurt(e);
          void scene.float(e.unit, `Блок щитом −${e.dmg}`, 'block');
          await sleep(420);
          break;
        case 'hit':
          this.hurt(e);
          void scene.float(e.unit, `−${e.dmg} · ${PART_LABEL[e.part ?? 'body']}`, 'hit');
          await sleep(420);
          break;
        case 'partDown':
          void scene.float(e.unit, `${PART_LABEL[e.part ?? 'body']} выведена`, 'info');
          await sleep(360);
          break;
        case 'mechDown':
          this.handlers.sound?.({ kind: 'down', mine });
          scene.wound(e.unit, 'body', 0);
          void scene.float(e.unit, 'Уничтожен', 'info');
          await sleep(600);
          break;
        case 'round':
          this.say(`Раунд ${e.n}`, false);
          break;
      }
    }
  }

  /** Урон части — и в сцену (спрятать руку), и в состояние (полоски), не дожидаясь следующего mechState. */
  private hurt(e: MechEvent): void {
    const unit = this.scene?.unitOf(e.unit);
    if (!unit || !e.part) return;
    const i = PARTS.indexOf(e.part);
    const hp = Math.max(0, unit.hp[i] - e.dmg);
    this.scene!.wound(e.unit, e.part, hp);
    const u = this.state?.units.find((x) => x.id === e.unit);
    if (u) u.hp[i] = hp;
    this.render();
  }

  // ------------------------------------------------------------------ ввод

  private get interactive(): boolean {
    const s = this.state;
    return this.online && !!s && this.open && s.turn === 'player' && !s.winner && !this.waiting && !this.playing && this.ended === null
      && (!this.yourAccount || s.units.find(u=>u.id===s.current)?.accountOwner===this.yourAccount);
  }

  private tap(x: number, y: number): void {
    const s = this.state;
    if (!s || this.ended) return;
    const field = new MechField(s.map);
    const cell = field.index(x, y);
    const unit = s.units.find((u) => alive(u) && u.x === x && u.y === y);
    if (unit?.side === 'enemy') {
      this.select(this.selected === unit.id ? null : unit.id);
      return;
    }
    if (unit) {
      if (unit.side === 'player' && unit.control !== 'ai' && unit.control !== 'objective' && !unit.activated && this.interactive && !s.moved)
        this.send({t:'mechAct',act:'select',unit:unit.id});
      this.select(null);
      this.preview = null;
      this.render();
      return;
    }
    if (!this.interactive) return;
    const reach = reachOf(s, true);
    if (!reach.has(cell)) {
      this.preview = null;
      this.render();
      return;
    }
    // Телефон: первый тап — предпросмотр пути, второй — ход. Мышь: путь видно наведением, клик — ход.
    if (this.preview === cell || !coarse()) {
      if (!reachOf(s).has(cell)) this.handlers.confirm({title:'Нужен рывок / прыжок',text:'При неудаче движение будет потрачено. Атаковать после попытки можно.',yes:'Попробовать',no:'Отмена'},()=>this.send({t:'mechAct',act:'move',x,y}));
      else this.send({ t: 'mechAct', act: 'move', x, y });
      return;
    }
    this.preview = cell;
    this.render();
  }

  private hover(x: number, y: number): void {
    const s = this.state;
    if (!s || !this.interactive) return;
    const cell = new MechField(s.map).index(x, y);
    const next = reachOf(s,true).has(cell) ? cell : null;
    if (next === this.preview) return;
    this.preview = next;
    this.renderMarks();
  }

  /** Выбрать цель (null — снять); прицельный режим сбрасывается. Снаружи зовёт только витрина. */
  select(id: string | null): void {
    this.selected = id;
    this.aimed = false;
    this.part = null;
    // Телефон: лист цели закроет низ экрана — стрелка и цель поднимаем в верхнюю треть.
    const me = this.me();
    const target = this.state?.units.find((u) => u.id === id);
    if (me && target && coarse()) this.scene?.centerOn((me.x + target.x) / 2, (me.y + target.y) / 2, 0.36);
    this.render();
  }

  private send(message: MechActMessage): void {
    if (!this.online) return;
    this.waiting = true;
    this.preview = null;
    this.handlers.send({...message,session:this.session,revision:this.revision,unit:message.unit ?? this.state?.current ?? undefined,request:crypto.randomUUID()});
    this.render();
  }

  private attack(): void {
    if (!this.selected) return;
    this.send({ t: 'mechAct', act: 'attack', target: this.selected, part: this.aimed && this.part ? this.part : undefined, hand:this.hand ?? undefined });
  }

  private endTurn(): void {
    const me = this.me();
    const dir = this.pendingDir ?? undefined;
    this.send({ t: 'mechAct', act: 'end', dir: me && dir !== me.dir ? dir : undefined });
  }

  private turn(step: number): void {
    const me = this.me();
    if (!me || !this.interactive) return;
    this.pendingDir = ((this.pendingDir ?? me.dir) + step + 8) % 8;
    this.scene?.face(me.id, this.pendingDir);
    this.render();
  }

  private quit(): void {
    this.handlers.confirm(
      { title: 'Отступить?', text: this.yourAccount ? 'Поражение уменьшает контроль. Обслуживание машин потребуется после боя. В совместном бою второй игрок должен подтвердить отступление.' : 'Связь с машиной оборвётся — это поражение. Повтор бесплатный.', yes: 'Отступить', no: 'Остаться' },
      () => this.send({ t: 'mechAct', act: 'quit' }),
    );
  }

  // ------------------------------------------------------------------ отрисовка

  private owns(unit: MechUnitView | null): boolean {
    return !!unit && (!this.yourAccount || unit.accountOwner === this.yourAccount);
  }

  private me(): MechUnitView | null {
    const s = this.state;
    if (!s) return null;
    return s.units.find((u) => u.id === s.current && u.side === 'player') ?? s.units.find((u) => u.side === 'player') ?? null;
  }

  private sideOf(id: string): 'player' | 'enemy' | null {
    return this.state?.units.find((u) => u.id === id)?.side ?? null;
  }

  private render(): void {
    if (!this.state) return;
    this.renderHead();
    this.renderOwn();
    this.renderTarget();
    this.renderTray();
    this.renderMarks();
  }

  private renderMarks(): void {
    const s = this.state;
    if (!s || !this.scene) return;
    const interactive = this.interactive;
    const reach = interactive ? reachOf(s,true) : new Map<number, number>();
    const me = this.me();
    const field = new MechField(s.map);
    const path = interactive && me && this.preview !== null
      ? field.path(me.x, me.y, this.preview % field.width, Math.floor(this.preview / field.width), s.moveRange+(me.dashRange ?? 0), occupiedExcept(s, me)) ?? [this.preview]
      : null;
    const targets = new Set<string>();
    if (me && interactive) {
      for (const u of s.units) if (u.side === 'enemy' && alive(u) && forecast(this.rules, s, me, u, null, this.hand).ok) targets.add(u.id);
    }
    const selected = s.units.find((u) => u.id === this.selected);
    const line = me && selected ? { from: me.id, to: selected.id, ok: forecast(this.rules, s, me, selected, null, this.hand).ok } : null;
    this.scene.setMarks({ reach, path, targets, selected: this.selected, line, current: s.turn === 'player' ? (s.current ?? null) : null });
  }

  private renderHead(): void {
    const s = this.state!;
    const mission = this.rules.missions?.[s.mission];
    const enemies = s.units.filter((u) => u.side === 'enemy');
    const killed = enemies.filter((u) => !alive(u)).length;
    const object = s.units.find(u=>u.id===s.objectiveUnit);
    this.head.replaceChildren(
      el('div', 'mech-head-title', [el('span', 'sro-label', 'Наземная операция'), el('strong', '', mission?.name ?? 'Вылазка')]),
      el('span', 'mech-chip sro-pane sro-pane--pill', this.yourAccount && s.turn === 'player' && !this.owns(this.me()!) ? `Раунд ${s.round} · ход напарника` : roundChip(s.round, s.turn, !!s.winner || this.ended !== null)),
      el('span', 'mech-goal', objectiveLine(s.goal ?? mission?.goal ?? 'Уничтожить противников', mission?.objective?.kind && mission.objective.kind !== 'eliminate' ? s.progress ?? 0 : killed, mission?.objective?.kind && mission.objective.kind !== 'eliminate' ? s.required ?? 0 : enemies.length)+(object ? ` · ${object.name}: ${object.hp[0]}/${object.max[0]} HP` : '')),
      el('div', 'mech-head-actions', [
        button('−', 'sro-btn sro-btn--icon sro-btn--ghost', () => this.scene?.zoomBy(1 / 1.2), 'Отдалить'),
        button('+', 'sro-btn sro-btn--icon sro-btn--ghost', () => this.scene?.zoomBy(1.2), 'Приблизить'),
        button('Отступить', 'sro-btn sro-btn--sm sro-btn--danger', () => this.quit(), undefined, this.ended !== null || this.savePending || !!s.winner),
      ]),
    );
    if(this.deadline&&!s.winner) {
      const timer=el('span','mech-timer sro-num');
      const update=()=>{const left=Math.max(0,Math.ceil((new Date(this.deadline!).getTime()-Date.now())/1000));timer.textContent=`Активация: ${left} с${left<=15?' · завершите ход':''}`;timer.classList.toggle('sro-warn',left<=15);};
      update();const interval=window.setInterval(()=>{if(!timer.isConnected){clearInterval(interval);return;}update();},1000);this.head.append(timer);
    }
    if(this.yourAccount&&!this.interactive&&!s.winner)this.head.append(el('span','sro-muted','Сейчас управляет напарник'));
    this.root.style.setProperty('--mech-head-height', `${this.head.getBoundingClientRect().height}px`);
  }

  private renderOwn(): void {
    const s = this.state!;
    const me = this.me();
    if (!me) {
      this.own.replaceChildren();
      return;
    }
    const def = unitDef(this.rules, me);
    const moving = s.turn === 'player' && s.current === me.id
      ? (s.moved ? 'Уже переместился' : `Ход: ${cells(s.moveRange)}`)
      : 'Ждёт своего хода';
    this.own.replaceChildren(
      el('div', 'mech-panel-head', [el('span', 'sro-label', this.owns(me) ? 'Ваш мех' : 'Мех напарника'), el('strong', '', me.name)]),
      partBars(me, null),
      el('div', 'mech-gear', [
        gearRow('Левая', armItem(this.rules, def?.left)?.name ?? '—', me.hp[1] <= 0 && me.max[1] > 0),
        gearRow('Правая', armItem(this.rules, def?.right)?.name ?? '—', me.hp[2] <= 0 && me.max[2] > 0),
      ]),
      el('div', 'mech-move sro-num', moving),
    );
    this.own.append(el('div','mech-squad', s.units.filter(u=>u.side==='player').map(u=>button(
      `${u.name} · ${u.control === 'ai' ? 'союзник · ИИ' : u.control === 'objective' ? 'цель миссии' : this.owns(u) ? 'ваш' : 'напарника'}${u.activated ? ' · ход использован' : ''}`,
      'sro-btn sro-btn--xs',()=>this.send({t:'mechAct',act:'select',unit:u.id}),undefined,
      !this.interactive || s.moved || !alive(u) || u.activated || u.control==='ai' || u.control==='objective' || !!this.yourAccount&&u.accountOwner!==this.yourAccount))));
    this.own.append(el('div','sro-muted',`Навыки: ${(me.skills ?? []).map(id=>this.rules.skills?.[id]?.name ?? id).join(', ') || 'нет'}`));
  }

  private renderTarget(): void {
    const s = this.state!;
    const target = s.units.find((u) => u.id === this.selected);
    const me = this.me();
    this.target.hidden = !target || this.ended !== null;
    if (!target || !me) return;
    const f = forecast(this.rules, s, me, target, this.aimed ? this.part : null, this.hand);
    const myTurn = this.interactive && s.current === me.id;
    const canFire = myTurn && f.ok && (!this.aimed || this.part !== null);
    const blocker = !myTurn ? (s.turn === 'enemy' ? 'Ход противника' : null)
      : forecastBlocker(f.refusal) ?? (this.aimed && !this.part ? 'Выберите часть' : null);
    const segment = (label: string, on: boolean, click: () => void) => {
      const b = button(label, 'sro-btn sro-btn--xs mech-seg', click);
      b.dataset.active = String(on);
      return b;
    };
    const parts = this.aimed
      ? el('div', 'mech-parts', PARTS.map((p, i) => {
        const b = button(PART_LABEL[p], 'sro-btn sro-btn--xs mech-seg', () => {
          this.part = p;
          this.render();
        }, undefined, target.hp[i] <= 0);
        b.dataset.active = String(this.part === p);
        return b;
      }))
      : null;
    const stat = (label: string, value: string, strong = false) =>
      el('div', 'mech-stat', [el('span', 'sro-muted', label), el(strong ? 'strong' : 'span', 'sro-num', value)]);
    this.target.replaceChildren(
      el('div', 'mech-panel-head', [
        el('span', 'sro-label', 'Цель'),
        el('strong', '', target.name),
        button('✕', 'sro-btn sro-btn--xs sro-btn--ghost mech-close', () => this.select(null), 'Снять цель'),
      ]),
      partBars(target, this.aimed ? this.part : null),
      el('div', 'mech-mode', [
        segment('Обычный', !this.aimed, () => {
          this.aimed = false;
          this.part = null;
          this.render();
        }),
        segment('Прицельный', this.aimed, () => {
          this.aimed = true;
          this.render();
        }),
      ]),
      ...(parts ? [parts] : []),
      el('div', 'mech-stats', [
        stat('Попадание', f.weapon ? `${f.chance} %` : '—', true),
        stat('Урон', f.weapon ? `${f.min}–${f.max}` : '—', true),
        stat('Дистанция', cells(f.distance)),
        stat('Сторона', SIDE_LABEL[f.side]),
        stat('Щит', f.block > 0 ? `блок ${f.block} %` : 'не прикрывает'),
      ]),
      ...(blocker ? [el('div', 'mech-blocker sro-warn', blocker)] : []),
      el('div', 'mech-actions', [
        button('Подтвердить атаку', 'sro-btn sro-btn--primary', () => this.attack(), undefined, !canFire),
        button('Отмена', 'sro-btn sro-btn--ghost', () => this.select(null)),
      ]),
    );
    const hands = el('div','mech-mode');
    for (const hand of ['left','right']) {
      const gun = gunOf(this.rules,me,hand);
      if (!gun) continue;
      const b=button(`${hand==='left'?'Левая':'Правая'} · ${gun.weapon.name}`,'sro-btn sro-btn--xs',()=>{this.hand=hand;this.render();});
      b.setAttribute('aria-pressed',String(this.hand===hand));hands.append(b);
    }
    this.target.prepend(hands);
    if ((me.skills ?? []).length) this.target.append(el('p','sro-muted','Прогноз — обычная атака. Автоматические навыки могут изменить её результат.'));
  }

  private renderTray(): void {
    const s = this.state!;
    const me = this.me();
    const can = this.interactive && !!me && s.current === me.id;
    const gun = me ? gunOf(this.rules, me) : null;
    const hint = !can ? (this.playing || this.waiting ? 'Идёт ход…' : s.turn === 'enemy' ? 'Ход противника' : '')
      : s.moved ? 'Атакуйте или завершите ход'
      : gun ? 'Клетка — перемещение, враг — атака' : 'Оружие разбито: только перемещение';
    this.tray.replaceChildren(
      el('span', 'mech-hint sro-muted', hint),
      el('div', 'mech-tray-buttons', [
        button('↺', 'sro-btn sro-btn--icon', () => this.turn(-1), 'Повернуть против часовой', !can),
        button('↻', 'sro-btn sro-btn--icon', () => this.turn(1), 'Повернуть по часовой', !can),
        button('Завершить ход', 'sro-btn sro-btn--primary mech-end-turn', () => this.endTurn(), undefined, !can),
      ]),
    );
    if (me && packEffect(this.rules,me,'repair') > 0 && !me.repaired) for (const [i,part] of PARTS.entries())
      if (me.hp[i]>0 && me.hp[i]<me.max[i]) this.tray.append(button(`Ремонт: ${PART_LABEL[part]}`,'sro-btn sro-btn--xs',()=>this.send({t:'mechAct',act:'repair',part}),undefined,!can));
    if (this.savePending) this.tray.append(button('Повторить сохранение итога','sro-btn sro-btn--primary',()=>this.send({t:'mechAct',act:'end',unit:this.state?.units.find(u=>u.control==='player')?.id}),undefined,!this.online || this.waiting || this.playing));
    if(s.actions?.includes('interact'))this.tray.append(button(s.interacted?'Объект открыт — эвакуируйтесь':'Взаимодействовать с объектом','sro-btn',()=>this.send({t:'mechAct',act:'interact'}),undefined,!can||!!s.interacted));
  }

  private showEnd(end: MechEndMsg): void {
    this.endCard.hidden = false;
    this.endCard.dataset.won = String(end.won);
    this.endCard.replaceChildren(
      el('div', 'sro-dialog__title', end.result && end.won ? 'Операция завершена' : endTitle(end.won)),
      el('p', 'sro-dialog__text', end.result?.territory ? `${end.won?'Победа':'Поражение'} · награда ${formatCredits(end.reward)}.` : end.result && end.won ? `${this.rules.missions?.[end.result.mission]?.name ?? 'Операция'}: победа. ${end.first ? `Награда: ${formatCredits(end.reward)}.` : 'Первая награда уже получена.'}` : endText(end.won, end.reward, end.first, formatCredits(end.reward), end.training)),
      el('div', 'sro-dialog__actions', [
        ...end.result?.territory?[]:[button('Ещё раз', 'sro-btn', () => {
          this.endCard.hidden = true;
          this.ended = null;
          this.state = null;
          this.handlers.send({ t: 'mechAct', act: end.training ? 'train' : 'start',mission:end.result?.mission,
            squad: end.result?.experience.map(e=>e.id), request:crypto.randomUUID() });
        })],
        button('В док', 'sro-btn sro-btn--primary', () => this.close()),
      ]),
    );
    this.endCard.append(el('p','sro-muted',end.result?.territory?'HP машин восстановлены. Перед следующей вылазкой погасите долг обслуживания в мастерской.':'Все машины и детали восстановлены бесплатно.'));
    if(end.result?.territory) {
      const t=end.result.territory;
      this.endCard.append(el('p','',`${t.objectName}: контроль ${t.oldControl} → ${t.control} · ${t.owner==='resistance'?'Сопротивление':'Корпорация'} · ваш вклад +${t.contribution}`));
      for(const [id,debt] of Object.entries(t.debts))this.endCard.append(el('p','',`${end.result.experience.find(e=>e.id===id)?.name??'Машина'}: долг ${debt.titanium} титана + ${debt.energy} энергии`));
    }
    if (end.result) {
      if (!end.won) this.endCard.append(el('p','sro-warn', ({retreat:'Отступление',squadDestroyed:'Отряд уничтожен',tractorDestroyed:'Тягач уничтожен',objectDestroyed:'Объект уничтожен'} as Record<string,string>)[end.result.reason] ?? end.result.reason));
      if (end.result.parts.length) this.endCard.append(el('p','',`Детали: ${end.result.parts.map(key=>{const [category,part]=key.split(':');return partName(this.rules,{category,part});}).join(', ')}`));
      for (const xp of end.result.experience) this.endCard.append(el('p','',`${xp.name}: +${xp.gained} XP · уровень ${xp.level} · ${xp.xp} XP${xp.newSkills.length ? ` · Новый навык: ${xp.newSkills.map(id=>this.rules.skills?.[id]?.name ?? id).join(', ')}` : ''}`));
    }
  }

  setOnline(online: boolean): void {
    if (online === this.online) return;
    this.online=online;
    if (!online && this.open) this.say('Связь потеряна. Восстанавливаем состояние сервера…');
    this.render();
  }

  private say(text: string, warn = true): void {
    this.toast.textContent = text;
    this.toast.classList.toggle('sro-pane--warn', warn);
    this.toast.hidden = false;
    window.clearTimeout(this.toastTimer);
    this.toastTimer = window.setTimeout(() => (this.toast.hidden = true), TOAST_MS);
  }
}

// ------------------------------------------------------------------ DOM

function div(className: string): HTMLDivElement {
  const d = document.createElement('div');
  d.className = className;
  return d;
}

function el(tag: string, className: string, content?: string | Node[]): HTMLElement {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (typeof content === 'string') e.textContent = content;
  else if (content) e.append(...content);
  return e;
}

function button(text: string, className: string, click: () => void, title?: string, disabled = false): HTMLButtonElement {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = className;
  b.textContent = text;
  if (title) {
    b.title = title;
    b.setAttribute('aria-label', title);
  }
  b.disabled = disabled;
  b.addEventListener('click', click);
  return b;
}

function partBars(u: MechUnitView, chosen: MechPart | null): HTMLElement {
  return el('div', 'mech-bars', PARTS.flatMap((p, i) => {
    if (u.max[i] <= 0) return [];
    const hp = Math.max(0, u.hp[i]);
    const row = el('div', 'mech-bar', [
      el('div', 'sro-bar-row', [el('span', '', PART_LABEL[p]), el('span', 'sro-num', hp > 0 ? `${hp} / ${u.max[i]}` : 'выведено')]),
      el('div', 'sro-bar sro-bar--thin sro-bar--hull', [fill(hp / u.max[i])]),
    ]);
    row.dataset.part = p;
    row.dataset.chosen = String(chosen === p);
    row.dataset.down = String(hp <= 0);
    return [row];
  }));
}

function fill(k: number): HTMLElement {
  const f = el('div', 'sro-bar__fill');
  f.style.width = `${Math.round(Math.max(0, Math.min(1, k)) * 100)}%`;
  return f;
}

function gearRow(side: string, name: string, down: boolean): HTMLElement {
  const row = el('div', 'mech-gear-row', [el('span', 'sro-muted', side), el('span', down ? 'sro-danger' : '', down ? `${name} · разбито` : name)]);
  return row;
}

function occupiedExcept(s: MechBattleView, me: MechUnitView): Set<number> {
  const width = s.map[0]?.length ?? 0;
  return new Set(s.units.filter((u) => alive(u) && u !== me).map((u) => u.y * width + u.x));
}

function coarse(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches;
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}
