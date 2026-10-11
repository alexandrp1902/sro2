import type { ClientMessage, WorkshopStateMsg } from '../net/protocol';
import { accessible, availableOperations, serviceCost, type TerritoryAct, type TerritoryStateMsg } from './territoryRules';

const TYPES: Record<string,string>={mine:'Шахта',factory:'Завод',power:'Энергостанция',warehouse:'Склад',relay:'Узел связи',repair:'Ремонтный центр',fort:'Укрепление'};
const BIOMES: Record<string,string>={desert:'Пустыня',ice:'Лёд',jungle:'Джунгли',barren:'Каменистая равнина','station-interior':'Промышленный комплекс'};
const ERRORS: Record<string,string>={territoryLocked:'Территория откроется после победы в финальном штурме.',territoryDock:'Для вылазки и выдачи снабжения прилетите в док Рудника Прайм.',staleRevision:'Состояние фронта изменилось. Проверьте обновлённую карту.',objectBusy:'Здесь уже идёт вылазка.',operationUnavailable:'Эта операция сейчас недоступна.',partnerUnavailable:'Напарник должен быть в доке Прайм, на связи и после финала кампании.',notReady:'Дождитесь готовности обоих игроков.',invitationExpired:'Приглашение истекло. Подготовьте вылазку заново.',serviceDebt:'Сначала обслужите выбранную машину.',squadRequired:'Выберите две машины; для аварийной одиночной вылазки достаточно одной.',resources:'Недостаточно кредитов или ресурсов.',saveFailed:'Ожидаем сохранения на сервере. Выплата ещё не подтверждена.',emergencyCooldown:'Аварийное обслуживание доступно раз в 24 часа.',incomplete:'У выбранной машины неполная сборка.',mechBusy:'Сначала завершите текущий бой.'};
const EVA=[['Вернуть завод','Победите в штурме завода, завершив хотя бы одну свою активацию. Награда 1000 кр.'],['Ответный удар','Отразите назначенный удар обороной своего объекта. Награда 1500 кр.'],['Снабжение фронта','Привезите и передайте из трюма 20 титана и 10 энергии. Награда 2000 кр.']];
function node(tag: string, text='', className=''): HTMLElement {const n=document.createElement(tag);n.textContent=text;n.className=className;return n;}
function button(text: string, click: ()=>void, disabled=false): HTMLButtonElement {const b=document.createElement('button');b.className='sro-btn';b.textContent=text;b.disabled=disabled;b.onclick=click;return b;}

