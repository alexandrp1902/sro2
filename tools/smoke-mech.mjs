// Сквозная проверка мастерской и четырёх наземных миссий (M22) без браузера: пилот долетает до Нова-Прайм, садится на неё,
// где кончилась «Тихая война», и через ретранслятор начинает «Первую вылазку». Проверяется, что из дока
// посреди боя не улететь, что мусорная команда отвергается кодом, что после обрыва связи бой ждёт и продолжается
// с того же места, и что бой доигрывается до конца — а награда платится ровно та, что объявлена.
//
// Сервер нужно поднять с проставленной кампанией, иначе ретранслятора не будет:
//   $env:SRO_STORY_SKIP='quietWar'; dotnet run --project server/Sro.Server
// Нужен Node 24 (встроенный WebSocket). Заводит аккаунт smk-mech-<число>. Идёт 1–3 минуты.
//   node tools/smoke-mech.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, orbitAt } from './wire.mjs';
import { mechPlan } from './mech-bot.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(10000 + Math.random() * 90000);
const PASSWORD = 'smoke-password';
const ROUTE = ['vega', 'nova'];
const PRIME = 'pl:novaPrime';
/** Больше ходов бою не нужно: план обещает бой до пяти минут, это раундов десять-пятнадцать. */
const MAX_TURNS = 80;

class Client {
  constructor(hello) {
    this.hello = hello;
    this.welcome = null;
    this.hangar = null;
    this.cargo = null;
    this.missions = null;
    this.dialogs = [];
    this.notices = [];
    this.snapshots = [];
    this.mech = null;
    this.rules = null;
    this.mechStates = 0;
    this.session = null; this.revision = 0; this.workshop = null; this.workshopStates = 0;
    this.refusals = [];
    this.end = null;
    this.mechEnds = 0;
    this.seq = 0;
    this.timer = 0;
    this.steer = () => [0, -1, 0];
    this.listeners = new Set();
  }

  get id() {
    return this.welcome.id;
  }

  get snapshot() {
    return this.snapshots[this.snapshots.length - 1];
  }

  get me() {
    return this.snapshot?.ships.find((s) => s.id === this.id);
  }

  get story() {
    return this.missions?.story ?? null;
  }

  connect() {
    return new Promise((resolve, reject) => {
      const { ws, read } = openSocket(url);
      this.ws = ws;
      this.seq = 0;
      ws.onopen = () => this.send({ t: 'hello', ...this.hello });
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = () => clearInterval(this.timer);
      ws.onmessage = (e) => {
        const message = read(e.data);
        switch (message.t) {
          case 'welcome':
            this.welcome = message;
            this.snapshots = [];
            this.seq = 0;
            resolve(message);
            break;
          case 'hangar':
            if (this.hangar?.docked && !message.docked) this.seq = 0;
            this.hangar = message;
            break;
          case 'cargo': this.cargo = message; break;
          case 'missions': this.missions = message; break;
          case 'dialog': this.dialogs.push(message); break;
          case 'notice': this.notices.push(message.code); break;
          case 'denied': reject(new Error(`denied: ${message.code}`)); break;
          case 'workshopState': this.workshop = message; this.workshopStates++; break;
          case 'mechState':
            this.session=message.session;this.revision=message.revision;
            this.mech = message.battle;
            if (message.rules) this.rules = message.rules;
            this.mechStates++;
            break;
          case 'mechRefused': this.refusals.push(message.code); break;
          case 'mechEnd':
            this.mechEnds++;
            if(!message.result || message.result.session===this.session)this.end=message;
            break;
          case 'snapshot':
            this.snapshots.push(message);
            if (this.snapshots.length > 2000) this.snapshots.splice(0, 1000);
            break;
        }
        for (const listener of this.listeners) listener();
      };
    });
  }

  send(message) {
    if (this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(message));
  }

  start() {
    this.timer = setInterval(() => {
      if (this.hangar?.docked) return;
      const [dx, dy, th] = this.steer();
      const length = Math.hypot(dx, dy) || 1;
      this.send({ t: 'input', seq: ++this.seq, dx: dx / length, dy: dy / length, th });
    }, INPUT_INTERVAL_MS);
  }

  async flyTo(target, within, timeoutMs) {
    const at = () => (typeof target === 'function' ? target() : target);
    this.steer = () => {
      const me = this.me;
      const to = at();
      if (!me || !to) return [0, -1, 0];
      const via = aroundSun(this.welcome.system, me, to);
      const final = via.x === to.x && via.y === to.y;
      return [via.x - me.x, via.y - me.y, !final || Math.hypot(via.x - me.x, via.y - me.y) > within ? 1 : 0];
    };
    await this.until(
      () => {
        const me = this.me;
        const to = at();
        return me && to && Math.hypot(to.x - me.x, to.y - me.y) <= within && Math.hypot(me.vx, me.vy) < 5;
      },
      timeoutMs,
      'reach the target',
    );
    this.steer = () => [0, -1, 0];
  }

