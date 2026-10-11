"""Export accepted directional components and calibrated anchors; never substitute a different class.

python tools/mech-catalog.py          # export available accepted components
python tools/mech-catalog.py --check  # verify exported catalog and all gameplay art keys
python tools/mech-catalog.py --check --require-complete # delivery gate including missing P2/P3/P4/P5
"""
import argparse
import json
import sys
import io
import math
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from PIL import Image

ROOT=Path(__file__).resolve().parent.parent
BASE=ROOT/'art/mechs/production'
OUT=ROOT/'client/public/mechs'
META=ROOT/'client/src/mech/artCatalog.json'
AUX_META=ROOT/'client/src/mech/auxCatalog.json'
DIRECTIONS=['N','NE','E','SE','S','SW','W','NW']
EXPORTS=ThreadPoolExecutor(max_workers=8)
PENDING=[]

def read(path):return json.loads(path.read_text(encoding='utf-8'))

def export_frame(sheet, rect, name, check, side=256):
    PENDING.append(EXPORTS.submit(encode_frame,sheet,rect,name,check,side))

def encode_frame(sheet, rect, name, check, side=256):
    x,y,w,h=rect
    if min(x,y)<0 or min(w,h)<=0 or x+w>sheet.width or y+h>sheet.height:
        raise ValueError(f'{name}: frame outside source')
    frame=sheet.crop((x,y,x+w,y+h)).resize((side,side),Image.Resampling.LANCZOS)
    if not frame.getchannel('A').getbbox():raise ValueError(f'{name}: empty frame')
    buffer=io.BytesIO();frame.save(buffer,'WEBP',quality=88,method=6)
    path=OUT/f'{name}.webp'
    if check:
        if not path.is_file() or path.read_bytes()!=buffer.getvalue():
            raise ValueError(f'{name}: exported frame missing or differs from source')
    else:path.write_bytes(buffer.getvalue())

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--check',action='store_true');parser.add_argument('--require-complete',action='store_true');args=parser.parse_args()
    manifest=read(BASE/'manifest.json');rules=read(ROOT/'shared/mechs.json');entries={e['id']:e for e in manifest['entries']}
    keys=set()
    for category in ['bodies','chassis','backpacks']:
        keys.update(p['art'] for p in rules[category].values())
    for category in ['weapons','shields']:
        keys.update(f"{p['art']}-{hand}" for p in rules[category].values() for hand in ['left','right'])
    unknown=keys-entries.keys()
    if unknown:raise ValueError(f'Unknown gameplay art keys: {sorted(unknown)}')
    OUT.mkdir(parents=True,exist_ok=True)
    catalog={};errors=[]
    component_keys=keys | {e['id'] for e in manifest['entries'] if e['stage']=='P2'}
    for key in sorted(component_keys):
        entry=entries[key]
        if entry.get('status')!='accepted' or entry.get('runtimeReady') is not True:continue
        try:
            anchors=entry['anchors'];rig=read(BASE/anchors['rig']);component=anchors['component'];asset=rig['assets'][component]
            source=BASE/asset['source']
            if not source.is_file():raise ValueError('accepted source is missing')
            if rig['directions']!=DIRECTIONS or len(asset['frames'])!=8:raise ValueError('eight explicit directions required')
            sheet=Image.open(source).convert('RGBA');frames={}
            for i,direction in enumerate(DIRECTIONS):
                x,y,w,h=asset['frames'][i]
                if min(x,y)<0 or min(w,h)<=0 or x+w>sheet.width or y+h>sheet.height:raise ValueError('frame outside source')
                t=rig['frames'][direction]['transforms'][component]
                name=f'part-{key}-{direction.lower()}'
                if not all(math.isfinite(t[p]) for p in ['x','y','scale','z']) or t['scale']<=0:raise ValueError('invalid transform')
                export_frame(sheet,[x,y,w,h],name,args.check)
                frames[direction.lower()]=dict(texture=name,x=t['x'],y=t['y'],scale=t['scale'],z=t['z'])
            catalog[key]=dict(cellSize=rig['cellSize'],pivot=rig['groundPivot'],frames=frames)
        except (KeyError,ValueError,OSError) as e:errors.append(f'{key}: {e}')
    auxiliary={}
    aux_path=BASE/'m22-auxiliary.json'
    for key,spec in (read(aux_path) if aux_path.is_file() else {}).items():
        entry=entries[key]
        if entry.get('status')!='accepted' or not entry.get('runtimeReady'):continue
        try:
            sheet=Image.open(BASE/spec['source']).convert('RGBA')
            if len(spec['frames'])!=entry['frames']:raise ValueError('incorrect auxiliary frame count')
            names=[];transforms=[]
            max_height=max(b[3]-b[1] for b in spec['bounds'])
            size=210 if spec['group'] in ['walk','disabled'] else 75 if spec['group']=='damage' else 265
            scale=size/(512*max_height)
            for i,rect in enumerate(spec['frames']):
                name=f'aux-{key}-{i}'
                export_frame(sheet,rect,name,args.check)
                names.append(name)
                b=spec['bounds'][i];cx=(b[0]+b[2])/2
                if spec['group'] in ['walk','disabled']:
                    reference=read(BASE/'medium-rig.json')['frames'][DIRECTIONS[i%8]]['anchors']['waist']
                    mount=[cx,b[1]+(b[3]-b[1])*.12]
                    target=reference
                elif spec['group']=='damage':
                    hand='left' if key.endswith('-left') else 'right'
                    target=read(BASE/'medium-rig.json')['frames'][DIRECTIONS[i%8]]['anchors'][hand+'Shoulder']
                    mount=[cx,(b[1]+b[3])/2]
                else:
                    target=[256,355];mount=[cx,(b[1]+b[3])/2]
                transforms.append(dict(x=round(target[0]-(256+(mount[0]*512-256)*scale),3),
                                       y=round(target[1]-(256+(mount[1]*512-256)*scale),3),
                                       scale=round(scale,6),z=3 if spec['group']=='damage' else 0))
            auxiliary[key]=dict(textures=names,transforms=transforms,group=spec['group'])
            if key in ['desert-ground-1','desert-ground-2','desert-rough','desert-crate','desert-wall','desert-building']:
                tile='tile-'+key.removeprefix('desert-')
                export_frame(sheet,spec['frames'][0],tile,args.check,256 if key.endswith('building') else 128)
        except (KeyError,ValueError,OSError) as error:errors.append(f'{key}: {error}')
    for future in PENDING:
        try:future.result()
        except (ValueError,OSError) as error:errors.append(str(error))
    EXPORTS.shutdown()
    if errors:raise ValueError('; '.join(errors))
    if args.check:
        if read(META)!=catalog:raise ValueError('artCatalog.json differs from accepted calibrated sources')
        if auxiliary and read(AUX_META)!=auxiliary:raise ValueError('auxCatalog.json differs from accepted sources')
    else:
        META.write_text(json.dumps(catalog,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        AUX_META.write_text(json.dumps(auxiliary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(f'{len(keys)} gameplay art keys valid; {len(catalog)} accepted components exported in eight directions')
    missing=sorted(keys-catalog.keys())
    print(f'{len(missing)} components have no accepted runtime files')
    if args.require_complete:
        pending=[e['id'] for e in manifest['entries'] if e['stage'] in ['P2','P3'] and (e.get('status')!='accepted' or not e.get('runtimeReady') or not e.get('source') or not (BASE/e['source']).is_file())]
        pending += [e['id'] for e in manifest['entries'] if (e['stage']=='P4' and e['id'].startswith('desert-') or e['stage']=='P5' and not e['id'].startswith('mech-fx-')) and (e.get('status')!='accepted' or not e.get('runtimeReady') or not e.get('source') or not (BASE/e['source']).is_file())]
        if missing or pending:raise ValueError(f'Incomplete production resources: {sorted(set(missing+pending))}')
        required={e['id'] for e in manifest['entries'] if e['stage']=='P3' or e['stage']=='P5' and e['group']=='mission' or e['stage']=='P4' and e['id'].startswith('desert-')}
        if required-auxiliary.keys():raise ValueError(f'Unexported auxiliary resources: {sorted(required-auxiliary.keys())}')
        print(f'Complete M22 delivery: {len(auxiliary)} auxiliary sheets exported and verified')
if __name__=='__main__':
    try:main()
    except (ValueError,KeyError,OSError) as error:
        print(error,file=sys.stderr)
        raise SystemExit(1)
