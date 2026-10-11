# Задание на генерацию графики гравигонок

Дата: 11 октября 2026 года. Назначение: спрайты одиночных браузерных гонок «Красный каньон» в SRO. Этот документ — задание и готовые промпты для следующего этапа генерации; текущие медный и синий болиды остаются рабочими до приёмки нового набора.

## Визуальный ориентир

Гонки на подрейсерах из «Звёздных войн»: два огромных двигателя впереди, небольшая открытая кабина пилота позади, длинные механические тяги и кабели, энергетическая перемычка между двигателями. Пыльный красный каньон, промышленная техника, металл со следами ремонта. Сделать собственные узнаваемые конструкции в этой эстетике. Общий вид строго сверху, ортографический; никакой изометрии или перспективы. Болиды летят вправо: двигатели справа, кабина слева.

Не рисовать интерфейс, надписи, логотипы, гонщиков соперников или элементы мультиплеера. Детали должны читаться на смартфоне при размере болида около 24–40 пикселей. Контрастный цельный силуэт важнее мелкого гравирования.

## Поставка и технические требования

Сохранять исходные PNG в `art/race/sources/`, принятые игровые файлы — в `client/public/race/`. Имена ниже стабильны; существующие файлы не перезаписывать до приёмки. Один запрос генерации — один отдельный ассет. Для вырезанных объектов нужен настоящий прозрачный фон и сохранённый альфа-канал, без нарисованной шахматной подложки. Бесшовные поверхности непрозрачны.

Объекты освещены нейтральным мягким светом. Пламя, искры, пыль, дым и энергоперемычка поставляются отдельно и анимируются в игре. Не запекать длинную тень: болид поворачивается, а свет и тень иначе повернутся вместе с ним. Прозрачные края без белого ореола; объекты целиком в кадре, одинаковый масштаб болидов и центр кабины/рамы. Логический размер болида остаётся около 3,65 мировых единиц, физический радиус 1,8: генерация не меняет физику.

| Группа | Файлы | Исходный размер | Требования |
| --- | --- | --- | --- |
| Болиды, 4 отдельных варианта | `pod-copper-v1.png`, `pod-blue-v1.png`, `pod-yellow-v1.png`, `pod-white-v1.png` | 1536×1024 | Прозрачный фон; одинаковая ориентация вправо и габариты; разные конструкции, а не только перекраска |
| Выхлоп и энергетическая связь | `pod-exhaust-v1.png`, `pod-energy-link-v1.png` | 512×512 | Прозрачный фон; самостоятельные эффекты, удобные для растяжения и изменения яркости |
| Поверхности | `canyon-sand-tile-v1.png`, `canyon-road-tile-v1.png`, `canyon-rock-tile-v1.png` | 1024×1024 | Полностью бесшовные по обоим направлениям; строго сверху; без крупных одиночных объектов |
| Скальные края | `canyon-bank-a-v1.png`, `canyon-bank-b-v1.png`, `canyon-spur-v1.png` | 1024×1024 | Прозрачный фон; разная форма уступов; достаточный запас камня снаружи края; без нарисованного дорожного полотна |
| Камни | `canyon-rock-small-v1.png`, `canyon-rock-large-v1.png`, `canyon-rock-cluster-v1.png` | 512×512 | Прозрачный фон; компактная читаемая форма; различимые размеры и силуэты |
| Промышленное окружение | `canyon-column-v1.png`, `canyon-pipe-v1.png`, `canyon-scrap-v1.png`, `canyon-beacon-v1.png` | 512×512 | Прозрачный фон; колонна сверху, обломки, трубы, небольшой светящийся маяк |
| Трассовые декали | `race-arrow-v1.png`, `race-start-stripe-v1.png`, `race-checkpoint-post-v1.png` | 512×512 | Прозрачный фон; стрелка вправо; стартовая полоса собирается поперёк трассы; стойки ворот самостоятельны |

Итого: 22 отдельных ассета. Рабочие изображения после генерации можно уменьшить до 256–512 px для объектов и до 512 px для поверхностей. Использовать WebP с альфой/PNG, атласы не крупнее 2048×2048 и мипмапы. Не загружать все исходники высокого разрешения в браузер. Целевой бюджет нового набора в загрузке — до 8 МБ; точный размер подтверждается после экспорта.