export class TerritoryPanel {
  private message: TerritoryStateMsg | null=null;
  private workshop: WorkshopStateMsg | null=null;
  private host: HTMLElement | null=null;
  private selected: string | null=null;
  private squad: string[]=[];
  private partner='';
  private route=false;
  private cardOpen=false;
  private waiting=false;
  private connected=true;
  private pending: TerritoryAct | null=null;
  private remote: HTMLElement | null=null;
  constructor(private readonly send: (message:ClientMessage)=>void) {}
  set(message: TerritoryStateMsg): void {
    this.message=message;
    if(this.pending && message.requestId===this.pending.requestId && !message.waiting) this.pending=null;
    this.waiting=message.waiting||this.pending!==null;
    if(this.host?.isConnected) this.render(this.host);
  }
  setWorkshop(workshop: WorkshopStateMsg): void {this.workshop=workshop;if(this.host?.isConnected)this.render(this.host);}
  online(online:boolean):void {if(this.connected===online)return;this.connected=online;if(online){this.send({t:'territoryAct',act:'read'});if(this.pending)this.send(this.pending);}if(this.host?.isConnected)this.render(this.host);}
  openRemote():void {
    this.remote?.remove();this.remote=node('aside','','territory-remote sro-pane');
    this.remote.append(button('Закрыть карту',()=>{this.remote?.remove();this.remote=null;}));
    const content=node('div');this.remote.append(content);document.body.append(this.remote);this.render(content);
    this.send({t:'territoryAct',act:'read'});this.send({t:'workshopAct',act:'read'});
  }
  private act(action: Omit<TerritoryAct,'t'|'requestId'|'revision'|'season'>):void {
    if(!this.message||this.waiting||!this.connected)return;
    this.cardOpen=false;
    this.pending={t:'territoryAct',...action,requestId:crypto.randomUUID(),revision:this.message.world.revision,season:this.message.world.season};
    this.waiting=true;this.send(this.pending);if(this.host)this.render(this.host);
  }
  render(host:HTMLElement):void {
    this.host=host;host.replaceChildren();host.classList.add('territory');host.append(node('h2','Нова-Прайм · общий фронт'));
    const data=this.message;if(!data){host.append(node('p','Загрузка карты…'));return;}
    const {world,config,profile}=data;
    const end=new Date(world.seasonEnd),remaining=Math.max(0,end.getTime()-Date.now());
    host.append(node('p',`Сезон ${world.season} · окончание ${end.toLocaleString()} · осталось ${Math.floor(remaining/86400000)} д ${Math.floor(remaining/3600000)%24} ч · ваш вклад ${world.contributions[data.account]??0}`));
    host.append(node('p','После сезона контроль приблизится к стартовому. Мехи, опыт и мастерская сохраняются.','sro-muted'));
    if(!data.unlocked)host.append(node('p',ERRORS.territoryLocked,'sro-warn'));
    if(!data.docked)host.append(node('p',ERRORS.territoryDock,'sro-muted'));
    if(data.refused)host.append(node('p',ERRORS[data.refused]??'Действие не выполнено. Проверьте состав и состояние фронта.','sro-warn'));
    if(this.waiting)host.append(node('p','Ожидаем долговременного сохранения…','sro-warn'));
    if(!this.connected)host.append(node('p','Восстанавливаем связь…','sro-warn'));
    const locked=this.waiting||!this.connected||!data.unlocked||!data.docked;
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 110 105');svg.classList.add('territory-map');svg.setAttribute('aria-label','Карта объектов и дорог Нова-Прайм');
    for(const def of config.objects) {
      for(const id of def.links.filter(id=>id>def.id)) {
        const other=config.objects.find(o=>o.id===id)!;const line=document.createElementNS(svg.namespaceURI,'line');
        for(const [key,value]of Object.entries({x1:def.x,y1:def.y,x2:other.x,y2:other.y}))line.setAttribute(key,String(value));
        line.setAttribute('stroke','#6a737f');line.setAttribute('stroke-width','1.2');line.setAttribute('tabindex','0');line.setAttribute('role','button');line.setAttribute('aria-label',`Маршрут ${def.name} — ${other.name}`);
        const pick=()=>{const target=world.objects.find(o=>o.id===id)!;this.selected=target.owner==='resistance'?id:def.id;this.route=true;this.cardOpen=true;this.render(host);};line.addEventListener('click',pick);line.addEventListener('keydown',e=>{if((e as KeyboardEvent).key==='Enter')pick();});svg.append(line);
      }
    }
    for(const def of config.objects) {
      const obj=world.objects.find(o=>o.id===def.id)!;const g=document.createElementNS(svg.namespaceURI,'g');g.setAttribute('tabindex','0');g.setAttribute('role','button');g.setAttribute('aria-label',`${def.name}: ${obj.owner==='resistance'?'Сопротивление':'Корпорация'}, контроль ${obj.control}`);
      const circle=document.createElementNS(svg.namespaceURI,'circle');circle.setAttribute('cx',String(def.x));circle.setAttribute('cy',String(def.y));circle.setAttribute('r',this.selected===def.id?'4.5':'3.5');circle.setAttribute('fill',obj.owner==='resistance'?'#6fceb2':'#dc846f');circle.setAttribute('stroke','#17232e');
      const text=document.createElementNS(svg.namespaceURI,'text');text.setAttribute('x',String(def.x));text.setAttribute('y',String(def.y-6));text.setAttribute('text-anchor','middle');text.setAttribute('font-size','3.3');text.setAttribute('fill','#e7edf5');text.textContent=`${obj.owner==='resistance'?'◆':'■'} ${def.id.slice(-2)} · ${obj.control}%`;
      const pick=()=>{this.selected=def.id;this.route=false;this.cardOpen=true;this.render(host);};g.addEventListener('click',pick);g.addEventListener('keydown',e=>{if((e as KeyboardEvent).key==='Enter')pick();});g.append(circle,text);svg.append(g);
    }
    host.append(svg);
    const list=node('div','','territory-list');for(const def of config.objects){const obj=world.objects.find(o=>o.id===def.id)!;list.append(button(`${obj.owner==='resistance'?'◆':'■'} ${def.name} · ${obj.control}%${obj.active?' · вылазка':''}`,()=>{this.selected=def.id;this.route=false;this.cardOpen=true;this.render(host);}));}host.append(list);
    const id=this.selected??config.objects[0].id,def=config.objects.find(o=>o.id===id)!,obj=world.objects.find(o=>o.id===id)!;
    const card=node('section','','territory-card sro-pane'+(this.cardOpen?' territory-bottom':''));if(this.cardOpen)card.append(button('Закрыть карточку',()=>{this.cardOpen=false;this.render(host);}));card.append(node('h3',`${this.route?'Маршрут к объекту: ':''}${def.name}`),node('p',`${TYPES[def.type]} · ${BIOMES[def.biome]??def.biome} · ${obj.owner==='resistance'?'Сопротивление ◆':'Корпорация ■'} · контроль ${obj.control}/100`));
    const progress=document.createElement('progress');progress.max=100;progress.value=obj.control;progress.setAttribute('aria-label','Контроль сопротивления');card.append(progress);
    if(obj.defenseAt)card.append(node('p',`Ответный удар: отразите до ${new Date(obj.defenseAt).toLocaleString()}`));
    if(obj.active)card.append(node('p','На объекте уже идёт вылазка.','sro-warn'));
    if(!accessible(config,world.objects,id))card.append(node('p','Сначала захватите соседний объект или узел связи.','sro-muted'));
    card.append(node('h4','Состав вылазки'));
    for(const mech of this.workshop?.state.mechs??[]) {
      const label=node('label'),check=document.createElement('input');check.type='checkbox';check.checked=this.squad.includes(mech.id);check.disabled=locked||!!profile.debts?.[mech.id];check.onchange=()=>{this.squad=check.checked?[...this.squad,mech.id].slice(-2):this.squad.filter(id=>id!==mech.id);this.render(host);};
      label.append(check,document.createTextNode(`${mech.name} · ур. ${mech.level}${profile.debts?.[mech.id]?' · требуется обслуживание':''}`));card.append(label);
    }
    const partner=document.createElement('input');partner.value=this.partner;partner.placeholder='Ник напарника (пусто — одиночная)';partner.setAttribute('aria-label','Ник напарника');partner.oninput=()=>this.partner=partner.value;partner.disabled=locked;card.append(partner);
    const discount=world.objects.some(o=>o.owner==='resistance'&&config.objects.find(d=>d.id===o.id)?.type==='repair');const cost=serviceCost(1,1,discount);
    card.append(node('p',`Максимальное обслуживание каждой машины: ${cost.titanium} титана + ${cost.energy} энергии. Аварийная вылазка: одна свободная машина.`,'sro-muted'));
    for(const key of availableOperations(obj).filter(k=>!this.route||k==='escort'||k==='rescue')) {
      const op=config.operations[key];card.append(node('p',`${op.name}: ${op.goal}. Контроль +${op.winControl} / ${op.loseControl}. Вклад +${op.contribution}, награда ${op.credits} кр.`));
      card.append(button(`Подготовить: ${op.name}`,()=>this.act({act:'prepare',object:id,operation:key,squad:this.squad,partner:this.partner||undefined}),locked||!!obj.active||!accessible(config,world.objects,id)||this.squad.length<1));
    }
    host.append(card);
    if(data.preparation) {
      const prep=data.preparation;const panel=node('section','','sro-pane territory-preparation');panel.append(node('h3','Совместная подготовка'),node('p',`Приглашение до ${new Date(prep.expires).toLocaleTimeString()}`));
      for(const player of prep.players)panel.append(node('p',`${player.name} · ${player.ready?'готов':'выбирает машины'}`));
      panel.append(button('Готов: выбранные машины',()=>this.act({act:'ready',preparation:prep.id,squad:this.squad}),locked||this.squad.length<1),button('Начать вылазку',()=>{this.remote?.remove();this.act({act:'start',preparation:prep.id});},locked||prep.leader!==data.account||prep.players.some(p=>!p.ready)),button('Отменить',()=>this.act({act:'cancel',preparation:prep.id}),locked));host.append(panel);
    }
    const service=node('section','','territory-service');service.append(node('h3',`Мастерская · уровень ${profile.level}`));
    if(profile.level<3)service.append(node('p',profile.level===1?'Уровень 2: 5000 кр, 40 титана, 20 энергии. Усиленная броня и точность.':'Уровень 3: 10000 кр, 80 титана, 40 энергии, 10 редкого металла. Усиленный ремонт и оружие.'),button('Улучшить мастерскую',()=>this.act({act:'upgrade'}),locked));
    for(const [mech,debt] of Object.entries(profile.debts??{})) {
      service.append(node('p',`${this.workshop?.state.mechs.find(m=>m.id===mech)?.name??'Машина'}: ${debt.titanium} титана + ${debt.energy} энергии`),button('Обслужить',()=>this.act({act:'service',mech}),locked),button('Аварийное обслуживание',()=>this.act({act:'emergency',mech}),locked||!!profile.emergencyAt&&Date.now()<new Date(profile.emergencyAt).getTime()+86400000));
    }
    for(const claim of world.claims.filter(c=>!c.claimed)) service.append(node('p',`${claim.kind==='final'?'Итог сезона':'Снабжение'} ${claim.season}: ${claim.reward.credits} кр · ${claim.reward.titanium} Ti · ${claim.reward.energy} энергии · ${claim.reward.rareMetal} редкого металла${claim.reward.part?' + деталь':''}`),button('Получить в доке',()=>this.act({act:'claim',claim:claim.id}),locked));
    host.append(service);
    if(profile.evaStep<3) {
      const eva=node('section','','territory-eva sro-pane');eva.append(node('h3',`Ева Морен · ${EVA[profile.evaStep][0]}`),node('p',EVA[profile.evaStep][1]));
      if(profile.evaAccepted&&!profile.evaDone)eva.append(node('p','Ева: задача ещё впереди. Выберите подходящую операцию; следующий удар назначается каждые шесть часов.'));
      if(!profile.evaAccepted)eva.append(button('Принять задание',()=>this.act({act:'evaAccept'}),locked));
      if(profile.evaStep===2&&profile.evaAccepted&&!profile.evaDone)eva.append(button('Передать из трюма 20 титана и 10 энергии',()=>this.act({act:'evaDeliver'}),locked));
      if(profile.evaDone)eva.append(node('p','Ева: фронт благодарит вас. Заберите награду.'),button('Получить награду',()=>this.act({act:'evaClaim'}),locked));host.append(eva);
    }
    for(const title of profile.titles??[])host.append(node('p',title));
    const history=node('details');history.append(node('summary','История сезонов'));
    for(const archive of world.archives)history.append(node('p',`Сезон ${archive.season}: ${archive.objects.filter(o=>o.owner==='resistance').length}/12 объектов · ваш вклад ${archive.contributions[data.account]??0}`));host.append(history);
    const result=this.workshop?.state.results.at(-1);
    if(result&&['assault','sabotage','defense','escort','rescue'].includes(result.mission))host.append(node('p',`Последняя вылазка: ${result.won?'победа':'поражение'} · ${result.credits} кр · XP ${result.experience.reduce((n,e)=>n+e.gained,0)}. Долги машин указаны выше.`));
  }
}
