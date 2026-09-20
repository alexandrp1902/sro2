"""Нарезка листов art/space/*.png в спрайты игры: client/public/sprites/*.webp и client/src/render/spriteMeta.json.

Листы — квадратные сетки 2×2 или 3×3 на прозрачном фоне (см. art/space/README.md). Объект может чуть вылезать
за свою клетку (кольцо планеты), поэтому границы ищутся по связным пятнам непрозрачности, а пятно относится
к клетке по своему центру. Кадры взрыва режутся по клеткам целиком — иначе кадры разного размера «прыгали» бы.

Запуск из корня репозитория: python tools/sprites.py  (нужны Pillow с WebP и numpy).
Перезапускать только после замены листов — результат лежит в git.
"""

import json
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "art" / "space"
OUT = ROOT / "client" / "public" / "sprites"
META = ROOT / "client" / "src" / "render" / "spriteMeta.json"

# Пиксель с меньшей прозрачностью — фон: у генератора по краям остаётся цветной «пух» с альфой 1–2.
ALPHA_CUT = 24
# Пятна меньше этой доли клетки — мусор, в границы объекта не входят.
MIN_BLOB = 0.002
QUALITY = 88

# Листы, нарисованные носом вверх. В игре снаряд поворачивается по курсу (rotation = atan2(dy, dx)),
# то есть кадр обязан смотреть вправо; эти четыре пришли из генератора вертикальными, и без поворота
# снаряд летел боком, да ещё и растягивался по короткой стороне. Поворачиваем один раз, при нарезке.
NOSE_UP = {
    "weapon-effects/weapon-shots-rail",
    "weapon-effects/weapon-shots-ion",
    "weapon-effects/weapon-shots-torpedo",
    "weapon-effects/weapon-shots-flak",
}

# лист, колонок, строк, имена по порядку (слева направо, сверху вниз; None — не нужен), длинная сторона, режим
SHEETS = [
    ("ships", 2, 2, ["light", "medium", "heavy", "pirate"], 256, "ship"),
    ("stations", 2, 2, ["ring", "mining", "fortress", "habitat"], 512, "trim"),
    ("suns", 2, 2, ["yellow", "orange", "blue", "red"], 640, "trim"),
    ("planets", 2, 2, ["terran", "desert", "ice", "gas"], 512, "trim"),
    ("meteors", 3, 3, [f"m{i}" for i in range(9)], 192, "trim"),
    ("resources", 3, 3,
     ["metal", "ore", "energy", "titanium", "crystals", "rareMetal", "plasma", "container", "electronics"], 128, "trim"),
    ("weapons", 2, 2, ["pulse", "laser", "plasma", "rockets"], 128, "trim"),
    ("modules", 2, 2, ["engine", "shield", "cargo", "scanner"], 128, "trim"),
    ("explosions", 3, 3, [f"f{i}" for i in range(9)], 256, "cell"),
    ("weapon-shots", 3, 3,
     ["bolt", "bolt-flash", "bolt-hit", "beam", "beam-flash", "beam-hit", "orb", "orb-flash", "orb-hit"], 192, "trim"),
    # Эффекты нового оружия (M11): по листу 3×1 на каждое — снаряд, вспышка, попадание.
    ("weapon-effects/weapon-shots-rail", 3, 1, ["rail", "rail-flash", "rail-hit"], 192, "trim", "weapon-shots"),
    ("weapon-effects/weapon-shots-ion", 3, 1, ["ion", "ion-flash", "ion-hit"], 192, "trim", "weapon-shots"),
    ("weapon-effects/weapon-shots-torpedo", 3, 1, ["torpedo", "torpedo-flash", "torpedo-hit"], 192, "trim", "weapon-shots"),
    ("weapon-effects/weapon-shots-flak", 3, 1, ["flak", "flak-flash", "flak-hit"], 192, "trim", "weapon-shots"),
]

