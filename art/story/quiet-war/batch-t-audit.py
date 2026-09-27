"""Read-only PNG checks for batch T; writes only a JSON report."""
import json
from pathlib import Path
from PIL import Image

folder = Path(__file__).resolve().parent
entries = json.loads((folder / 'batch-t-manifest.json').read_text(encoding='utf-8'))
report = {'date': '2026-09-27', 'files': [], 'errors': [], 'warnings': []}
for entry in entries:
    with Image.open(folder / entry['file']) as im:
        im.load()
        row = {**entry, 'width': im.width, 'height': im.height, 'mode': im.mode, 'bytes': (folder / entry['file']).stat().st_size, 'decoded': True}
        if entry['kind'] == 'item':
            alpha = im.getchannel('A') if 'A' in im.getbands() else None
            row['alphaExtrema'] = alpha.getextrema() if alpha else None
            row['visibleBounds'] = alpha.getbbox() if alpha else None
            row['alphaThresholdBounds'] = {str(t): alpha.point(lambda v: 255 if v >= t else 0).getbbox() for t in [8, 32, 128, 240]} if alpha else None
            if alpha is None or alpha.getextrema() != (0, 255):
                report['errors'].append(entry['id'] + ': missing transparent background or opaque subject')
            if row['visibleBounds']:
                x0, y0, x1, y1 = row['visibleBounds']
                row['touchesEdge'] = x0 == 0 or y0 == 0 or x1 == im.width or y1 == im.height
                if row['touchesEdge']:
                    report['warnings'].append(entry['id'] + ': faint alpha noise touches canvas edge; source preserved')
                bounds = row['alphaThresholdBounds']['8']
                row['subjectTouchesEdge'] = not bounds or bounds[0] == 0 or bounds[1] == 0 or bounds[2] == im.width or bounds[3] == im.height
                if row['subjectTouchesEdge']:
                    report['errors'].append(entry['id'] + ': subject touches canvas edge at alpha >= 8')
        report['files'].append(row)
(folder / 'batch-t-validation.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
raise SystemExit(bool(report['errors']))
