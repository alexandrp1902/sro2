'use strict';
(async()=>{
 try{
  for(const entry of window.MECH_REVIEW.results){
   if(entry.error)throw Error(entry.error);
   const h=document.createElement('h2');h.textContent=entry.id+' — '+entry.width+'×'+entry.height;document.querySelector('main').append(h);
   const row=document.createElement('div');row.className='row';document.querySelector('main').append(row);
   const img=new Image();img.src=entry.source;await img.decode();
   for(const frame of entry.frames){
    const card=document.createElement('section');card.className='card';const label=document.createElement('strong');label.textContent=frame.direction;card.append(label);
    for(const size of [240,72]){const c=document.createElement('canvas');c.width=c.height=size;c.getContext('2d').drawImage(img,...frame.rect,0,0,size,size);card.append(c);}
    row.append(card);
   }
  }
 }catch(e){document.querySelector('#error').textContent=e.message;}
 document.body.dataset.ready='true';
})();
