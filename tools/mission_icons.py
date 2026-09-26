"""Недостающие значки видов заданий, нарисованные кодом: art/space/mission-reputation/mission-*.png.

Пачка C нарисовала пять значков (сопровождение, патруль, курьер, метеорит, мех), а видов работы на доске
восемь. Остальные — простые фигуры в том же духе: белый плоский силуэт на прозрачном, крупные скруглённые
формы без мелочи, чтобы читались в 16–18 px. Рисуется вчетверо крупнее и уменьшается — края гладкие.

  collect  — кирка над самородком: «добыть руду»;
  kill     — череп: «сбить пиратов»;
  deliver  — ящик и стрелка: «отвезти груз»;
  defend   — щит с планетой: «отстоять поселение».

Метеорит в прицеле (пачка C) остаётся охоте на камни — у «собрать» теперь свой значок.

Запуск из корня репозитория: python tools/mission_icons.py, потом python tools/sprites.py.
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "art" / "space" / "mission-reputation"

SIZE = 512
SS = 4  # суперсэмплинг
S = SIZE * SS


def canvas():
    mask = Image.new("L", (S, S), 0)
    return mask, ImageDraw.Draw(mask)


def p(x: float, y: float) -> tuple[float, float]:
    """Доли кадра → пиксели холста."""
    return (x * S, y * S)


def box(x0: float, y0: float, x1: float, y1: float) -> list[float]:
    return [x0 * S, y0 * S, x1 * S, y1 * S]


def save(mask: Image.Image, name: str) -> None:
    small = mask.resize((SIZE, SIZE), Image.LANCZOS)
    white = Image.new("RGBA", (SIZE, SIZE), (250, 250, 250, 255))
    white.putalpha(small)
    white.save(OUT / f"mission-{name}.png")
    print(f"mission-{name}.png")


def collect() -> None:
    m, d = canvas()
    # Самородок внизу справа: неровный многоугольник, грани прорезаны.
    nugget = [p(0.50, 0.62), p(0.66, 0.50), p(0.86, 0.56), p(0.94, 0.76), p(0.80, 0.92), p(0.56, 0.90), p(0.44, 0.76)]
    d.polygon(nugget, fill=255)
    w = int(0.03 * S)
    d.line([p(0.66, 0.50), p(0.68, 0.72), p(0.94, 0.76)], fill=0, width=w, joint="curve")
    d.line([p(0.68, 0.72), p(0.56, 0.90)], fill=0, width=w)
    # Кирка рисуется стоя — рукоять вертикально, серп головки поперёк её верха — и поворачивается целиком:
    # у повёрнутой дуги нет удобного API, а у картинки есть.
    pick = Image.new("L", (S, S), 0)
    k = ImageDraw.Draw(pick)
    k.rounded_rectangle(box(0.455, 0.20, 0.545, 0.94), radius=0.045 * S, fill=255)
    # Головка — толстый серп: большой круг минус сдвинутый вниз, обрезанный по ширине.
    head = Image.new("L", (S, S), 0)
    h = ImageDraw.Draw(head)
    h.ellipse(box(0.06, 0.10, 0.94, 0.98), fill=255)
    h.ellipse(box(0.02, 0.24, 0.98, 1.20), fill=0)
    h.rectangle(box(0.0, 0.40, 1.0, 1.0), fill=0)
    pick.paste(255, (0, 0), head)
    pick = pick.rotate(-38, resample=Image.BICUBIC, center=(S * 0.5, S * 0.5))
    pick = pick.resize((int(S * 0.86), int(S * 0.86)), Image.LANCZOS)
    layer = Image.new("L", (S, S), 0)
    layer.paste(pick, (int(-0.04 * S), int(-0.02 * S)))
    # Камень поверх кирки, с зазором по контуру: там, где лежит увеличенный силуэт камня, кирку стираем.
    # Иначе в 16 px два силуэта слипаются в одно пятно.
    cx = sum(x for x, _ in nugget) / len(nugget)
    cy = sum(y for _, y in nugget) / len(nugget)
    halo = [(cx + (x - cx) * 1.16, cy + (y - cy) * 1.16) for x, y in nugget]
    ImageDraw.Draw(layer).polygon(halo, fill=0)
    m.paste(255, (0, 0), layer)
    save(m, "collect")


def kill() -> None:
    m, d = canvas()
    # Череп: купол, скулы и челюсть, глазницы и нос вырезаны.
    d.ellipse(box(0.16, 0.10, 0.84, 0.70), fill=255)
    d.rounded_rectangle(box(0.30, 0.52, 0.70, 0.88), radius=0.08 * S, fill=255)
    d.ellipse(box(0.25, 0.34, 0.45, 0.54), fill=0)
    d.ellipse(box(0.55, 0.34, 0.75, 0.54), fill=0)
    d.polygon([p(0.50, 0.56), p(0.44, 0.66), p(0.56, 0.66)], fill=0)
    # Зубы — прорези в челюсти.
    for x in (0.42, 0.50, 0.58):
        d.rounded_rectangle(box(x - 0.018, 0.74, x + 0.018, 0.90), radius=0.015 * S, fill=0)
    save(m, "kill")


def deliver() -> None:
    m, d = canvas()
    # Ящик с лентой.
    d.rounded_rectangle(box(0.08, 0.26, 0.58, 0.76), radius=0.06 * S, fill=255)
    d.rectangle(box(0.08, 0.44, 0.58, 0.50), fill=0)
    d.rectangle(box(0.30, 0.26, 0.36, 0.44), fill=0)
    # Стрелка вправо: куда везти.
    d.rounded_rectangle(box(0.54, 0.44, 0.78, 0.58), radius=0.03 * S, fill=255)
    d.polygon([p(0.72, 0.28), p(0.94, 0.51), p(0.72, 0.74)], fill=255)
    # Зазор между ящиком и стрелкой, чтобы силуэт не слипался.
    d.line([p(0.61, 0.40), p(0.61, 0.62)], fill=0, width=int(0.03 * S))
    save(m, "deliver")


def defend() -> None:
    m, d = canvas()
    # Щит: плоский верх, бока сходятся к острию.
    pts = []
    for i in range(0, 41):
        t = i / 40
        # Правая половина: от верхнего угла до острия по кривой.
        x = 0.50 + 0.36 * math.cos(t * math.pi / 2) ** 0.8
        y = 0.18 + 0.72 * t ** 1.4
        pts.append(p(x, y))
    left = [(S - x, y) for x, y in reversed(pts)]
    # Верх чуть выгнут вверх; дальше по правому боку вниз к острию и по левому обратно.
    d.polygon([p(0.14, 0.18), p(0.50, 0.08), p(0.86, 0.18)] + pts + left, fill=255)
    # Планета внутри: вырез кольцом, потом шар поменьше — щит закрывает поселение.
    d.ellipse(box(0.31, 0.26, 0.69, 0.64), fill=0)
    d.ellipse(box(0.37, 0.32, 0.63, 0.58), fill=255)
    save(m, "defend")


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    collect()
    kill()
    deliver()
    defend()