# Отдельные картинки (одна PNG — один спрайт): файл под art/space, имя в игре, длинная сторона.
SINGLES = [
    # Корабли NPC (пачка A): свои силуэты, чтобы их не путали с кораблями пилотов.
    *[(f"npc-ships/ships-{n}", f"ships-{n}", 256) for n in
      ["ranger", "ranger-heavy", "trader-hauler", "trader-convoy", "pirate-raider", "pirate-brute", "pirate-flagship", "drone"]],
    # Корпуса пилотов (M11): пламени у них нет — клиент подставляет чужое (см. render/ship.ts).
    *[(f"ships-extra/ships-{n}", f"ships-{n}", 256) for n in
      ["scout", "interceptor", "industrial", "frigate", "freighter", "cruiser"]],
    # Новые звёзды, планеты и станции (пачка B).
    *[(f"galaxy/planets-{n}", f"planets-{n}", 512) for n in ["barren", "jungle", "lava", "ocean", "ringed", "toxic"]],
    *[(f"galaxy/stations-{n}", f"stations-{n}", 512) for n in ["outpost", "pirate", "ranger", "trade"]],
    *[(f"galaxy/suns-{n}", f"suns-{n}", 640) for n in ["white", "binary"]],
    # Иконки нового снаряжения (M11).
    *[(f"equipment-extra/weapons-{n}", f"weapons-{n}", 128) for n in
      ["cannon", "heavy-laser", "railgun", "ion", "point-defense", "torpedoes"]],
    *[(f"equipment-extra/modules-{n}", f"modules-{n}", 128) for n in
      ["reactor", "fuel-tank", "military-radar", "afterburner", "repair", "cooling"]],
    # Товары рынка (M12). Имя спрайта — resources-<id товара в loot.json>: itemSprite() найдёт их без алиасов.
    *[(f"trade-missions/goods-{f}", f"resources-{n}", 128) for f, n in
      [("food", "food"), ("medicine", "medicine"), ("machinery", "machinery"),
       ("luxury", "luxury"), ("weapons", "arms"), ("fuel-cells", "fuelCells")]],
    # Письмо для заданий-курьеров (M14): нарезано заранее, кода это пока не касается.
    ("trade-missions/item-letter", "item-letter", 128),
]


def blobs(alpha: np.ndarray, step: int = 4):
    """Связные пятна непрозрачности на уменьшенной маске: (площадь, cx, cy, x0, y0, x1, y1) в пикселях листа."""
    h, w = alpha.shape
    mask = alpha[: h - h % step, : w - w % step].reshape(h // step, step, w // step, step).max(axis=(1, 3)) > ALPHA_CUT
    seen = np.zeros_like(mask)
    mh, mw = mask.shape
    found = []
    for sy in range(mh):
        for sx in range(mw):
            if not mask[sy, sx] or seen[sy, sx]:
                continue
            queue = deque([(sy, sx)])
            seen[sy, sx] = True
            ys, xs = [], []
            while queue:
                y, x = queue.popleft()
                ys.append(y)
                xs.append(x)
                for ny, nx in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)):
                    if 0 <= ny < mh and 0 <= nx < mw and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        queue.append((ny, nx))
            found.append((
                len(ys) * step * step,
                (sum(xs) / len(xs) + 0.5) * step,
                (sum(ys) / len(ys) + 0.5) * step,
                min(xs) * step, min(ys) * step, (max(xs) + 1) * step, (max(ys) + 1) * step,
            ))
    return found


