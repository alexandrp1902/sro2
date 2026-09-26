import json
from pathlib import Path
from PIL import Image

folder = Path(__file__).resolve().parent
root = folder.parents[2]
rows = json.loads((root / 'art/next-art-manifest.json').read_text(encoding='utf-8-sig'))
report = {'date': '2026-09-26', 'A-J': {'total': len(rows), 'missing': [r['name'] for r in rows if not (root / r['dir'] / (r['name'] + '.png')).exists()]}, 'portraits': []}
for name in ['eva', 'holt', 'dan', 'contact']:
    file = folder / 'sources' / f'portrait-{name}.png'
    with Image.open(file) as im:
        im.load()
        report['portraits'].append({'file': file.relative_to(folder).as_posix(), 'width': im.width, 'height': im.height, 'mode': im.mode, 'decoded': True})
(folder / 'validation.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
