# Задание на арт: что ещё нарисовать

Здесь только **несделанное**. Выполненные пачки (A, A2, B, C, D, E, F, G, H, I, J, K, M, S) из списка убраны
2026-09-26: их файлы лежат в своих папках, фактические промпты — в `prompts.md` рядом с ними, история
подключения — в [`next-art-status.md`](next-art-status.md), прежний текст заявок — в git.

Как пользоваться: для каждой картинки склеить **общий стиль** своей группы + **описание предмета**, одна
генерация — одна картинка. Имя файла = заголовок пункта. Готовые склеенные промпты на английском — в
[`next-art-manifest.json`](next-art-manifest.json): это очередь генерации, в ней ровно пункты этого файла.
Рядом с исходниками класть `prompts.md` с фактически отправленными промптами. В игру нарежу и подключу сам.

Приоритет — по тому, что сейчас видно в игре заглушкой:

1. **L** — лица докам: четыре пары поселений показывают одних и тех же людей.
2. **O** — лица «Тихой войны»: пять говорящих с двумя буквами вместо лица.
3. **N** — корабли кампании: повстанцы и корпорация летают чужими корпусами.
4. **Q** — иконки сюжетных предметов: шесть предметов с картинками обычных товаров.
5. **R** — значки заданий в стиле художника вместо нарисованных кодом и значок кампании.
6. **Мехи** — по своему плану, `art/mechs/production/PLAN.md` (см. раздел ниже).

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

## Пачка L — свои лица общим докам

Стиль: STYLE-SCENE + описание исходной сцены + человек + CAST. Исходники PNG — в `art/dock/twins/`.

Четыре пары поселений делят одни и те же сцены офиса и лавки, и в двух доках встречает один и тот же человек
(имена у них уже разные — их раздаёт код, лицо одно). Кадры ниже — **те же сцены**, что уже есть, с
**другим человеком того же ремесла**: комната, камера, свет и вид из окна исходника сохраняются, меняются
лицо, возраст и одежда. Исходник — `art/dock/planet-settlements/<набор>-<сцена>.png`.

| Набор | Делят | Старая картинка остаётся у | Новые кадры |
| --- | --- | --- | --- |
| `barren` | Купол Альфы, Рудник Кастора | Купола Альфы | `barren-mine-*` для Рудника Кастора |
| `ice` | Ледяная Вега, Станция Мороз | Ледяной Веги | `ice-frost-*` для Станции Мороз |
| `lava` | Плавильня Пекла, Пепельный Приют | Плавильни Пекла | `lava-ash-*` для Пепельного Приюта |
| `desert` | Пыльная Гавань, Рудник Прайм | Пыльной Гавани | `desert-prime-*` для Рудника Прайм |

- **barren-mine-office** (с `barren-office`) — диспетчер Рудника Кастора: женщина ~45 из Средней Азии,
  каска с фонарём, пыльная рабочая роба; за окном тот же лунный пейзаж, но террикон и конвейер рудника.
- **barren-mine-trader** (с `barren-trader`) — торговец Рудника: темнокожий мужчина ~35, крепкий,
  комбинезон, рукавицы за поясом, на прилавке образцы руды. Все люди — **внутри** герметичного
  помещения: в исходнике за окном стоят люди без скафандров, это повторять нельзя.
- **ice-frost-office** (с `ice-office`) — диспетчер Станции Мороз: седобородый скандинав ~55, толстый
  свитер под форменной курткой; на стене графики ледовой верфи без текста.
- **ice-frost-trader** (с `ice-trader`) — торговка Мороза: славянка ~35, ушанка, очки-гогглы сдвинуты на
  лоб, меховые рукавицы на прилавке.
- **lava-ash-office** (с `lava-office`) — диспетчер Пепельного Приюта: женщина ~40 ближневосточной
  внешности, платок на плечах, усталое доброе лицо; приют беднее Плавильни — латаные панели.
- **lava-ash-trader** (с `lava-trader`) — торговец Приюта: латиноамериканец ~55, седые усы, прожжённый
  кожаный фартук.
