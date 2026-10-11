// Deterministic smoke driver. All rolls, legal actions and results still belong to the server.
const steps=[[0,-1],[1,-1],[1,0],[1,1],[0,1],[-1,1],[-1,0],[-1,-1]];
const distance=(a,b)=>Math.max(Math.abs(a.x-b.x),Math.abs(a.y-b.y));
export function mechPlan(state,rules){
  const me=state.units.find(u=>u.id===state.current);if(!me)return[{act:'end'}];
  const width=state.map[0].length,height=state.map.length;
  const inside=(x,y)=>x>=0&&y>=0&&x<width&&y<height;
  const pass=(x,y)=>inside(x,y)&&['.',','].includes(state.map[y][x]);
  const index=(x,y)=>y*width+x;
  const taken=new Set(state.units.filter(u=>u.hp[0]>0&&u.id!==me.id).map(u=>index(u.x,u.y)));
  const here=index(me.x,me.y), reach=new Map([[here,0]]),queue=[here];
  for(let head=0;head<queue.length;head++){
    const at=queue[head],n=reach.get(at);if(state.moved||n>=state.moveRange)continue;
    const x=at%width,y=Math.floor(at/width);
    for(const [dx,dy]of steps){const xx=x+dx,yy=y+dy,cell=index(xx,yy);
      if(!pass(xx,yy)||taken.has(cell)||reach.has(cell)||dx&&dy&&(!pass(x+dx,y)||!pass(x,y+dy)))continue;
      reach.set(cell,n+1);queue.push(cell);
    }
  }
  const blocked=(x,y)=>['w','b'].includes(state.map[y][x]);
  const axis=(a,d,i,n)=>{const t=a*n+d*i,cell=Math.floor(t/n),rest=t-cell*n;return 2*rest===n?[cell,cell+1]:2*rest<n?[cell,cell]:[cell+1,cell+1];};
  const line=(a,b)=>{const n=distance(a,b);for(let i=1;i<n;i++){const[x0,x1]=axis(a.x,b.x-a.x,i,n),[y0,y1]=axis(a.y,b.y-a.y,i,n);if(blocked(x0,y0)&&blocked(x1,y0)&&blocked(x0,y1)&&blocked(x1,y1))return false;}return true;};
  const def=me.assembly??rules.units[me.unit];
  if(!state.units.some(u=>u.control==='player'&&u.hp[0]>0&&['left','right'].some((hand,i)=>u.hp[i+1]>0&&rules.weapons[(u.assembly??rules.units[u.unit])[hand]])))return[{act:'quit'}];
  const objective=rules.missions[state.mission]?.objective;
  const holdsZone=objective?.kind==='capture' && (def.right!=='sniper' || !state.units.some(u=>u.id!==me.id&&u.control==='player'&&u.hp[0]>0));
  const foes=state.units.filter(u=>u.side!==me.side&&u.hp[0]>0);
  const inZone=p=>objective?.zone?.some(z=>z.x===p.x&&z.y===p.y);
  let best=null;
  for(const[cell,n]of [...reach].sort((a,b)=>a[0]-b[0])){
    const pos={x:cell%width,y:Math.floor(cell/width)};
    for(const foe of foes)for(const[hand,i]of [['right',2],['left',1]]){
      const id=def[hand],w=rules.weapons[id];if(!w||me.hp[i]<=0)continue;
      const d=distance(pos,foe);if(d<w.minRange||d>w.maxRange||!line(pos,foe))continue;
      const fd=foe.assembly??rules.units[foe.unit];
      let part='body';
      for(const [arm,j]of [['right',2],['left',1]])if(objective?.kind!=='capture'&&foe.hp[j]>0&&foe.hp[j]<=w.damage*2&&rules.weapons[fd[arm]]){part=arm;break;}
      const[fx,fy]=steps[foe.dir],rx=pos.x-foe.x,ry=pos.y-foe.y;
      const f=rx*fx+ry*fy,s=rx*-fy+ry*fx;
      const from=f>Math.abs(s)?'front':-f>Math.abs(s)?'rear':s>0?'right':'left';
      const c=rules.combat,rangeLoss=d<w.optimalMin?(w.optimalMin-d)*c.rangeStep:d>w.optimalMax?(d-w.optimalMax)*c.rangeStep:0;
      const chance=Math.max(c.hitMin,Math.min(c.hitMax,w.accuracy-Math.min(c.rangeFloor,rangeLoss)+(n===0?c.stillBonus:n*2<=state.moveRange?0:-c.farMovePenalty)+(from==='rear'?c.rearBonus:from==='front'?0:c.flankBonus)-c.aimedPenalty));
      const armor=part==='body'?rules.bodies[fd.body].armor:(rules.weapons[fd[part]]??rules.shields[fd[part]]).armor;
      const raw=w.damage*chance/100*100/(100+armor);
      let score=raw+(part!=='body'?100:0)-(foe.hp[0]/10000)-n*.01;
      if(from!=='rear')for(const arm of ['left','right']){const shield=rules.shields[fd[arm]];if(shield&&foe.hp[arm==='left'?1:2]>0&&part!==arm)score-=shield.block;}
      if(holdsZone&&inZone(pos))score+=2000;
      if(['defend','escort'].includes(objective?.kind)){
        const obj=state.units.find(u=>u.id===state.objectiveUnit);if(obj)score+=Math.max(0,10-distance(foe,obj))*12;
      }
      if(!best||score>best.score)best={cell,n,target:foe.id,hand,part,score};
    }
  }
  if(holdsZone){
    const capture=[...reach].map(([cell,n])=>({cell,n,pos:{x:cell%width,y:Math.floor(cell/width)}})).filter(c=>inZone(c.pos)).sort((a,b)=>a.n-b.n||a.cell-b.cell)[0];
    if(capture&&(!best||!inZone({x:best.cell%width,y:Math.floor(best.cell/width)})))return[...(capture.cell!==here?[{act:'move',x:capture.pos.x,y:capture.pos.y}]:[]),{act:'end'}];
  }
  if(best)return[...(best.cell!==here?[{act:'move',x:best.cell%width,y:Math.floor(best.cell/width)}]:[]),{act:'attack',target:best.target,hand:best.hand,part:best.part}];
  const goal=holdsZone?objective.zone.slice().sort((a,b)=>distance(me,a)-distance(me,b))[0]
    :foes.slice().sort((a,b)=>distance(me,a)-distance(me,b))[0];
  if(!goal)return[{act:'end'}];
  const candidates=[...reach].sort((a,b)=>distance({x:a[0]%width,y:Math.floor(a[0]/width)},goal)-distance({x:b[0]%width,y:Math.floor(b[0]/width)},goal)||a[1]-b[1]||a[0]-b[0]);
  const cell=candidates[0][0];return[...(cell!==here?[{act:'move',x:cell%width,y:Math.floor(cell/width)}]:[]),{act:'end'}];
}
