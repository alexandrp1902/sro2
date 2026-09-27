# Задание на арт: что ещё нарисовать

> Поставка 2026-09-26: все 28 изображений очереди L/O/N/Q/R созданы. [Галерея](review-2026-09-26.html), [отчёт с проверками и особенностями исходников](generation-2026-09-26-report.md). Ниже сохранена спецификация; игровые ресурсы ещё не пересобирались. План мехов остаётся отдельной работой.

Здесь только **несделанное**. Выполненные пачки (A, A2, B, C, D, E, F, G, H, I, J, K, L, M, N, O, Q, R, S)
из списка убраны: их файлы лежат в своих папках, фактические промпты — в `prompts.md` рядом с ними, история
подключения — в [`next-art-status.md`](next-art-status.md), прежний текст заявок — в git.

Как пользоваться: для каждой картинки склеить **общий стиль** своей группы + **описание предмета**, одна
генерация — одна картинка. Имя файла = заголовок пункта. Готовые склеенные промпты на английском — в
[`next-art-manifest.json`](next-art-manifest.json): это очередь генерации, в ней ровно пункты этого файла.
Рядом с исходниками класть `prompts.md` с фактически отправленными промптами. В игру нарежу и подключу сам.

В очереди **пачка T** (пролог «Дорога в Нову», ниже) и **мехи** — по своему плану, `art/mechs/production/PLAN.md`.
Пачки T в [`next-art-manifest.json`](next-art-manifest.json) ещё нет: склеить промпты по общим стилям при запуске.

---

## Общие стили (копировать в начало промпта)

### STYLE-SHIP — корабль, вид строго сверху

> Use case: stylized-concept. Generate ONE isolated spaceship hull sprite for a top-down 2D space RPG, 1024x1024 canvas with genuinely TRANSPARENT alpha background, not a checkerboard drawing. STRICT ORTHOGRAPHIC 90-degree overhead dorsal view, nose toward exact TOP of image, engines toward bottom, no perspective or isometric tilt. Entire craft centered, all protrusions inside canvas with 8% clear padding. Detailed semi-realistic game art: painted metal hull with worn edges and chipped paint, gunmetal mechanical parts, subtle cyan windows and tiny amber indicators; crisp readable silhouette, coherent soft upper-left illumination, no cast shadow outside hull. No engine flames, exhaust, smoke, bloom outside silhouette, starfield, floor, text, letters, numbers, logo, frame or watermark. A finished functional industrial spacecraft, not a sketch.

Пламя не рисуем: его делает код (`client/src/render/flame.ts`).

### STYLE-ICON — иконка предмета, 3/4

> Use case: stylized-concept. ONE isolated spaceship equipment inventory icon for a semi-realistic space RPG. 1024x1024 square, genuinely transparent alpha background, no painted checkerboard. Detailed industrial game asset, worn pale steel armor panels, blue-gray gunmetal structure, dark recesses, exposed bolts and hoses, restrained luminous accent. Elevated three-quarter product view showing top, front and side, crisp chunky readable silhouette at small sizes, soft studio illumination from upper left, no cast shadow outside object. Center entire item with clear padding, no cropped parts. No text, labels, letters, numbers, watermark, UI border, people, ship, room, floor or extra separate objects.

### STYLE-UI-ICON — плоский значок интерфейса, белый на прозрачном

> Use case: stylized-concept. ONE flat monochrome UI icon, 256x256 PNG, pure WHITE bold silhouette on genuinely transparent background. {предмет}. Clean vector-like graphic, simple thick shapes and generous clear negative spaces, centered with 12% padding, readable at 24px. No color, gradients, shading, texture, shadows, border, text or labels.

Цвет накладывает игра маской, поэтому только белое.

### STYLE-SCENE — фон экрана дока 1600×1200

> Use case: stylized-concept. Single production space RPG background, 1600x1200 landscape 4:3. Detailed semi-realistic game illustration, worn gunmetal and blue-gray steel, dark navy shadows, restrained cyan practical lighting and amber details, clear midtones, matching industrial spaceship sprites. No text, logos, watermark, borders, UI. Key content inside central 60%, background fills frame. Bottom quarter quiet and dark for dialogue overlay.

### STYLE-PORTRAIT — портрет для сюжетного диалога

> Use case: stylized-concept. Asset type: SRO space RPG dialogue portrait, one square image, composed to remain clear at 256x256. Subject: {кто}. Style: detailed semi-realistic game painting, industrial 'glass and steel' aesthetic, worn gunmetal and blue-gray fabrics, dark navy shadows, restrained cyan and amber details. Plain even dark navy background, no interior. Shoulder-up station ID-card framing, large face centered, full head and hair inside frame. Soft side illumination, natural facial proportions and skin texture, working tired person, no heroic pose, no glamour. Opaque background. No text, letters, logos, watermark, UI or border. Generate only this single portrait.

Тот же стиль, что у портретов Евы, Холта, Дана и контакта (`art/story/quiet-war/sources/`): новые должны
стоять с ними в одном ряду.

### CAST — люди в сценах (копировать в конец промпта каждой сцены с человеком)