- **desert-prime-office** (с `desert-office`) — диспетчер Рудника Прайм: славянин ~60, седые усы, потёртая
  куртка горняка с нашивкой без текста, респиратор на шее; за окном те же дюны, но копёр шахты и рудовозы.
  Не путать с Евой Морен — она инженер этого же рудника, но в доке не стоит.
- **desert-prime-trader** (с `desert-trader`) — торговка Рудника Прайм: восточноазиатка ~40, короткие
  волосы под банданой, защитные очки на лбу; на прилавке ящики с рудой и запчасти буров.

Итого 8 кадров. Подключение (сделаю сам): `tools/dock.py` → `client/public/dock/<набор>-<сцена>.webp` →
набор в `SCENE_SETS` (`client/src/ui/dockScreen.ts`) — верфь и ангар двойник берёт у исходного набора,
поэтому там нужен запасной набор, а не общий `planet-*` → внешность в `APPEARANCE`
(`client/src/sim/staff.ts`) → `scene` поселению в `shared/galaxy.json`. Имена код выведет сам.

---

## Пачка O — лица второй половины «Тихой войны»

Стиль: STYLE-PORTRAIT. Исходники — в `art/story/quiet-war/sources/` рядом с портретами пачки M.
В диалоге этих людей сейчас представляют две буквы в рамке.

- **portrait-gor** — Литейщик Гор, хозяин Плавильни Пекла: грузный мужчина ~55, копоть на лице, ожоги на
  предплечьях, кожаный фартук литейщика, сварочные очки на лбу, взгляд торговца — продаёт то, что делает,
  и знает цену.
- **portrait-reed** — Техник Рид из ремонтной бригады военной станции Нова: женщина ~30, серый рабочий
  комбинезон в масле, много карманов, сбруя с инструментом на плече, волосы убраны; тихий настороженный
  взгляд человека, который рискует ради других.
- **portrait-costa** — Интендант Коста, снабжение поста рейнджеров на Барнарде: мужчина ~50 (в репликах
  он говорит о себе в мужском роде), форма рейнджеров — тёмно-синее с белым и голубыми шевронами, очки на
  воротнике, осторожный скептичный вид честного снабженца. Это **не** азиатка-интендант из сцены торговца
  поста рейнджеров — другой человек.
- **portrait-platform-dispatcher** — Диспетчер Платформы Нова-Один: **та же** латиноамериканка, что в
  сцене `art/dock/planet-settlements/orbital-platform-office.png` (лицо, причёска и форма те же), крупнее
  и вымотанная после осады, гарнитура на голове. В игре это один человек: диспетчер дока Платформы.
- **portrait-fang-lead** — командир звена рейнджеров «Клык»: пилот ~40, лётный шлем с поднятым визором,
  лётный костюм рейнджеров, строгое лицо солдата, выполняющего приказ.

Итого 5 портретов. Подключение: `tools/portraits.py` (строка в `PORTRAITS`) и имя говорящего в `PORTRAITS`
`client/src/ui/dialog.ts` — «Литейщик Гор», «Техник Рид», «Интендант Коста», «Диспетчер Платформы»,
«Звено «Клык»».

---

## Пачка N — корабли «Тихой войны»

Стиль: STYLE-SHIP. Исходники — в `art/space/story-ships/`. Сейчас повстанец летает «Тягачом» с ржавой
меткой, а корпорация «Нова-Рудник» — чужими корпусами «Страж», «Игла» и «Галеон»: их отличает только
метка и имя.

- **ships-rebel** — канонерка бастующих шахтёров: переделанный короткий горный буксир, наваренные листы
  брони разного цвета, самодельная импульсная пушка на носу, остаток бурового оголовка под носом.
  Ржаво-красный и грязно-жёлтый горный окрас, полоса от руки. Бедный и самодельный, не военный.