  until(predicate, timeoutMs, what) {
    return new Promise((resolve, reject) => {
      const cleanup = () => {
        clearTimeout(timer);
        this.listeners.delete(check);
      };
      const check = () => {
        if (!predicate()) return;
        cleanup();
        resolve();
      };
      const timer = setTimeout(() => {
        cleanup();
        reject(new Error(`timeout: ${what}`));
      }, timeoutMs);
      this.listeners.add(check);
      check();
    });
  }

  async undock() {
    if (!this.hangar?.docked) return;
    this.send({ t: 'dock', on: false });
    await this.until(() => this.hangar && !this.hangar.docked, 4000, 'out of the dock');
    await this.until(() => this.me, 4000, 'own ship after take-off');
  }

  /** Прыжок; пират по дороге может сбить зарядку (M15.7) — тогда ещё раз, до трёх попыток. */
  async jump(to) {
    const gate = this.welcome.system.gates.find((g) => g.to === to);
    for (let attempt = 1; ; attempt++) {
      await this.flyTo(gate, 120, 120000);
      this.notices.length = 0;
      this.send({ t: 'jump', to });
      try {
        await this.until(() => this.welcome.system.id === to, 12000, `jump to ${to}`);
        break;
      } catch (error) {
        if (attempt >= 3) throw new Error(`${error.message} (notices: ${this.notices.join(', ') || 'none'}; system: ${this.welcome.system.id}; ship: ${JSON.stringify(this.me)}; hangar: ${JSON.stringify(this.hangar)})`);
      }
    }
    await this.until(() => this.me, 4000, 'own ship after the jump');
  }

  /** Сесть на поселение планеты: подлететь на дальность стыковки и попросить именно это место. */
  async land(key) {
    const system = this.welcome.system;
    const planet = (system.planets ?? []).find((p) => `pl:${p.id}` === key);
    if (!planet) throw new Error(`no settlement ${key} in ${system.id}`);
    const range = this.welcome.loot.stationRange + planet.size;
    await this.until(() => this.snapshot, 4000, 'a snapshot');
    await this.flyTo(() => orbitAt(system, planet.orbit, this.snapshot.tick), range - 60, 120000);
    this.send({ t: 'dock', on: true, place: key });
    await this.until(() => this.hangar?.docked, 5000, `landing on ${key}`);
  }

  /** Действие боя и ответ на него: новое состояние, итог или отказ. */
  async act(message) {
    const states = this.mechStates;
    const refusals = this.refusals.length;
    const ends=this.mechEnds;
    const continuing = message.act !== 'start' && message.act !== 'train';
    this.send({ t: 'mechAct', ...(continuing ? {session:this.session, revision:this.revision, unit:this.mech?.current} : {}), request:crypto.randomUUID(), ...message });
    await this.until(() => this.mechStates > states || this.refusals.length > refusals || this.mechEnds>ends, 6000, `answer to ${message.act}`);
    if(this.mech?.winner && !this.end && continuing)await this.until(()=>this.end,6000,'committed terminal result');
    return this.refusals.length > refusals ? this.refusals[this.refusals.length - 1] : null;
  }

  close() {
    clearInterval(this.timer);
    this.ws.close(CLOSE_HIDDEN, 'hidden');
  }
}