def bounds(sheet: Image.Image, cols: int, rows: int):
    """Рамка объекта в каждой клетке по пятнам, чей центр в этой клетке."""
    alpha = np.asarray(sheet)[..., 3]
    h, w = alpha.shape
    cw, ch = w / cols, h / rows
    boxes = [None] * (cols * rows)
    for area, cx, cy, x0, y0, x1, y1 in blobs(alpha):
        if area < MIN_BLOB * cw * ch:
            continue
        i = min(rows - 1, int(cy // ch)) * cols + min(cols - 1, int(cx // cw))
        b = boxes[i]
        boxes[i] = (x0, y0, x1, y1) if b is None else (min(b[0], x0), min(b[1], y0), max(b[2], x1), max(b[3], y1))
    # Уточняем рамку по настоящей альфе (маска была огрублена шагом).
    result = []
    for b in boxes:
        x0, y0, x1, y1 = b
        x0, y0 = max(0, x0 - 4), max(0, y0 - 4)
        x1, y1 = min(w, x1 + 4), min(h, y1 + 4)
        region = alpha[y0:y1, x0:x1] > ALPHA_CUT
        ys, xs = np.nonzero(region)
        result.append((x0 + xs.min(), y0 + ys.min(), x0 + xs.max() + 1, y0 + ys.max() + 1))
    return result


def clean(img: Image.Image) -> Image.Image:
    """Прозрачный «пух» генератора — в ноль, чтобы при масштабе он не подкрашивал края."""
    a = np.asarray(img).copy()
    a[a[..., 3] < 4] = 0
    return Image.fromarray(a, "RGBA")


def fit(img: Image.Image, longest: int) -> Image.Image:
    """Уменьшение в премультиплицированной альфе: цвет прозрачных пикселей не протекает в края."""
    k = min(1.0, longest / max(img.size))
    if k >= 1:
        return img
    size = (max(1, round(img.width * k)), max(1, round(img.height * k)))
    return img.convert("RGBa").resize(size, Image.LANCZOS).convert("RGBA")


def save(img: Image.Image, name: str) -> None:
    img.save(OUT / f"{name}.webp", "WEBP", quality=QUALITY, method=6)


def split_ship(img: Image.Image):
    """
    Корпус и пламя двигателей отдельно: пламя рисуется по тяге. Низ корпуса — последняя строка, где ещё
    заметно серого металла; всё ниже — пламя. Возвращает (корпус, пламя, низ корпуса в пикселях).
    """
    a = np.asarray(img).astype(int)
    r, g, b, al = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    opaque = al > 128
    blue = (b > r + 50) & (b > g - 10)
    metal = opaque & ~blue
    rows = metal.sum(axis=1)
    bottom = int(np.nonzero(rows > img.width * 0.03)[0].max()) + 1
    hull = a.copy()
    flame = a.copy()
    hull[bottom:] = 0
    flame[:bottom] = 0
    to = lambda x: Image.fromarray(x.astype(np.uint8), "RGBA")
    return to(hull), to(flame), bottom


def single(img: Image.Image) -> Image.Image:
    """Одиночная картинка: обрезаем прозрачные поля генератора по настоящей альфе."""
    alpha = np.asarray(img)[..., 3]
    ys, xs = np.nonzero(alpha > ALPHA_CUT)
    if len(xs) == 0:
        return img
    return img.crop((int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1))


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("*.webp"):
        old.unlink()
    meta = {}
    for sheet in SHEETS:
        sheet_name, cols, rows, names, longest, mode = sheet[:6]
        prefix = sheet[6] if len(sheet) > 6 else sheet_name
        sheet = clean(Image.open(SRC / f"{sheet_name}.png").convert("RGBA"))
        w, h = sheet.size
        if mode == "cell":
            cw, ch = w // cols, h // rows
            boxes = [((i % cols) * cw, (i // cols) * ch, (i % cols + 1) * cw, (i // cols + 1) * ch) for i in range(cols * rows)]
        else:
            boxes = bounds(sheet, cols, rows)
        for name, box in zip(names, boxes):
            if name is None:
                continue
            key = f"{prefix}-{name}"
            cell = sheet.crop(box)
            # -90° — по часовой стрелке: нос с верха кадра уезжает вправо.
            if sheet_name in NOSE_UP:
                cell = cell.rotate(-90, expand=True)
            img = fit(cell, longest)
            entry = {"w": img.width, "h": img.height}
            if mode == "ship":
                hull, flame, bottom = split_ship(img)
                save(hull, key)
                save(flame, f"{key}-flame")
                # Длина корпуса без пламени: по ней корабль масштабируется под размер корпуса в игре.
                entry["body"] = bottom
            else:
                save(img, key)
            meta[key] = entry
            print(f"{key:28} {img.width}x{img.height}")
    for path, key, longest in SINGLES:
        img = fit(single(clean(Image.open(SRC / f"{path}.png").convert("RGBA"))), longest)
        save(img, key)
        meta[key] = {"w": img.width, "h": img.height}
        print(f"{key:28} {img.width}x{img.height}")
    META.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    total = sum(f.stat().st_size for f in OUT.glob("*.webp"))
    print(f"{len(list(OUT.glob('*.webp')))} файлов, {total / 1024:.0f} КБ")


if __name__ == "__main__":
    main()
