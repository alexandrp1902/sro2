// Real WebSocket smoke on its own DataDir and copied balance. No production accounts or OS clock changes.
// dotnet build server/Sro.sln, then node tools/smoke-territory.mjs
import {spawn} from 'node:child_process';
import {mkdtempSync,mkdirSync,copyFileSync,cpSync,readdirSync,readFileSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {resolve,join} from 'node:path';
import {randomBytes,pbkdf2Sync} from 'node:crypto';
import net from 'node:net';
import {openSocket} from './wire.mjs';
import {mechPlan} from './mech-bot.mjs';

const root=resolve(import.meta.dirname,'..'),dir=mkdtempSync(join(tmpdir(),'sro-m23-smoke-')),shared=join(dir,'shared'),accounts=join(dir,'data','accounts');
mkdirSync(shared,{recursive:true});mkdirSync(accounts,{recursive:true});
for(const file of readdirSync(join(root,'shared'),{withFileTypes:true}).filter(f=>f.isFile()))copyFileSync(join(root,'shared',file.name),join(shared,file.name));
cpSync(join(root,'shared/race'),join(shared,'race'),{recursive:true});
const rules=JSON.parse(readFileSync(join(shared,'mechs.json'),'utf8'));
const config=JSON.parse(readFileSync(join(shared,'mech-territory.json'),'utf8'));
config.seasonEpoch=new Date().toISOString();config.seasonSeconds=120;config.daySeconds=10;config.strikeSeconds=5;config.transitionSeconds=20;
writeFileSync(join(shared,'mech-territory.json'),JSON.stringify(config));
const password='isolated-smoke-password',ids=[];
for(let index=0;index<2;index++) {
  const id=randomBytes(16).toString('hex'),salt=randomBytes(16);ids.push(id);
  const parts=[],mechs=[];
  for(const [i,kind]of ['assaultShield','assaultEnergy'].entries()) {
    const def=i===0?{...rules.units.prototype,left:'pistol-cannon',backpack:'armor'}:{...rules.units.prototype,body:'heavy',chassis:'heavyBiped',left:'autocannon',right:'pistol-cannon',backpack:'armor'},assembly={};
    for(const [slot,part]of Object.entries({body:def.body,chassis:def.chassis,left:def.left,right:def.right,backpack:def.backpack}))if(part) {
      const instance=randomBytes(16).toString('hex'),category=slot==='left'||slot==='right'?(rules.weapons[part]?'weapon':'shield'):slot;
      parts.push({id:instance,category,part});assembly[slot]=instance;
    }
    mechs.push({id:randomBytes(16).toString('hex'),name:`Veteran ${i+1}`,assembly,level:3,xp:0,skills:[]});
  }
  const profile={credits:30000,hull:'light',weapon:'pulse',hulls:['light'],weapons:[],cargo:{titanium:20,energy:10},system:'nova',place:'pl:novaPrime',tutorialStep:'done',career:'ranger',
    workshop:{mechs,parts,resources:{titanium:200,energy:100,rareMetal:20},squad:mechs.map(m=>m.id),grants:['prototype'],completed:['firstSortie','mineDefense','primeEscort','majorAssault'],results:[],revision:0},territory:{level:1}};
  writeFileSync(join(accounts,id+'.json'),JSON.stringify({id,name:`m23-smoke-${index}`,password:`pbkdf2-sha256$1000$${salt.toString('base64')}$${pbkdf2Sync(password,salt,1000,32,'sha256').toString('base64')}`,keys:[],created:new Date().toISOString(),profile}));
}
const port=await new Promise(r=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>r(p));});});
const env={...process.env};delete env.SRO_STORY_SKIP;
const runtime=join(dir,'runtime');cpSync(join(root,'server/Sro.Server/bin/Debug/net10.0'),runtime,{recursive:true});
const processServer=spawn('dotnet',[join(runtime,'Sro.Server.dll'),'--urls',`http://127.0.0.1:${port}`,'--SharedDir',shared,'--DataDir',join(dir,'data'),'--ClientDist',join(root,'client/dist')],{cwd:join(root,'server/Sro.Server'),env,stdio:['ignore','pipe','pipe'],windowsHide:true});
let logs='';processServer.stdout.on('data',chunk=>logs+=chunk);processServer.stderr.on('data',chunk=>logs+=chunk);
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function until(test,label,timeout=15000){const end=Date.now()+timeout;while(Date.now()<end){const value=test();if(value)return value;await sleep(30);}throw new Error(`Timeout: ${label}`);}
class Client {
  async connect(index) {
    const {ws,read}=openSocket(`ws://127.0.0.1:${port}/ws`);this.ws=ws;this.messages=[];
    ws.onopen=()=>this.send({t:'hello',name:`m23-smoke-${index}`,password,mode:'existing'});
    ws.onmessage=e=>{const m=read(e.data);this.messages.push(m);if(m.t==='territoryState')this.territory=m;if(m.t==='workshopState')this.workshop=m;if(m.t==='mechState')this.mech=m;if(m.t==='mechEnd')this.end=m;};
    await until(()=>this.messages.find(m=>m.t==='welcome'),'login');this.send({t:'dock',on:true,place:'pl:novaPrime'});
    await until(()=>this.messages.find(m=>m.t==='hangar'&&m.docked),'prime dock');this.send({t:'territoryAct',act:'read'});this.send({t:'workshopAct',act:'read'});
    await until(()=>this.territory?.docked&&this.workshop,'territory/workshop');
  }
  send(m){this.ws.send(JSON.stringify(m));}
  async act(act,fields={},retry=0) {
    const requestId=randomBytes(12).toString('hex');this.send({t:'territoryAct',act,requestId,revision:this.territory.world.revision,season:this.territory.world.season,...fields});
    const response=await until(()=>this.messages.find(m=>m.t==='territoryState'&&m.requestId===requestId),act);
    if(response.refused==='staleRevision'&&retry<3){await sleep(40);return this.act(act,fields,retry+1);}
    if(response.refused)throw new Error(`${act}: ${response.refused}`);return response;
  }
  async command(command) {
    const m=this.mech,request=randomBytes(12).toString('hex'),start=this.messages.length;
    this.send({t:'mechAct',...command,session:m.session,revision:m.revision,unit:m.battle.current,request});
    await until(()=>this.messages.slice(start).find(x=>x.t==='mechEnd'||x.t==='mechRefused'||x.t==='mechState'&&x.revision>m.revision),'mech command');
    const refused=this.messages.slice(start).find(x=>x.t==='mechRefused');if(refused)throw new Error(`mech: ${refused.code}`);
  }
}
const clients=[];const results=[];
function movement(state,goal,adjacent=false) {
  const me=state.units.find(u=>u.id===state.current),width=state.map[0].length,taken=new Set(state.units.filter(u=>u.hp[0]>0&&u!==me).map(u=>u.y*width+u.x));
  const pass=(x,y)=>x>=0&&y>=0&&y<state.map.length&&x<width&&['.',','].includes(state.map[y][x]);
  const distance=(x,y)=>Math.max(Math.abs(x-goal.x),Math.abs(y-goal.y));
  const candidates=[{x:me.x,y:me.y,n:0}],seen=new Set([me.y*width+me.x]);
  for(let i=0;i<candidates.length;i++){const p=candidates[i];if(p.n>=state.moveRange||state.moved)continue;for(let dy=-1;dy<=1;dy++)for(let dx=-1;dx<=1;dx++){
    const x=p.x+dx,y=p.y+dy,index=y*width+x;if(!pass(x,y)||taken.has(index)||seen.has(index)||dx&&dy&&(!pass(p.x+dx,p.y)||!pass(p.x,p.y+dy)))continue;seen.add(index);candidates.push({x,y,n:p.n+1});}}
  candidates.sort((a,b)=>Math.abs(distance(a.x,a.y)-(adjacent?1:0))-Math.abs(distance(b.x,b.y)-(adjacent?1:0))||a.n-b.n);
  const best=candidates[0];return best.n>0?{act:'move',x:best.x,y:best.y}:{act:'end'};
}
async function operation(object,kind,coop=false) {
  const [a,b]=clients;a.end=null;b.end=null;a.mech=null;b.mech=null;
  await a.act('prepare',{object,operation:kind,squad:a.workshop.state.mechs.map(m=>m.id),partner:coop?'m23-smoke-1':undefined});
  const prep=a.territory.preparation;
  if(coop){await until(()=>b.territory.preparation?.id===prep.id,'invite');await b.act('ready',{preparation:prep.id,squad:b.workshop.state.mechs.map(m=>m.id)});await until(()=>a.territory.preparation.players.every(p=>p.ready),'ready');}
  await a.act('start',{preparation:prep.id});await until(()=>a.mech,'battle start');
  for(let turn=0;turn<200&&!a.end;turn++) {
    const state=a.mech.battle,owner=state.units.find(u=>u.id===state.current)?.accountOwner,client=owner===ids[1]?b:a;
    if(state.winner){await until(()=>a.end,'result');break;}
    await until(()=>client.mech?.revision===a.mech.revision,'same battle revision');
    const me=state.units.find(u=>u.id===state.current),objective=state.units.find(u=>u.id===state.objectiveUnit);
    let command;
    if(['sabotage','rescue'].includes(kind)) {
      const done=kind==='sabotage'?objective.hp[0]===0:state.interacted;
      const target=done?state.evacuation[0]:objective,adjacent=!done,d=Math.max(Math.abs(me.x-target.x),Math.abs(me.y-target.y));
      command=!done&&d===1?{act:'interact'}:movement(state,target,adjacent);
    } else command=mechPlan(state,a.mech.rules)[0];
    await client.command(command);
  }
  if(!a.end?.won)throw new Error(`${kind} failed: ${JSON.stringify({end:a.end,round:a.mech.battle.round,progress:a.mech.battle.progress,current:a.mech.battle.current,units:a.mech.battle.units.map(u=>({id:u.id,x:u.x,y:u.y,hp:u.hp}))})}`);
  results.push({kind,coop,rounds:a.mech.battle.round,reward:a.end.reward});console.log(`OK ${kind} ${coop?'coop':'solo'}: ${a.mech.battle.round} rounds`);
  await until(()=>!a.territory.world.objects.find(o=>o.id===object).active,'released reservation');
  for(const client of coop?clients:[a]) {
    client.send({t:'workshopAct',act:'read'});await sleep(80);
    for(const mech of Object.keys(client.territory.profile.debts??{}))await client.act('service',{mech});
  }
}
try {
  await until(asyncReady,'server startup');
  clients.push(new Client(),new Client());await clients[0].connect(0);await clients[1].connect(1);
  for(let i=0;i<4;i++)await operation('np-02','assault',i===0);
  if(clients[0].territory.world.objects.find(o=>o.id==='np-02').owner!=='resistance')throw new Error('owner did not change');
  await operation('np-02','escort');await operation('np-07','rescue');await operation('np-07','sabotage');
  const defense=await until(()=>clients[0].territory.world.objects.find(o=>o.defenseAt&&!o.active),'planned defense',15000);await operation(defense.id,'defense');
  await until(()=>clients[0].territory.world.claims.some(c=>c.kind==='supply'&&!c.claimed),'daily supply',20000);
  const claim=clients[0].territory.world.claims.find(c=>c.kind==='supply'&&!c.claimed);await clients[0].act('claim',{claim:claim.id});
  await until(()=>clients[0].territory.world.season===2,'short season',130000);
  const final=clients[0].territory.world.claims.find(c=>c.kind==='final');if(!final)throw new Error('missing final reward');await clients[0].act('claim',{claim:final.id});
  console.log(JSON.stringify({ok:true,operations:results,supply:claim.reward,season:2,dataDir:dir},null,2));
} catch(error) {console.error(error);console.error(logs.slice(-4000));process.exitCode=1;}
finally {for(const client of clients)client.ws?.close();processServer.kill();writeFileSync(join(dir,'server.log'),logs);}
function asyncReady(){return logs.includes('Now listening on:')||logs.includes('Application started.');}
