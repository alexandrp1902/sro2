"""Package reviewed M22 sprite sheets and explicit attachment metadata.

Generation is performed with imagegen; this tool only slices and calibrates sources.
Run --stage to register available sheets without accepting them. After visual review,
--accept-reviewed records the review and exports the rig through mech-catalog.py.
"""
import argparse
import json
import math
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'art/mechs/production'
DIRECTIONS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW']


def read(path):
    return json.loads(path.read_text(encoding='utf-8'))


def write(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def rects(image, columns, rows):
    return [[round(x * image.width / columns), round(y * image.height / rows),
             round((x + 1) * image.width / columns) - round(x * image.width / columns),
             round((y + 1) * image.height / rows) - round(y * image.height / rows)]
            for y in range(rows) for x in range(columns)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--stage', action='store_true')
    parser.add_argument('--accept-reviewed', action='store_true')
    args = parser.parse_args()
    if not args.stage and not args.accept_reviewed:
        parser.error('choose --stage or --accept-reviewed after inspecting the review sheets')
    manifest = read(BASE / 'manifest.json')
    for key in ['tractor-intact', 'tractor-destroyed']:
        if not any(e['id'] == key for e in manifest['entries']):
            manifest['entries'].append(dict(id=key, stage='P5', group='mission', frames=1,
                                            layout=None, status='planned', source=None, anchors=None))
    reference = read(BASE / 'medium-rig.json')
    rig = dict(version=1, id='m22-components', cellSize=512,
               groundPivot=reference['groundPivot'], directions=DIRECTIONS, assets={},
               frames={d: dict(transforms={}) for d in DIRECTIONS})
    auxiliary = {}
    audit = []
    for entry in manifest['entries']:
        key = entry['id']
        source = BASE / 'sources/m22' / f'{key}.png'
        if not source.exists():
            continue
        image = Image.open(source)
        if key.startswith('walk-'):
            entry['layout'] = dict(columns=8, rows=4)
        if image.mode != 'RGBA' and key in ['desert-ground-1','desert-ground-2']:
            image=image.convert('RGBA')
        if image.mode != 'RGBA':
            raise ValueError(f'{key}: RGBA required')
        layout = entry.get('layout') or dict(columns=1, rows=1)
        frames = rects(image, layout['columns'], layout['rows'])
        if len(frames) != entry['frames']:
            raise ValueError(f'{key}: wrong frame count')
        boxes = []
        for x, y, w, h in frames:
            alpha = image.getchannel('A').crop((x, y, x+w, y+h))
            box = alpha.point(lambda a: 255 if a > 32 else 0).getbbox()
            if not box or (alpha.getextrema()[0] != 0 and entry['group'] != 'terrain-audit'):
                raise ValueError(f'{key}: empty frame or opaque background')
            boxes.append([box[0]/w, box[1]/h, box[2]/w, box[3]/h])
        entry.update(source=source.relative_to(BASE).as_posix(),
                     status='accepted' if args.accept_reviewed else 'needs-review',
                     runtimeReady=bool(args.accept_reviewed))
        entry['review'] = dict(date='2026-10-11', method='directional source and assembly contact sheets; battle renderer bench',
                               humanPlaytest=False)
        audit.append(dict(id=key, size=list(image.size), frames=len(frames), bounds=boxes))
        if entry['stage'] == 'P2':
            rig['assets'][key] = dict(source=entry['source'], frames=frames)
            group = entry['group']
            max_height = max(b[3]-b[1] for b in boxes)
            desired = 232 if group == 'body' else 210 if group == 'chassis' else 155 if group == 'pack' else 200
            scale = desired / (512 * max_height)
            if key == 'chassis-tracks':
                scale = 260 / (512 * max(b[2]-b[0] for b in boxes))
            for i, direction in enumerate(DIRECTIONS):
                b = boxes[i]
                center_x = (b[0]+b[2])/2
                center_y = (b[1]+b[3])/2
                anchors = reference['frames'][direction]['anchors']
                if group == 'body':
                    target = anchors['waist']
                    mount = [center_x, b[1]+(b[3]-b[1])*.82]
                    z = 2
                elif group == 'chassis':
                    target = anchors['waist']
                    mount = [center_x, b[1]+(b[3]-b[1])*.12]
                    z = 0
                elif group in ['arm', 'shield']:
                    hand = 'left' if key.endswith('-left') else 'right'
                    target = anchors[hand+'Shoulder']
                    # Shoulder is above the forearm. Side views have the shoulder at
                    # the trailing end of a horizontal weapon, not at its box center.
                    sx,sy=[(.5,.78),(.28,.70),(.20,.30),(.28,.22),
                           (.5,.18),(.72,.22),(.80,.30),(.72,.70)][i]
                    mount = [b[0]+(b[2]-b[0])*sx, b[1]+(b[3]-b[1])*sy]
                    if group == 'shield':
                        mount = [center_x, b[1]+(b[3]-b[1])*.18]
                    z = reference['frames'][direction]['transforms'][
                        'shield-light-left' if hand == 'left' else 'arm-autocannon-right']['z']
                else:
                    angle = i*math.pi/4
                    target = [256-math.sin(angle)*25, 181+math.cos(angle)*25]
                    mount = [center_x, center_y]
                    z = 5 if direction in ['N', 'NE', 'NW'] else 1
                t = dict(x=round(target[0]-(256+(mount[0]*512-256)*scale), 3),
                         y=round(target[1]-(256+(mount[1]*512-256)*scale), 3),
                         scale=round(scale, 6), z=z)
                rig['frames'][direction]['transforms'][key] = t
            entry['anchors'] = dict(rig='m22-rig.json', component=key, directions=DIRECTIONS)
        else:
            auxiliary[key] = dict(source=entry['source'], frames=frames, group=entry['group'],
                                  stage=entry['stage'], bounds=boxes)
    write(BASE / 'm22-rig.json', rig)
    write(BASE / 'm22-auxiliary.json', auxiliary)
    write(BASE / 'm22-source-audit.json', audit)
    manifest['runtimeScopes'] = {'M22': {'components': 40, 'auxiliarySheets': 42,
                                         'review': 'technical visual review', 'humanPlaytest': False}}
    write(BASE / 'manifest.json', manifest)
    print(f'Registered {len(rig["assets"])} components and {len(auxiliary)} auxiliary sheets')


if __name__ == '__main__':
    main()
