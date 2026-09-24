"""Проверка точек сопел client/src/render/shipNozzles.ts по самим спрайтам.

Позиции сопел сняты с картинок на глаз, и промах в десяток пикселей виден в игре: факел либо висит
в пустоте за кормой, либо утоплен в корпус и перекрыт им. Скрипт считает то же, что считает рендер
(ship.ts: local = x - w/2, y - body/2; длина языка = min(body * 0.6, width * 4)), и говорит про каждое
сопло, попало ли оно в силуэт и как далеко от среза кормы в своей колонке.

Запуск из корня репозитория:
    python tools/nozzles.py               — отчёт и контактный лист .artifacts/nozzles.png
    python tools/nozzles.py --no-sheet    — только отчёт (код возврата 1, если есть промахи)

Нужны Pillow и numpy. Лист смотреть глазами: цифры ловят грубые промахи, а «красиво ли» — нет.
"""

import argparse
import json
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
SPRITES = ROOT / "client" / "public" / "sprites"
META = ROOT / "client" / "src" / "render" / "spriteMeta.json"
TABLE = ROOT / "client" / "src" / "render" / "shipNozzles.ts"
SHEET = ROOT / ".artifacts" / "nozzles.png"

# Пиксель прозрачнее — фон (как в tools/sprites.py).
ALPHA_CUT = 24
# Насколько срез кормы в колонке сопла может отстоять от самой точки: выше — факел торчит из корпуса,
# ниже — висит за кормой в пустоте. Ядро градиента сидит на 0.32 длины, поэтому небольшой запас внутрь нужен.
DELTA_MIN, DELTA_MAX = -1, 10

# Контактный лист: кораблей в ряд, высота клетки и поля вокруг корпуса под факел.
COLUMNS = 7
CELL_H = 240
PAD = 110


def nozzles() -> dict[str, list[tuple[int, int, int]]]:
    """Таблица из shipNozzles.ts: имя спрайта → список (x, y, ширина)."""
    text = TABLE.read_text(encoding="utf-8")
    out: dict[str, list[tuple[int, int, int]]] = {}
    for name, body in re.findall(r"'(ships-[\w-]+)':\s*\[(.*?)\],\n", text, re.S):
        out[name] = [(int(x), int(y), int(w)) for x, y, w in re.findall(r"\[(\d+),\s*(\d+),\s*(\d+)\]", body)]
    return out


def glow(width: int, length: int) -> np.ndarray:
    """Тот же язык пламени, что печёт exhaust.ts: голубое ядро с растушёвкой, вытянутое по корме."""
    stops = [
        (0.00, (0xEF, 0xFF, 0xFF, 1.00)),
        (0.12, (0xB8, 0xF5, 0xFF, 1.00)),
        (0.30, (0x38, 0xCC, 0xFF, 0.87)),
        (0.55, (0x13, 0x9A, 0xFF, 0.47)),
        (0.80, (0x12, 0x6A, 0xFF, 0.13)),
        (1.00, (0x12, 0x6A, 0xFF, 0.00)),
    ]
    h, w = max(1, length), max(1, width)
    ys, xs = np.mgrid[0:h, 0:w]
    # Клетка градиента — 96×192 с центром (48, 44) в «сжатых» по Y координатах и радиусом 47.
    gx = xs / w * 96.0
    gy = ys / h * 192.0 / 2.0
    t = np.clip(np.hypot(gx - 48.0, gy - 44.0) / 47.0, 0.0, 1.0)
    rgba = np.zeros((h, w, 4), dtype=np.float32)
    for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
        band = (t >= t0) & (t <= t1)
        k = ((t - t0) / (t1 - t0))[band][:, None]
        rgba[band] = np.array(c0, dtype=np.float32) * (1 - k) + np.array(c1, dtype=np.float32) * k
    rgba[..., :3] *= rgba[..., 3:4]  # сложение цветов: слабая альфа = слабый свет
    return rgba


