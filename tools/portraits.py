# Портреты сюжетных героев: art/story/<кампания>/sources/portrait-*.png -> client/public/portraits/*.webp.
# Отдельно от sprites.py: портрет — картинка в DOM-карточке диалога, а не текстура Pixi. Попади он
# в spriteMeta.json, его качал бы каждый вход в игру, хотя видят его только в сюжетном диалоге.
# Генератор отдаёт квадрат 1254² без прозрачности — сводим к 256² по центру.
# Перезапускать только после новых картинок — результат лежит в git.
#   python tools/portraits.py

from pathlib import Path

from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "client" / "public" / "portraits"

SIZE = (256, 256)
QUALITY = 88

# (файл под art/, имя в игре). Имя ищет PORTRAITS в client/src/ui/dialog.ts.
PORTRAITS = [
    ("story/quiet-war/sources/portrait-eva", "eva"),
    ("story/quiet-war/sources/portrait-holt", "holt"),
    ("story/quiet-war/sources/portrait-dan", "dan"),
    # Контакт на станции пока нигде не говорит: портрет готов, подключится вместе с его репликами.
    ("story/quiet-war/sources/portrait-contact", "contact"),
]


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for source, name in PORTRAITS:
        path = ROOT / "art" / f"{source}.png"
        if not path.exists():
            print(f"{name:10} пропущен: нет {path.relative_to(ROOT)}")
            continue
        image = ImageOps.fit(Image.open(path).convert("RGB"), SIZE, Image.LANCZOS, centering=(0.5, 0.5))
        target = OUT / f"{name}.webp"
        image.save(target, "WEBP", quality=QUALITY, method=6)
        print(f"{name:10} {SIZE[0]}x{SIZE[1]}  {target.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