> The person is an ordinary working adult, not a fashion model and not a hero portrait: natural face proportions, visible skin texture and pores, small asymmetries, plain hair that looks lived in, no glamour makeup, no idealised beauty, no exaggerated figure, practical utilitarian work clothing that covers the body, calm plausible working expression. EXACTLY ONE person in the whole frame, no bystanders, no reflections of other people.

Композиция сцен с человеком: голова x50 % y34 %, торс y48 %, вся голова ниже y22 %, человек занимает около
35 % ширины, нижняя четверть кадра тихая и тёмная под текст диалога.

---

## Пачка T — пролог «Дорога в Нову»

Пролог сюжета (плейтест 2026-09-26): пять миссий от Терры до Новы, рейс семейного транспорта «Надежда».
Сейчас предметы стоят чужими иконками (`ITEM_SPRITES` в `client/src/render/sprites.ts`), а у говорящих —
монограммы (`PORTRAITS` в `client/src/ui/dialog.ts`, поиск по имени).

Иконки — STYLE-ICON, 1024×1024, в `art/story/quiet-war/items/`:

- **contracts** — «Контракты найма»: пачка печатных бланков корпорации «Нова-Рудник» в жёсткой папке
  с зажимом и пломбой, уголки загнуты. Сейчас — лист чертежа.
- **mailCapsule** — «Почтовая капсула»: цилиндрическая капсула почтового дрона, красная маркировка,
  маячок на торце, слегка обгоревшая после падения. Сейчас — контейнер.
- **passengerList** — «Список пассажиров»: планшет-накладная с защитным чехлом, на экране таблица без
  читаемого текста, приклеенная бумажная копия. Сейчас — бортовой журнал.

Портреты — STYLE-PORTRAIT, 256×256, в `art/story/quiet-war/sources/` (рядом с Евой и Холтом):

- **ilyina** — Вербовщица Ильина, отдел найма «Нова-Рудник»: женщина за 40, аккуратная форменная куртка
  корпорации, профессиональная улыбка, которой не веришь.
- **brandt** — Староста Брандт, Купол Альфы: пожилой мужчина, обветренное лицо, рабочий комбинезон колонии.
- **hanna-lane** — Ханна Лейн, жена шахтёра: женщина около 35, усталая, простая тёплая одежда купола.
- **vega-dispatcher** — Диспетчер Веги: мужчина средних лет в гарнитуре, форма станции, лицо «третья смена».
- **owen** — Смотритель Оуэн, Пыльная Гавань: мужчина за 50, пыль на воротнике, защитные очки на лбу.
- **larsen** — Капитан Ларсен, транспорт «Надежда»: мужчина около 60, седой, капитанская куртка
  гражданского флота, спокойный и встревоженный.

## Мехи — по своему плану

Производство мехов идёт отдельно: [`art/mechs/production/PLAN.md`](mechs/production/PLAN.md) (этапы
P2–P6, контракт камеры и восьми направлений) и [`environment/TASK.md`](mechs/production/environment/TASK.md)
(здания и стены). Старая пачка E — иконки, юниты вида сверху, тайлы — нарисована целиком, но для боя M21
не годится: он собран из восьминаправленного рига P1. Что бой ждёт в первую очередь:

1. **Корпуса противника** `bandit`, `militia`, `enemy-heavy` — направленные листы 4×2 под риг (P2). Сейчас
   враг — перекрашенный мех игрока.
2. **Рука с дробовиком** `shotgun`, правая и левая — P2. Сейчас дробовик рисуется автопушкой.
3. **Повреждённое плечо** — 2 стороны × 8 направлений (P3). Сейчас сломанная рука просто исчезает.
4. **Цикл шага шасси** — P3. Сейчас мех переезжает по клеткам не шагая.
5. **Поле пустыни** — грунт, стены, здания (P4): первые 10 из 135 кадров нарисованы и ждут проверки
   стыков, остальное поле — старые тайлы пачки E.

Эффекты боя (попадание, взрыв, блок щитом) пачки E уже подключены. Исходники `art/mechs/production/`
пока не в git — без них `tools/mechs.py` не пересоберёт арт боя.

---

## Не арт — ждёт кода, рисовать не нужно

- **Поселения на `ocean`, `toxic`, `ringed`** — сцены и посадка `landing-ringed` нарисованы (бывшая пачка H),
  не хватает самих поселений в `shared/galaxy.json`.
- **Минный постановщик** — `weapons-mine-layer` и `space-mine` нарисованы, мины в игре нет.
- **Портрет контакта** нарезан, но у контакта нет реплик в `shared/story.json`.
- **Корабль на площадке верфи и ангара** уменьшен до 105 px, потому что спрайт корпуса 256 px пикселил.
  Исходники корпусов крупнее — около 600 px у четырёх первых (лист 2×2) и 1254 px у остальных, так что
  рисовать ничего не надо: нарезать для дока крупнее и вернуть размер в `.dock-scene-ship`
  (`client/src/style.css`).
- **Фоны регионов** `bg-core`, `bg-frontier`, `bg-rim` — отклонены 2026-09-20, не подключаются.