def tail(alpha: np.ndarray, x: int, y: int, width: int) -> int | None:
    """Где кончается сам кожух двигателя в колонке сопла.

    Нижний непрозрачный пиксель колонки для этого не годится: у «Флагмана» и «Галеона» ниже сопла
    тянется тонкий шип обтекателя, и по нему выходило, что факел утоплен в корпус на 16 px. Поэтому
    идём вниз, пока сплошная полоса вокруг сопла шире половины его ширины.
    """
    keep = max(3, width // 2)
    last = None
    for row in range(y, alpha.shape[0]):
        if alpha[row, x] <= ALPHA_CUT:
            break
        left = x
        while left > 0 and alpha[row, left - 1] > ALPHA_CUT:
            left -= 1
        right = x
        while right + 1 < alpha.shape[1] and alpha[row, right + 1] > ALPHA_CUT:
            right += 1
        if right - left + 1 < keep:
            break
        last = row
    return last


def check(name: str, meta: dict, points: list[tuple[int, int, int]]) -> list[dict]:
    """Про каждое сопло: в силуэте ли оно и как далеко срез кожуха в его колонке."""
    image = Image.open(SPRITES / f"{name}.webp").convert("RGBA")
    alpha = np.array(image)[..., 3]
    rows = []
    for x, y, width in points:
        inside = 0 <= x < alpha.shape[1] and 0 <= y < alpha.shape[0] and alpha[y, x] > ALPHA_CUT
        end = tail(alpha, x, y, width) if inside else None
        delta = None if end is None else end - y
        verdict = "ok"
        if not inside:
            verdict = "мимо корпуса"
        elif delta is None or delta < DELTA_MIN:
            verdict = "за кормой"
        elif delta > DELTA_MAX:
            verdict = "утоплено"
        rows.append({"x": x, "y": y, "w": width, "tail": end, "delta": delta, "verdict": verdict})
    return rows


def cell(name: str, meta: dict, points: list[tuple[int, int, int]], scale: float) -> Image.Image:
    """Корабль с факелами на полной тяге — ровно по формулам ship.ts."""
    image = Image.open(SPRITES / f"{name}.webp").convert("RGBA")
    w, h = image.size
    body = meta.get("body", h)
    # Факел уходит за нижний край кадра: в игре его ничто не обрезает, и на листе тоже не должно.
    pad = PAD
    canvas = np.zeros((h + pad, w + 2 * pad, 4), dtype=np.float32)
    for x, y, width in points:
        fire_w = max(1, round(width * 1.65))
        fire_h = max(1, round(min(body * 0.6, width * 4)))
        patch = glow(fire_w, fire_h)
        left = round(x + pad - fire_w / 2)
        top = round(y - fire_h * 0.32)
        x0, y0 = max(0, left), max(0, top)
        x1, y1 = min(canvas.shape[1], left + fire_w), min(canvas.shape[0], top + fire_h)
        if x0 >= x1 or y0 >= y1:
            continue
        cut = patch[y0 - top : y1 - top, x0 - left : x1 - left]
        canvas[y0:y1, x0:x1, :3] += cut[..., :3]
        canvas[y0:y1, x0:x1, 3] = np.maximum(canvas[y0:y1, x0:x1, 3], cut[..., 3])
    fire = Image.fromarray(np.clip(canvas * 255, 0, 255).astype(np.uint8), "RGBA")
    out = Image.new("RGBA", fire.size, (0, 0, 0, 0))
    out.alpha_composite(fire)       # огонь за корпусом, как в ship.ts
    out.alpha_composite(image, (pad, 0))
    return out.resize((max(1, round(out.width * scale)), max(1, round(out.height * scale))), Image.LANCZOS)


def sheet(meta: dict, table: dict[str, list[tuple[int, int, int]]], path: Path) -> None:
    names = sorted(table)
    scale = (CELL_H - 34) / (256 + PAD)
    width = max(round((meta[n]["w"] + 2 * PAD) * scale) for n in names) + 12
    rows = (len(names) + COLUMNS - 1) // COLUMNS
    page = Image.new("RGBA", (width * COLUMNS, CELL_H * rows), (10, 14, 22, 255))
    draw = ImageDraw.Draw(page)
    try:
        font = ImageFont.truetype("segoeui.ttf", 13)
    except OSError:
        font = ImageFont.load_default()
    for i, name in enumerate(names):
        art = cell(name, meta[name], table[name], scale)
        left = (i % COLUMNS) * width + (width - art.width) // 2
        top = (i // COLUMNS) * CELL_H + 6
        page.alpha_composite(art, (left, top))
        label = f"{name[len('ships-'):]} / {len(table[name])}"
        draw.text(((i % COLUMNS) * width + 10, (i // COLUMNS) * CELL_H + CELL_H - 24), label, fill=(190, 210, 235, 255), font=font)
    path.parent.mkdir(parents=True, exist_ok=True)
    page.convert("RGB").save(path)


def main() -> int:
    parser = argparse.ArgumentParser(description="Проверка точек сопел по спрайтам кораблей.")
    parser.add_argument("--no-sheet", action="store_true", help="только отчёт, без контактного листа")
    parser.add_argument("--sheet", type=Path, default=SHEET, help="куда положить контактный лист")
    args = parser.parse_args()
    # Консоль Windows по умолчанию в cp1251: без этого отчёт падает на стрелке и кириллице.
    sys.stdout.reconfigure(encoding="utf-8")

    meta = json.loads(META.read_text(encoding="utf-8"))
    table = nozzles()
    missing = [n for n in meta if n.startswith("ships-") and n not in table]
    for name in missing:
        print(f"{name}: сопел нет в таблице")
    bad = len(missing)
    for name in sorted(table):
        rows = check(name, meta.get(name, {}), table[name])
        flags = [r for r in rows if r["verdict"] != "ok"]
        bad += len(flags)
        marks = " ".join(f"({r['x']},{r['y']})Δ{r['delta']}{'' if r['verdict'] == 'ok' else ' ' + r['verdict']}" for r in rows)
        print(f"{'!' if flags else ' '} {name[len('ships-'):]:<16} {marks}")
    if not args.no_sheet:
        sheet(meta, table, args.sheet)
        print(f"\nконтактный лист: {args.sheet}")
    print(f"\nсопел с промахом: {bad}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