## Готовые промпты

### Базовый промпт болида

> Production sprite for a top-down browser sci-fi canyon racing game. A detailed, believable podracing vehicle inspired by the mechanical racing aesthetic of Star Wars: two enormous separated turbine engines at the front RIGHT, a small open pilot cockpit behind them on the LEFT, long exposed structural struts and cables connecting the cockpit to the engines. Original industrial design, readable compact silhouette, weathered titanium, patched graphite chassis, desert dust, visible mechanical vents. Strict orthographic overhead view, no perspective or isometric tilt. Whole vehicle centered, nose points right, vehicle approximately 1.7 times as long as wide, same composition and scale across variants. Genuine transparent background. No ground, no lettering, logos, background, baked flame, energy beam, smoke or long cast shadow. Premium realistic game render with clear large shapes that remain readable at small on-screen sizes.

К базовому промпту добавить ровно один вариант:

- **Copper:** `Copper-orange armor, two cylindrical oversized engines, compact rear cockpit, repaired exposed copper cables; rugged workshop-built racer.`
- **Blue:** `Cobalt blue and silver armor, two tapered turbine engines, reinforced aerodynamic connecting struts, low rear cockpit; fast precision racer.`
- **Yellow:** `Sand-yellow and dark graphite armor, two broad exposed turbine engines with protective rings, skeletal connecting frame, open rear cockpit; powerful desert racer.`
- **White:** `Off-white weathered titanium with muted red accents, two long narrow high-performance engines, lightweight rear cockpit, thin tension cables; light experimental racer.`

### Бесшовные поверхности

> Seamless repeating square texture for a strict overhead sci-fi desert canyon racing game, realistic game art, consistent neutral lighting. Texture must join perfectly on all four edges. No perspective, horizon, directional cast shadows, characters, vehicles, markings, text, isolated large stones, or frame. Surface: [SURFACE]. Fine restrained detail readable on mobile, no distracting high-contrast noise.

`SURFACE`: красно-коричневый песок с мелкой пылью; отдельно уплотнённая тёмная песчаная поверхность гоночной трассы со слабыми следами; отдельно слоистый красный песчаник для скальных массивов. Каждый вариант генерировать отдельным запросом.

### Скальные края и выступ

> One isolated terrain sprite for a top-down red sandstone canyon racing game. Strict orthographic overhead view, detailed layered weathered rock, warm red-brown and ochre strata, broken irregular silhouette. [SHAPE]. Genuine transparent background, entire rock formation visible, neutral soft overhead lighting, no perspective, road, cars, text, painted long shadow or surrounding ground. Clean silhouette suitable for placing along an irregular track boundary and rotating in a game.

`SHAPE`: отдельно вытянутый неровный скальный уступ; отдельно более широкий уступ с углублением; отдельно короткий острый скальный выступ. Край проезда строится по существующим стенам коллизии: спрайт не должен обещать проезд через физическую стену или закрывать свободное полотно.

### Камни и промышленное окружение

> One isolated [OBJECT] for a top-down sci-fi red canyon racing game, strict orthographic overhead view, realistic detailed game asset, clear readable silhouette, weathered dusty materials, neutral overhead light. Genuine transparent background, entire object centered, no perspective, surrounding ground, cars, text, labels, watermark or long cast shadow. Match the same red sandstone and worn titanium industrial palette as the racing vehicles and canyon.

`OBJECT`: небольшой песчаниковый камень; крупный песчаниковый валун; группа трёх камней; короткая промышленная колонна; изогнутая металлическая труба; куча механических обломков; маленький бирюзовый гоночный маяк. Генерировать по одному объекту на изображение.

### Декали и эффекты

> A single [ASSET] for a strict top-down sci-fi desert racing game. Clean readable game sprite on genuinely transparent background, centered and fully visible. No perspective, background, vehicle, text, logos or interface. [DETAIL].