const results=[];
function check(text,pass){results.push(pass);console.log(`${pass?'OK  ':'FAIL'} ${text}`);if(!pass)throw new Error(text);}
async function workshop(c,message){const count=c.workshopStates;c.send({t:'workshopAct',revision:c.workshop?.state.revision,request:crypto.randomUUID(),...message});await c.until(()=>c.workshopStates>count,6000,'workshop response');return c.workshop;}
async function play(c,mission){
  c.end=null;const squad=c.workshop.state.mechs.slice(0,mission==='firstSortie'?1:2).map(m=>m.id);
  const refused=await c.act({act:'start',mission,squad});if(refused)throw new Error(`start ${mission}: ${refused}`);
  for(let turn=0;turn<140&&!c.end;turn++){
    for(const command of mechPlan(c.mech,c.rules)){
      if(c.end)break;
      const code=await c.act(command);if(code)throw new Error(`${mission} ${command.act}: ${code}`);
    }
  }
  await c.until(()=>c.end,6000,`result ${mission}`);
  await c.until(()=>c.workshop?.state.results.some(r=>r.session===c.end.result?.session),6000,`saved workshop ${mission}`);
  console.log(`${mission}: ${c.end.won?'win':'loss'} in ${c.mech.round} rounds`);
  return c.end;
}
async function win(c,mission){for(let attempt=0;attempt<12;attempt++){const result=await play(c,mission);if(result.won)return result;}throw new Error(`${mission}: no victory in 12 ordinary attempts`);}
async function equip(c,index,keys){
  const state=c.workshop.state,mech=state.mechs[index];
  const used=new Set(state.mechs.filter(m=>m.id!==mech.id).flatMap(m=>Object.values(m.assembly)));
  const assembly={};
  for(const [slot,key]of Object.entries(keys)){const [category,part]=key.split(':');const found=state.parts.find(p=>p.category===category&&p.part===part&&!used.has(p.id));if(!found)throw new Error(`missing guaranteed ${key}`);assembly[slot]=found.id;used.add(found.id);}
  await workshop(c,{act:'save',mech:mech.id,assembly});if(c.workshop.refused)throw new Error(`equip: ${c.workshop.refused}`);
}
async function main(){
  const name=process.argv[3]??`smk-mech-${RUN}`;let c=new Client({name,password:PASSWORD,create:!process.argv[3]});await c.connect();c.start();c.send({t:'mission',action:'skip'});
  console.log(`pilot ${name}: ${c.welcome.system.id}, travelling to ${PRIME}`);
  await c.until(()=>c.missions&&!c.missions.tutorial&&c.hangar,4000,'tutorial skipped');
  if(!c.hangar.docked || c.hangar.place?.key!==PRIME){await c.undock();for(const to of c.welcome.system.id==='nova'?[]:ROUTE)await c.jump(to);await c.land(PRIME);}
  check('space story opens the first sortie, ground finale remains unfinished',c.story.relay&&c.story.done<c.story.total);
  await workshop(c,{act:'read'});check('space completion grants exactly one persistent prototype',c.workshop.state.mechs.length===1&&!c.workshop.open);
  await c.act({act:'start',mission:'firstSortie'});const session=c.session,round=c.mech.round;
  c.send({t:'dock',on:false});await c.until(()=>c.notices.includes('mechBusy'),3000,'undock blocked');check('undock blocked during battle',c.hangar.docked);
  check('invalid action rejected',await c.act({act:'teleport'})==='badAct');
  c.close();c=new Client({name,password:PASSWORD});await c.connect();await c.until(()=>c.mech,5000,'resumed battle');
  check('second connection resumes the same session and revision',c.session===session&&c.mech.round===round);
  await c.act({act:'quit'});check('retreat repairs without campaign progress',!c.end.won&&c.end.reward===0);
  await workshop(c,{act:'read'});
  const before=c.cargo.credits;const first=await win(c,'firstSortie');check('first victory pays 1500 and opens workshop',first.reward===1500&&c.workshop.open&&c.cargo.credits===before+1500);
  check('starter parts granted once',c.workshop.state.grants.filter(g=>g==='starter').length===1);
  const request=crypto.randomUUID(),oldRev=c.workshop.state.revision;
  await workshop(c,{act:'create',name:'Вторая',request});await workshop(c,{act:'create',name:'Вторая',request,revision:oldRev});
  check('duplicate create does not duplicate machine',c.workshop.state.mechs.length===2);
  const state=c.workshop.state,second=state.mechs[1],installed=new Set(Object.values(state.mechs[0].assembly));
  const free=state.parts.filter(p=>!installed.has(p.id));const find=(category,part)=>free.find(p=>p.category===category&&p.part===part).id;
  await workshop(c,{act:'save',mech:second.id,assembly:{body:find('body','light'),chassis:find('chassis','lightBiped'),left:find('shield','light'),right:find('weapon','pistol-cannon')}});
  check('second ready machine needs no purchase or random loot',!c.workshop.refused);
  for(const mission of ['mineDefense','primeEscort','majorAssault']){
    const end=await win(c,mission);check(`${mission}: victory commits XP, first reward and parts`,end.first&&end.result.parts.length>0&&end.result.experience.length===2);
    if(mission==='mineDefense')await equip(c,1,{body:'body:medium',chassis:'chassis:mediumBiped',left:'shield:light',right:'weapon:autocannon',backpack:'backpack:armor'});
    if(mission==='primeEscort')await equip(c,0,{body:'body:sniper',chassis:'chassis:lightBiped',left:'weapon:pistol-cannon',right:'weapon:sniper'});
  }
  check('quietWar finale is complete',c.story.groundComplete&&c.workshop.state.completed.length===4);
  const credits=c.cargo.credits,parts=c.workshop.state.parts.length,xp=c.workshop.state.mechs[0].xp;
  const repeat=await win(c,'mineDefense');check('replay grants XP and no repeated credits or parts',repeat.reward===0&&!repeat.first&&c.cargo.credits===credits&&c.workshop.state.parts.length===parts&&repeat.result.experience.some(e=>e.gained>0));
  const saved=JSON.stringify(c.workshop.state);c.end=null;await c.act({act:'train'});await c.act({act:'quit'});
  check('training does not mutate workshop, XP, credits or story',c.end.training&&c.cargo.credits===credits&&JSON.stringify(c.workshop.state)===saved);
  const oldSession=repeat.result.session;await c.act({act:'end',session:oldSession,revision:0});check('redelivered old result does not pay again',c.cargo.credits===credits);
  c.close();const back=new Client({name,password:PASSWORD});await back.connect();await workshop(back,{act:'read'});
  check('progress and inventory survive another device',JSON.stringify(back.workshop.state)===saved);
  back.send({t:'dock',on:false});await back.until(()=>!back.hangar.docked,4000,'undock after battle');check('undock allowed after result',!back.hangar.docked);back.close();
}
main().then(()=>{console.log(`all ${results.length} checks passed`);process.exit(0);}).catch(e=>{console.error(e.stack);process.exit(1);});
