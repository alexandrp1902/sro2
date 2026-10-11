"""Validate generated race art and prepare review exports without replacing live assets.

Run from any directory: python tools/prepare-race-art.py
Requires Pillow. Originals in art/race/sources remain untouched.
"""
from pathlib import Path
import html
import json

from PIL import Image, ImageChops, ImageStat


ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "art" / "race"
BUDGET = 8 * 1024 * 1024


def prepare():
    manifest = json.loads((ART / "generation.json").read_text(encoding="utf-8"))
    names = [asset["name"] for asset in manifest["assets"]]
    if len(names) != 22 or len(set(names)) != 22:
        raise SystemExit("Expected 22 distinct assets in generation.json")
    candidates = ART / "candidates"
    candidates.mkdir(exist_ok=True)
    results, cards = [], []
    for asset in manifest["assets"]:
        name = asset["name"]
        source_file = asset.get("source_file", f"{name}.png")
        source = ART / "sources" / source_file
        with Image.open(source) as original:
            original.load()
            rgba = original.convert("RGBA")
            alpha = rgba.getchannel("A")
            histogram = alpha.histogram()
            transparent = asset["transparent"]
            errors = []
            if transparent and ("A" not in original.getbands() or histogram[0] == 0):
                errors.append("Missing transparent pixels / alpha channel")
            if not transparent and sum(histogram[:255]):
                errors.append("Surface is not fully opaque")
            # Ignore almost invisible antialiasing when measuring occupied bounds.
            bounds = alpha.point(lambda value: 255 if value >= 16 else 0).getbbox()
            clipped = bool(bounds and (bounds[0] == 0 or bounds[1] == 0 or
                                      bounds[2] == original.width or bounds[3] == original.height))
            if transparent and clipped:
                errors.append("Visible content touches canvas edge; inspect clipping")
            requested = [asset["width"], asset["height"]]
            exported = rgba if transparent else original.convert("RGB")
            exported.thumbnail((512, 512), Image.Resampling.LANCZOS)
            target = candidates / f"{name}.webp"
            if not target.exists() or target.stat().st_mtime < source.stat().st_mtime:
                exported.save(target, "WEBP", quality=90, method=6)
            seams = None
            if not transparent:
                rgb = original.convert("RGB")
                horizontal = ImageChops.difference(rgb.crop((0, 0, 1, rgb.height)),
                                                  rgb.crop((rgb.width - 1, 0, rgb.width, rgb.height)))
                vertical = ImageChops.difference(rgb.crop((0, 0, rgb.width, 1)),
                                                rgb.crop((0, rgb.height - 1, rgb.width, rgb.height)))
                seams = {"left_right_mean_difference": round(sum(ImageStat.Stat(horizontal).mean) / 3, 2),
                         "top_bottom_mean_difference": round(sum(ImageStat.Stat(vertical).mean) / 3, 2)}
            row = {"name": name, "source_file": source_file, "requested_size": requested,
                   "source_size": list(original.size), "source_mode": original.mode,
                   "source_bytes": source.stat().st_size,
                   "export_size": list(exported.size), "export_bytes": target.stat().st_size,
                   "transparent_pixels": histogram[0], "visible_bounds": bounds,
                   "seam_metrics": seams, "errors": errors}
            results.append(row)
            safe = html.escape(name)
            url = f"candidates/{safe}.webp"
            preview = (f'<div class="tile" style="background-image:url({url})"></div>' if not transparent else
                       f'<div class="previews"><div class="light"><img src="{url}"></div>'
                       f'<div class="dark"><img src="{url}"></div></div>')
            sizes = ""
            if name.startswith("pod-") and name.split("-")[1] in ("copper", "blue", "yellow", "white"):
                sizes = '<div class="sizes">' + ''.join(
                    f'<span><img src="{url}" style="width:{size}px"> {size} px</span>'
                    for size in (24, 32, 40)) + '</div>'
            notes = "; ".join(errors) or "Technical alpha/frame checks passed; visual review required"
            if list(original.size) != requested:
                notes += f"; generated size {original.width}×{original.height}, requested {requested[0]}×{requested[1]}"
            if seams:
                notes += f"; seam RGB difference {seams} (diagnostic, not proof of seamlessness)"
            cards.append(f'<article id="{safe}"><h2>{safe}</h2>{preview}{sizes}<p>{html.escape(notes)}</p>'
                         f'<a href="sources/{html.escape(source_file)}">Original PNG</a> · <a href="{url}">Candidate WebP</a></article>')
    total = sum(row["export_bytes"] for row in results)
    report = {"date": "2026-10-11", "status": "awaiting_visual_acceptance",
              "asset_count": len(results), "export_bytes": total,
              "budget_bytes": BUDGET, "within_budget": total <= BUDGET, "assets": results}
    (ART / "validation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    gallery = '''<!doctype html><html lang="ru"><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>SRO — приёмка графики гонок</title><style>
body{background:#211c19;color:#eee;font:16px system-ui;margin:24px}h1{font-size:24px}
main{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,440px),1fr));gap:20px}
article{background:#352c25;padding:16px;border-radius:12px;overflow:hidden}h2{font-size:16px}
.previews{display:flex;gap:8px}.previews>div{width:50%;display:grid;place-items:center;min-height:180px}
.light{background:#eee}.dark{background:#101820}img{max-width:100%;max-height:240px;object-fit:contain}
.sizes{display:flex;flex-wrap:wrap;align-items:center;gap:24px;background:#704632;padding:16px;margin-top:12px}
.sizes span{display:flex;align-items:center;gap:8px}.sizes img{max-height:none}
.tile{width:100%;aspect-ratio:1;background-size:33.333333% 33.333333%}
p{line-height:1.5;font-size:13px;color:#ddd}a{color:#8ce5e4}</style>
<h1>Красный каньон — приёмка 22 ассетов</h1>
<p>Светлый и тёмный фон; болиды 24/32/40 px; поверхности повторены 3×3.
Исходники сохранены без изменений. Экспорты — кандидаты; игровые файлы пока не заменены.</p>
'''
    gallery += f'<p>Загрузка набора: {total / 1024 / 1024:.2f} МБ / 8 МБ.</p><main>' + ''.join(cards) + '</main></html>'
    (ART / "review.html").write_text(gallery, encoding="utf-8")
    errors = sum(len(row["errors"]) for row in results)
    print(f'{len(results)} assets; {total / 1024 / 1024:.2f} MiB; {errors} technical review flags')
    print(ART / "review.html")
    if total > BUDGET:
        raise SystemExit("Export exceeds 8 MiB budget")


if __name__ == "__main__":
    prepare()
