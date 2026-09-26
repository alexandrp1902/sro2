"""Read-only image checks for the art request manifest; writes JSON report."""
import json
from pathlib import Path
import numpy as np
from PIL import Image
ROOT = Path(__file__).resolve().parents[1]
manifest = json.loads((ROOT / "art/next-art-manifest.json").read_text(encoding="utf-8-sig"))
results = []
for entry in manifest:
    path = ROOT / entry["dir"] / (entry["name"] + ".png")
    if not path.exists():
        results.append({"name": entry["name"], "missing": True})
        continue
    with Image.open(path) as original:
        original.load()
        data = np.asarray(original.convert("RGBA"))
        alpha = data[:, :, 3]
        ys, xs = np.where(alpha > 24)
        bounds = [int(xs.min()), int(ys.min()), int(xs.max()+1), int(ys.max()+1)] if len(xs) else None
        w, h = original.size
        visible_rgb = data[:, :, :3][alpha > 24]
        results.append({
            "name": entry["name"], "file": str(path.relative_to(ROOT)).replace("\\", "/"),
            "size": [w, h], "requestedSize": [entry["w"], entry["h"]], "mode": original.mode,
            "alphaRequired": entry["alpha"], "transparentPixels": int(np.count_nonzero(alpha == 0)),
            "visiblePixels": int(len(xs)), "bounds": bounds,
            "margins": [bounds[0], bounds[1], w-bounds[2], h-bounds[3]] if bounds else None,
            "nonWhiteVisiblePixels": int(np.count_nonzero(np.any(visible_rgb != 255, axis=1))),
            "allCornersTransparent": all(int(alpha[y,x]) == 0 for y,x in [(0,0),(0,w-1),(h-1,0),(h-1,w-1)])
        })
report = {"sourceImagesUnmodified": True, "results": results}
(ROOT / "art/generation-2026-09-26-audit.json").write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
assert len(results) == 28 and all(not r.get('missing') and r['visiblePixels'] for r in results)
assert all(r['transparentPixels'] > 0 for r in results if r['alphaRequired'])
assert all(r['size'] == [256, 256] and r['nonWhiteVisiblePixels'] == 0 for r in results[-5:])
from html.parser import HTMLParser
class GalleryLinks(HTMLParser):
    def handle_starttag(self, tag, attrs):
        for key, value in attrs:
            if key in ('src', 'href') and value and not value.startswith('#'):
                assert (ROOT / 'art' / value).exists(), value
GalleryLinks().feed((ROOT / 'art/review-2026-09-26.html').read_text(encoding='utf-8'))
print('28 PNGs verified; 15 transparent; 5 pure-white 256px icons; all gallery links exist.')
