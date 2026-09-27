# Ева Морен — обновление 2026-09-27

Новый портрет создан встроенным image_gen по прежнему изображению как референсу стиля, одежды и композиции. Ева — молодая взрослая девушка 22–25 лет, с мягкими чертами, лёгкой улыбкой и собранными волосами. Сохранены наушники, потёртая рабочая куртка и следы грязи.

Исходник: `sources/portrait-eva.png`. Игровая версия: `client/public/portraits/eva.webp`, 256×256, собрана штатным `tools/portraits.py`. Существующая привязка имени в `client/src/ui/dialog.ts` использует новый файл автоматически.

Старое изображение и прежний WebP сохранены в `reserved/` для другого персонажа-инженера. Эта запись заменяет описание внешности Евы в исторических `STATUS.md` и `prompts.md` от 26 сентября.

## Фактический промпт

Use case: style-transfer. Asset: square photorealistic sci-fi game dialogue portrait, Eva Moren. Input image is a reference for composition, lighting, clothing and rendering style; create a DIFFERENT young adult woman, age 22–25, naturally beautiful and sweet-looking, soft delicate facial features, bright expressive hazel eyes, subtle warm friendly smile, natural skin texture and faint freckles, chestnut hair loosely tied back with a few face-framing loose strands. Head and upper torso, face prominent and readable at 72px. Preserve the reference's dark navy plain backdrop, realistic cinematic cool light with subtle warm rim light, blue mechanic work jacket, dark undershirt, practical straps and industrial headset around neck. Clothes slightly dirty and worn: subtle grease stains, faded fabric and frayed seams; functional and modest. Face mostly clean with only a tiny incidental grease smudge. Youthful approachable competent engineer, no glamour makeup, no airbrushed plastic skin, no exaggerated grin, no text, no watermark, one person only. Square PNG image.
