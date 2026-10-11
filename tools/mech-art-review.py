"""Render review contact sheets from the exact production rigs (does not edit sources)."""
import json
from pathlib import Path
from PIL import Image, ImageDraw

ROOT=Path(__file__).resolve().parents[1]
BASE=ROOT/'art/mechs/production'
OUT=ROOT/'.artifacts/m22-art'
OUT.mkdir(parents=True,exist_ok=True)
rigs=[json.loads((BASE/name).read_text(encoding='utf-8')) for name in ['medium-rig.json','m22-rig.json']]
DIRECTIONS=rigs[0]['directions']
ASSEMBLIES={
    'light sniper':['chassis-light-biped','body-sniper','arm-pistol-cannon-left','arm-sniper-right'],
    'heavy sword':['chassis-heavy-biped','body-heavy','shield-heavy-left','arm-sword-right','pack-damage'],
    'medium twin':['chassis-medium-biped','body-medium','arm-autocannon-left','arm-autocannon-right','pack-armor'],
    'tracked enemy':['chassis-tracks','body-enemy-heavy','shield-heavy-left','arm-energy-right','pack-repair'],
}
sheet=Image.new('RGBA',(8*160,4*178),(22,29,36,255));draw=ImageDraw.Draw(sheet)
for row,(name,keys) in enumerate(ASSEMBLIES.items()):
    for col,direction in enumerate(DIRECTIONS):
        canvas=Image.new('RGBA',(512,512))
        layers=[]
        for key in keys:
            rig=next((r for r in rigs if key in r['assets']),None)
            if not rig:continue
            asset=rig['assets'][key];t=rig['frames'][direction]['transforms'][key]
            x,y,w,h=asset['frames'][col]
            frame=Image.open(BASE/asset['source']).convert('RGBA').crop((x,y,x+w,y+h))
            edge=round(512*t['scale']);frame=frame.resize((edge,edge),Image.Resampling.LANCZOS)
            layers.append((t['z'],frame,(round(256-edge/2+t['x']),round(256-edge/2+t['y']))))
        for _,frame,pos in sorted(layers,key=lambda x:x[0]):canvas.alpha_composite(frame,pos)
        sheet.alpha_composite(canvas.resize((160,160),Image.Resampling.LANCZOS),(col*160,row*178+18))
        draw.text((col*160+4,row*178+2),name+' '+direction,fill='white')
sheet.convert('RGB').save(OUT/'assemblies.png')
# Inspect each individual source at all eight directions; auxiliary animation frames
# are included here once available, with every phase visible.
for group in ['body','chassis','arm','shield','pack','walk','disabled','damage','wreck','mission','terrain-audit','terrain-new']:
    manifest=json.loads((BASE/'manifest.json').read_text(encoding='utf-8'))
    entries=[e for e in manifest['entries'] if e['group']==group and e.get('source') and (BASE/e['source']).is_file()]
    if not entries:continue
    rows=[]
    for entry in entries:
        image=Image.open(BASE/entry['source']).convert('RGBA')
        layout=entry.get('layout') or dict(columns=1,rows=1)
        for start in range(0,entry['frames'],8):
            row_image=Image.new('RGBA',(8*128,146),(22,29,36,255));d=ImageDraw.Draw(row_image)
            d.text((4,1),entry['id']+' frames '+str(start),fill='white')
            for offset in range(min(8,entry['frames']-start)):
                index=start+offset;x=index%layout['columns'];y=index//layout['columns']
                rect=(round(x*image.width/layout['columns']),round(y*image.height/layout['rows']),
                      round((x+1)*image.width/layout['columns']),round((y+1)*image.height/layout['rows']))
                frame=image.crop(rect).resize((128,128),Image.Resampling.LANCZOS)
                row_image.alpha_composite(frame,(offset*128,18))
            rows.append(row_image)
    preview=Image.new('RGBA',(1024,146*len(rows)))
    for i,row in enumerate(rows):preview.alpha_composite(row,(0,146*i))
    preview.convert('RGB').save(OUT/f'{group}.png')
print(OUT/'assemblies.png')