Отдельные запросы: небольшая дорожная стрелка вправо из потёртой охристой краски; сегмент шахматной стартовой полосы; компактная промышленная стойка контрольных ворот с бирюзовым светом; бирюзовый факел двигателя с ярким ядром и мягким прозрачным краем, направленный влево; короткая бирюзовая дуга энергетической перемычки между двумя двигателями без самих двигателей.

## Приёмка и подключение

Проверить альфа-канал на светлом и тёмном фоне, ориентацию вправо, полное попадание объекта в кадр, совпадение масштаба четырёх болидов. Бесшовность поверхностей проверяется мозаикой 3×3. Силуэты болидов проверить в игре при 24, 32 и 40 px, камни и края — на нескольких реальных поворотах и сужениях.

Спрайты меняют отображение, а не стены, контрольные точки или физику. Текстуры поверхности накладывать внутрь действительного дорожного полигона; скалы ставить снаружи стен, объекты препятствий привязывать к их центрам и радиусам. После подключения проверить портретный и альбомный экран смартфона, ПК, отсутствие скачков загрузки во время гонки и фактический FPS на целевых устройствах.

## Результат генерации — 11 октября 2026

Сгенерирован полный набор из 22 отдельных ассетов встроенным `image_gen`. Подготовлены исходные PNG и 22 экспортированных WebP до 512 px. Суммарный размер экспортов — около **1,25 МиБ**, в пределах бюджета 8 МБ. Статус: набор подготовлен к приёмке; замена игровых файлов и подключение в `RaceScene` ещё не выполнялись.

- [Галерея приёмки](../art/race/review.html): светлый и тёмный фон, болиды в 24/32/40 px, мозаики поверхностей 3×3. Открывать в браузере.
- [Исходники](../art/race/sources/) и [экспорты-кандидаты](../art/race/candidates/).
- [Манифест генерации](../art/race/generation.json): точные промпты и выбранный исходник каждого ассета.
- [Технический отчёт](../art/race/validation.json): размеры, прозрачность, границы видимого содержимого, размер загрузки и диагностические показатели стыков текстур.
- [Подготовка и проверка](../tools/prepare-race-art.py): запуск `python tools/prepare-race-art.py`, требуется Pillow. Исходники и текущие игровые ассеты скрипт не изменяет.

После визуального просмотра исправлены шесть вариантов: три скальных края и стойка ворот получили вертикальный ракурс; маленький камень и дорожная стрелка перегенерированы для удаления ошибочно добавленного транспорта. Выбранные исправления находятся в `sources/revisions/`; поле `source_file` манифеста указывает на них. Первые варианты сохранены для истории и не используются в экспортах.

Проверено: наличие всех 22 ассетов, декодирование WebP, прозрачность объектов, непрозрачность поверхностей, отсутствие обрезки видимого содержимого по порогу альфы 16, бюджет загрузки. Галерея просмотрена в Edge; поверхности проверены мозаикой 3×3, болиды — на светлом и тёмном фоне и в малом масштабе. Численные показатели стыков сами по себе не являются доказательством бесшовности.

Отличие от запрошенных исходных размеров: квадратные изображения генератор выдал в 1254×1254; оригиналы сохранены без изменения. Болиды имеют 1536×1024. WebP поверхностей и квадратных объектов — 512×512, болидов — 512×341.

По запросу пользователя 11 октября 2026 года все 22 экспорта подключены в `client/public/race/`. Четыре болида доступны для выбора, пилоты добавлены отдельным слоем в открытые кабины. Выхлоп и энергетическая связь анимируются; поверхности, декали, скалы и окружение размещены на трассе. Маска суши обрезает декоративные скалы по границам проезда. Физика и радиус болида сохранены. Дополнительно реализованы дым повреждений, искры в точке столкновения и инерционный взрыв с отделяющимися двигателями и деталями корпуса, без игровых коллизий.

Проверены загрузка всех ассетов, четыре варианта болида, портретный и альбомный экраны в Edge, дым и искры, разлёт двигателей, однократный запуск и затухание взрыва. Скриншоты — `.artifacts/race/fx/`; скрипт проверки — `tools/race-fx-browser-check.mjs`. FPS на физическом смартфоне остаётся непроверенным.
