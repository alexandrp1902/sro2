// Isolated real server/WebSocket checks. Requires dotnet build server/Sro.sln.
import {spawn} from 'node:child_process';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,readdirSync,cpSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {resolve,join} from 'node:path';
import {randomBytes,pbkdf2Sync} from 'node:crypto';
import net from 'node:net';
import assert from 'node:assert/strict';
import {openSocket} from './wire.mjs';
const root=resolve(import.meta.dirname,'..'),dir=mkdtempSync(join(tmpdir(),'sro-race-smoke-')),data=join(dir,'data'),accounts=join(data,'accounts');
const shared=join(dir,'shared');cpSync(join(root,'shared'),shared,{recursive:true});
// A test-only column guarantees a head-on collision; the production track is untouched.
const trackPath=join(shared,'race/tracks/red-canyon.json'),track=JSON.parse(readFileSync(trackPath,'utf8'));
track.version='network-smoke';track.obstacles.push({id:'smoke-column',center:{x:90,y:0},radius:.6,reason:'COLUMN'});
writeFileSync(trackPath,JSON.stringify(track));
mkdirSync(accounts,{recursive:true});const password='race-smoke-password';
for(let i=0;i<2;i++){
  const id=randomBytes(16).toString('hex'),salt=randomBytes(16);
  writeFileSync(join(accounts,id+'.json'),JSON.stringify({id,name:`race-smoke-${i}`,password:`pbkdf2-sha256$1000$${salt.toString('base64')}$${pbkdf2Sync(password,salt,1000,32,'sha256').toString('base64')}`,keys:[],created:new Date().toISOString(),profile:{system:'tau',place:'pl:tauPrima',hull:'light',weapon:'pulse',hulls:['light'],weapons:[],credits:1234,cargo:{metal:2},career:'ranger',tutorialStep:'done'}}));
}
const port=await new Promise(r=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>r(p));});});
const runtime=join(dir,'runtime');cpSync(join(root,'server/Sro.Server/bin/Debug/net10.0'),runtime,{recursive:true});
let server,logs='';const env={...process.env};delete env.SRO_STORY_SKIP;
function start(){server=spawn('dotnet',[join(runtime,'Sro.Server.dll'),'--urls',`http://127.0.0.1:${port}`,'--SharedDir',shared,'--DataDir',data,'--ClientDist',join(root,'client/dist')],{cwd:join(root,'server/Sro.Server'),env,stdio:['ignore','pipe','pipe'],windowsHide:true});server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);}
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function until(test,label,ms=15000){const end=Date.now()+ms;while(Date.now()<end){const value=await test();if(value)return value;await sleep(25);}throw new Error(`${label} timed out\n${logs.slice(-2500)}`);}
class Client {
  async connect(index){const {ws,read}=openSocket(`ws://127.0.0.1:${port}/ws`);this.ws=ws;this.messages=[];ws.onopen=()=>this.send({t:'hello',name:`race-smoke-${index}`,password});ws.onmessage=e=>{const m=read(e.data);this.messages.push(m);if(m.t==='raceEvent'&&m.snapshot)this.snapshot=m.snapshot;};await this.wait(m=>m.t==='welcome');await this.wait(m=>m.t==='hangar'&&m.docked);}
  send(m){this.ws.send(JSON.stringify(m));}
  wait(test,ms=15000){return until(()=>this.messages.find(test),'WebSocket message',ms);}
  async join(requestId=randomBytes(8).toString('hex')){this.send({t:'raceAct',act:'join',requestId});return this.wait(m=>m.t==='raceEvent'&&m.event==='joined'&&m.requestId===requestId);}
}
const clients=[];let heartbeat;
try {
  start();await until(async()=>{try{return(await fetch(`http://127.0.0.1:${port}/health`)).ok;}catch{return false;}},'health');
  const a=new Client(),b=new Client();clients.push(a,b);await a.connect(0);await b.connect(1);
  const joined=await a.join('idempotent');assert.equal((await a.join('idempotent')).sessionId,joined.sessionId);
  a.send({t:'raceAct',act:'join',requestId:'another'});assert.equal((await a.wait(m=>m.code==='RACE_ALREADY_ACTIVE')).code,'RACE_ALREADY_ACTIVE');
  b.send({t:'raceAct',act:'abort',sessionId:joined.sessionId});await b.wait(m=>m.code==='SESSION_NOT_FOUND');
  a.send({t:'raceAct',act:'ready',sessionId:joined.sessionId,configHash:'wrong'});await a.wait(m=>m.code==='CONFIG_MISMATCH');
  a.send({t:'raceAct',act:'ready',sessionId:joined.sessionId,configHash:joined.config.hash});
  let seq=0;
  heartbeat=setInterval(()=>a.send({t:'raceAct',act:'input',sessionId:joined.sessionId,input:{seq:++seq,steerMode:'turn',heading:0,turn:0,throttle:1,brake:false,boostPressId:0,controlActive:true}}),50);
  await a.wait(m=>m.snapshot?.state==='COUNTDOWN');await sleep(700);
  assert.equal(a.snapshot.position.x,joined.config.track.spawn.x);assert.equal(a.snapshot.elapsedMs,0);
  a.send({t:'dock',on:false});await sleep(100);assert(!a.messages.some(m=>m.t==='hangar'&&!m.docked));
  // Client finishTime and coordinates are never accepted as an official finish.
  a.send({t:'raceAct',act:'result',sessionId:joined.sessionId,finishTime:1,position:{x:30,y:0}});await a.wait(m=>m.code==='RESULT_PENDING');
  const crash=await a.wait(m=>m.event==='result'&&m.sessionId===joined.sessionId&&m.validationStatus==='CONFIRMED',20000);
  clearInterval(heartbeat);heartbeat=null;assert.equal(crash.result.status,'CRASHED');assert.equal(crash.result.totalMs,null);
  const lost=await a.join();a.send({t:'raceAct',act:'ready',sessionId:lost.sessionId,configHash:lost.config.hash});
  const abort=await a.wait(m=>m.event==='result'&&m.sessionId===lost.sessionId&&m.validationStatus==='CONFIRMED',10000);
  assert.equal(abort.result.status,'ABORTED');assert.equal(abort.result.reason,'CONTROL_LOST');assert.equal(abort.result.elapsedMs,3000);
  a.send({t:'raceAct',act:'leaderboard',kind:'total',requestId:'board'});const board=await a.wait(m=>m.event==='leaderboard'&&m.requestId==='board');assert.equal(board.board.rows.length,0);
  const saved=readdirSync(join(data,'race/results')).map(f=>JSON.parse(readFileSync(join(data,'race/results',f))));assert.equal(saved.length,2);
  a.ws.close();b.ws.close();await new Promise(r=>{server.once('exit',r);server.kill();});
  start();await until(async()=>{try{return(await fetch(`http://127.0.0.1:${port}/health`)).ok;}catch{return false;}},'restarted health');
  const again=new Client();clients.push(again);await again.connect(0);
  again.send({t:'raceAct',act:'result',sessionId:joined.sessionId});const recovered=await again.wait(m=>m.event==='result');assert.equal(recovered.result.elapsedMs,crash.result.elapsedMs);
  again.send({t:'dock',on:false});await again.wait(m=>m.t==='hangar'&&!m.docked);
  again.send({t:'raceAct',act:'join',requestId:'wrong-port'});await again.wait(m=>m.code==='NOT_DOCKED_AT_RACE_PORT');
  console.log(JSON.stringify({access:'PASS',countdown:'PASS',foreignSession:'PASS',duplicateJoin:'PASS',crash:'PASS',clientTimeTampering:'PASS',controlTimeout:'PASS',restartRecovery:'PASS',returnToSpace:'PASS',dataDir:dir},null,2));
} finally {if(heartbeat)clearInterval(heartbeat);for(const c of clients)c.ws?.close();server?.kill();}