- **ships-corp-guard** — фрегат охраны корпорации: гладкий угловатый корпус, две спинные башни, чистая
  холодная стально-серая краска с белой и ледяно-голубой отделкой и крупной корпоративной полосой (без
  букв). Дорогой, ухоженный — холоднее и чище любого рейнджера и пирата.
- **ships-corp-courier** — курьер корпорации: тонкая быстрая игла с маленьким бронированным контейнером,
  тот же холодный окрас. Силуэт отличается от бело-рыжей гражданской «Иглы».
- **ships-corp-transport** — бронированный рудовоз корпорации: длинный коробчатый корпус, четыре
  опломбированных контейнерных стеллажа, бронированный мостик в корме, блистеры ПРО, тот же окрас.

Итого 4 корпуса. Подключение: `tools/sprites.py` (`SINGLES`, 256 px) → вид `rebel` и `corp` из
`shared/npcs.json` получают свой спрайт вместо корпуса (`shipSprite`/`ROLE_SPRITES` в
`client/src/render/sprites.ts`).

---

## Пачка Q — иконки сюжетных предметов

Стиль: STYLE-ICON + «This is a story mission item, not a weapon or a trade crate.» Исходники — в
`art/space/story-items/`. Сейчас эти предметы нарисованы картинками обычных товаров (`ITEM_SPRITES` в
`client/src/render/sprites.ts`). Каркас, приводной блок и оружейный модуль уже показаны деталями меха —
их рисовать не нужно.

- **item-power-core** — `powerCore`, «Силовой привод»: бронированный цилиндр с рёбрами охлаждения и
  светящимся голубым окном ядра, тяжёлые разъёмы. Сейчас — энергия.
- **item-armor-sections** — `armorSections`, «Броневые секции»: стопка из трёх толстых изогнутых листов
  корабельной брони под стяжкой, отверстия под болты, опалённые кромки. Сейчас — металл.
- **item-ship-log** — `shipLog`, «Бортовой журнал»: бортовой самописец — прочный оранжево-чёрный ящик
  с ручкой, мятый угол, один мигающий янтарный огонёк. Сейчас — электроника.
- **item-blueprint** — `blueprint`, «Неполный чертёж»: потёртый планшет с голубой каркасной голограммой
  меха, часть голограммы отсутствует. Сейчас — электроника.
- **item-reactor** — `reactor`, «Реактор»: приземистая бронированная сфера в раме для переноски,
  знаки радиации (символы, без букв), тёплое янтарное свечение из вентиляции. Сейчас — плазма.
- **item-neuro-link** — `neuroLink`, «Нейроинтерфейс»: модуль размером с кулак, гладкий тёмный корпус,
  пучок тонких контактов и мягкая голубая светополоса. Сейчас — кристаллы.

Итого 6 иконок. Подключение: `tools/sprites.py` (`resources-<имя>`, 128 px) и строка в `ITEM_SPRITES`.

---

## Пачка R — значки заданий в стиле пачки C и значок кампании

Стиль: STYLE-UI-ICON. Исходники — в `art/space/mission-reputation/`, **поверх** одноимённых файлов:
четыре значка там сейчас нарисованы кодом (`tools/mission_icons.py`) и проще пяти значков художника
(`mission-escort`, `-patrol`, `-courier`, `-meteor`, `-ground`). Смысл тот же — нужен тот же почерк.

- **mission-kill** — простой череп (убить пиратов).
- **mission-collect** — кирка поверх гранёного самородка руды (добыть в космосе).
- **mission-deliver** — ящик груза и крупная стрелка вправо (отвезти груз).
- **mission-defend** — щит с маленькой планетой в центре (отстоять поселение).
- **campaign-quiet-war** — значок кампании «Тихая война»: шахтёрская каска, расколотая молниевидной
  трещиной. Сейчас сюжетная строка на доске отличается только золотой кромкой.

Итого 5 значков. Подключение: после генерации `tools/mission_icons.py` не запускать (он перезапишет файлы),
сразу `python tools/sprites.py`. Значок кампании — новая строка в `SINGLES` и в сюжетной строке доски.

---

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
