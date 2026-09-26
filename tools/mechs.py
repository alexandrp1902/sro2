"""Нарезка арта мехов для боя M21: client/public/mechs/*.webp и client/src/mech/rigMeta.json.

Слои меха — четыре листа P1 (art/mechs/production/sources), каждый 4×2 кадра по 8 направлениям. Прямоугольники
кадров, масштаб, сдвиг и порядок слоёв берутся из art/mechs/production/medium-rig.json — того же рига, что проверен
в браузере (rig-renderer.js). Кадр режется и приводится к квадрату, как делает рендерер рига: он рисует источник
в квадрат cellSize × scale.

Тайлы пустыни (art/mechs/tiles/desert-*) — стенд-ин до партии P4: 128 px, здание — 256 px на квадрат 2×2.

Эффекты боя (art/mechs/effects/mech-fx-*) — листы 3×2 кадра по 512 px, кадр по центру клетки: режутся сеткой
в fx-<имя>-<n>.webp по 192 px, число кадров уходит в rigMeta.json.

Запуск из корня репозитория: python tools/mechs.py  (нужен Pillow с WebP).
Перезапускать только после замены листов — результат лежит в git.
"""

import json
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
RIG = ROOT / "art" / "mechs" / "production" / "medium-rig.json"
TILES = ROOT / "art" / "mechs" / "tiles"
EFFECTS = ROOT / "art" / "mechs" / "effects"
OUT = ROOT / "client" / "public" / "mechs"
META = ROOT / "client" / "src" / "mech" / "rigMeta.json"

# Сторона кадра слоя в игре. Мех на поле — около 1.6 клетки по 72 px с зумом до ×1.5: 256 хватает с запасом.
LAYER_SIDE = 256
TILE_SIDE = 128
QUALITY = 88

# Короткие имена слоёв в игре — по роли, а не по файлу: рука с автопушкой завтра станет любой правой рукой.
LAYERS = {
    "chassis-medium-biped": "chassis",
    "body-medium": "body",
    "arm-autocannon-right": "right",
    "shield-light-left": "left",
}

# Эффект в игре → лист и его сетка (столбцы, ряды). Кадры идут по рядам слева направо.
FX_SIDE = 192
FX_FILES = {
    "hit": ("mech-fx-hit.png", 3, 2),
    "explosion": ("mech-fx-explosion.png", 3, 2),
    "block": ("mech-fx-shield-block.png", 3, 2),
}

# Клетка поля → файл тайла. Земля и камни — сплошные, ящик, стена и здание — поверх земли.
TILE_FILES = {
    "ground-1": "desert-ground-1.png",
    "ground-2": "desert-ground-2.png",
    "rough": "desert-rough.png",
    "crate": "desert-crate.png",
    "wall": "desert-wall.png",
    "building": "desert-building.png",
}


def save(image: Image.Image, name: str) -> None:
    image.save(OUT / f"{name}.webp", "WEBP", quality=QUALITY, method=6)


def main() -> None:
    rig = json.loads(RIG.read_text(encoding="utf-8"))
    OUT.mkdir(parents=True, exist_ok=True)
    directions = rig["directions"]

    for asset, layer in LAYERS.items():
        spec = rig["assets"][asset]
        sheet = Image.open(RIG.parent / spec["source"]).convert("RGBA")
        for i, (x, y, w, h) in enumerate(spec["frames"]):
            frame = sheet.crop((x, y, x + w, y + h)).resize((LAYER_SIDE, LAYER_SIDE), Image.LANCZOS)
            save(frame, f"{layer}-{directions[i].lower()}")

    for name, file in TILE_FILES.items():
        side = TILE_SIDE * 2 if name == "building" else TILE_SIDE
        tile = Image.open(TILES / file).convert("RGBA").resize((side, side), Image.LANCZOS)
        save(tile, f"tile-{name}")

    fx = {}
    for name, (file, cols, rows) in FX_FILES.items():
        sheet = Image.open(EFFECTS / file).convert("RGBA")
        cw, ch = sheet.width // cols, sheet.height // rows
        for n in range(cols * rows):
            x, y = n % cols * cw, n // cols * ch
            frame = sheet.crop((x, y, x + cw, y + ch)).resize((FX_SIDE, FX_SIDE), Image.LANCZOS)
            save(frame, f"fx-{name}-{n}")
        fx[name] = cols * rows

    frames = {}
    for i, direction in enumerate(directions):
        frame = rig["frames"][direction]
        frames[direction.lower()] = {
            "layers": {
                LAYERS[asset]: {
                    "x": round(t["x"], 2),
                    "y": round(t["y"], 2),
                    "scale": t["scale"],
                    "z": t["z"],
                }
                for asset, t in frame["transforms"].items()
                if t.get("enabled", True)
            },
            "muzzle": [round(v, 2) for v in frame["anchors"]["muzzle"]],
        }
    meta = {
        "cellSize": rig["cellSize"],
        "groundPivot": rig["groundPivot"],
        "directions": [d.lower() for d in directions],
        "frames": frames,
        "fx": fx,
    }
    META.parent.mkdir(parents=True, exist_ok=True)
    META.write_text(json.dumps(meta, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{len(LAYERS) * len(directions)} layers, {len(TILE_FILES)} tiles, {sum(fx.values())} fx frames -> {OUT}")


if __name__ == "__main__":
    main()
