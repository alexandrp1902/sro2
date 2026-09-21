# Задание на арт: следующие пачки (M11–M20)

План этапов: [docs/SRO - План развития M11–M20.md](../docs/SRO%20-%20План%20развития%20M11–M20.md).

Как пользоваться: для каждой картинки склеить **общий стиль** своей группы + **описание предмета**, одна генерация — одна картинка. Имя файла = заголовок пункта. Исходники PNG складывать в `art/space/<пачка>/` (или `art/dock/`, `art/mechs/`), рядом `prompts.md` с фактическими промптами. В игру их нарежу и подключу сам (webp, до 256 px для иконок и спрайтов).

Приоритет: пачки A и B нужны к M11, C — к M12–M14, D — к M15, E — к M16–M17. Пачки можно генерировать заранее.

---

## Общие стили (копировать в начало промпта)

### STYLE-SHIP — корабль, вид строго сверху

> Use case: stylized-concept. Generate ONE isolated spaceship hull sprite for a top-down 2D space RPG, 1024x1024 canvas with genuinely TRANSPARENT alpha background, not a checkerboard drawing. STRICT ORTHOGRAPHIC 90-degree overhead dorsal view, nose toward exact TOP of image, engines toward bottom, no perspective or isometric tilt. Entire craft centered, all protrusions inside canvas with 8% clear padding. Detailed semi-realistic game art: worn blue-gray armor, gunmetal mechanical parts, scratched pale steel panels, subtle cyan windows and tiny amber indicators; crisp readable silhouette, coherent soft upper-left illumination, no cast shadow outside hull. No engine flames, exhaust, smoke, bloom outside silhouette, starfield, floor, text, letters, numbers, logo, frame or watermark. A finished functional industrial spacecraft, not a sketch.

### STYLE-ICON — иконка снаряжения или товара, 3/4

> Use case: stylized-concept. ONE isolated spaceship equipment inventory icon for a semi-realistic space RPG. 1024x1024 square, genuinely transparent alpha background, no painted checkerboard. Detailed industrial game asset, worn pale steel armor panels, blue-gray gunmetal structure, dark recesses, exposed bolts and hoses, restrained luminous accent. Elevated three-quarter product view showing top, front and side, crisp chunky readable silhouette at small sizes, soft studio illumination from upper left, no cast shadow outside object. Center entire item with clear padding, no cropped parts. No text, labels, letters, numbers, watermark, UI border, people, ship, room, floor or extra separate objects.

### STYLE-OBJECT — объект в космосе сверху (станция, планета, звезда)

> Use case: stylized-concept. ONE isolated space object sprite for a top-down 2D space RPG, 1024x1024 canvas with genuinely TRANSPARENT alpha background, not a checkerboard drawing. STRICT ORTHOGRAPHIC overhead view, object centered with 6% clear padding. Detailed semi-realistic game art matching worn blue-gray industrial spaceships, soft upper-left illumination. No starfield, text, letters, numbers, logo, frame or watermark.

### STYLE-SCENE — фон экрана дока 1600×1200

> Use case: stylized-concept. Single production space RPG background, 1600x1200 landscape 4:3. Detailed semi-realistic game illustration, worn gunmetal and blue-gray steel, dark navy shadows, restrained cyan practical lighting and amber details, clear midtones, matching industrial spaceship sprites. No text, logos, watermark, borders, UI. Key content inside central 60%, background fills frame. Bottom quarter quiet and dark for dialogue overlay.

### STYLE-SPLASH — заставка стартового экрана 1920×1080

> Use case: stylized-concept. Single production space RPG title-screen illustration, 1920x1080 landscape 16:9. Detailed semi-realistic game painting matching worn blue-gray industrial spaceship sprites: gunmetal hulls, scratched pale steel panels, dark navy space, restrained cyan practical lights and amber warning details, clear readable midtones, no neon overload. Cinematic wide shot. The CENTER of the frame is deliberately the calmest and darkest part — open space, distant dust and faint glow only, no ships, no explosions and no bright light there — because an interface panel is placed over it; all loud action, light and detail live in the left, right and upper thirds, and the lower third stays dark. The frame is also center-cropped to a tall 9:16 phone screen, so the central 35% of the width must still read as a finished picture, never as empty flat black. No text, letters, numbers, logo, watermark, frame, border, UI, HUD, cursor or signature.

### STYLE-MECH — мех или деталь меха, вид строго сверху (для тактического поля)

> Use case: stylized-concept. ONE isolated combat mech unit sprite for a top-down turn-based tactics game, 1024x1024 canvas with genuinely TRANSPARENT alpha background. STRICT ORTHOGRAPHIC 90-degree overhead view, front of the mech facing exact TOP of image, no perspective or isometric tilt, centered with 10% padding. Detailed semi-realistic industrial art matching worn blue-gray spaceships: pale steel armor plates, gunmetal joints, dark recesses, small cyan sensor lights and amber indicators, soft upper-left illumination, no cast shadow outside silhouette. Crisp readable silhouette at 64 px. No text, numbers, logo, ground, frame or watermark.

---

## Пачка A — корабли NPC (к M11)

Корабли scout, interceptor, frigate, cruiser, industrial, freighter становятся корпусами игроков. NPC нужны свои, чтобы их не путали с игроками. Стиль: STYLE-SHIP.

### ships-ranger
Ranger patrol corvette of a law-enforcement fleet. Compact wedge hull with twin forward gun housings, a raised armored bridge, broad white-and-navy livery with bold light-blue chevron stripes along both wings, a small beacon light pod on the dorsal spine. Disciplined, clean, clearly official — different from any civilian or pirate craft.

### ships-ranger-heavy
Ranger heavy patrol cruiser, leader of a ranger squad. Long armored hull with a broad forward shield plate, four dorsal turret housings, navy-and-white livery with light-blue chevrons like the ranger corvette, twin large engines. Larger and more imposing than the ranger corvette.

### ships-trader-hauler
Small civilian merchant hauler used by NPC traders. Stubby boxy hull with one cargo container clamped under a short spine, rounded cockpit at top, faded yellow-ochre and gray civilian paint with hazard stripes on the container, no weapons. Humble and slow looking, clearly smaller than a big freighter.

### ships-trader-convoy
Large merchant convoy transport for escort missions. Long spine carrying eight stacked colorful cargo containers (muted ochre, rust red, teal), bridge tower at top, four engines at bottom, a couple of small defensive turret bumps. Must read as "valuable cargo to protect".

### ships-pirate-raider
Light pirate raider. Asymmetric patched-together hull built from scavenged parts, one wing longer than the other, mismatched rust-red and black armor plates, crude welded spikes on the nose, a single oversized gun bolted on one side. Aggressive and ragged.

### ships-pirate-brute
Heavy pirate gunship. Bulky rectangular hull made from a stripped cargo hauler, heavy welded armor slabs, rust red and soot black paint, skull-like pattern of vents on the prow (no text), two big mismatched turrets and a missile rack. Menacing and crude.

### ships-pirate-flagship
Pirate flagship, boss of an invasion. Very large dreadnought assembled from several ships welded together, jagged prow ram, many mismatched turrets, black and blood-red plating with scorched patches, glowing orange reactor slits. Clearly the biggest and most dangerous pirate.

### ships-drone
Unmanned training/target drone. Small round-disc hull with four short thruster arms, bright orange-and-white safety livery, a single sensor eye in the center. Obviously robotic and harmless.

---

## Пачка A2 — выстрелы нового оружия (к M11)

Такой же лист, как `art/space/weapon-shots.png` (см. `weapon-shots.prompt.txt`): для каждого вида снаряд + вспышка выстрела + попадание, вид сверху, прозрачный фон, снаряд направлен вверх.

### weapon-shots-rail
Railgun slug: a very thin long white-cyan streak with a tiny bright metallic core, short sharp muzzle flash with radial shock ring, impact as small hard white spark burst with fragments.

### weapon-shots-ion
Ion bolt: a rippling teal-cyan pulse, like a small electric ring wave; muzzle flash is a teal ring; impact is a teal electric crackle spreading over a shield surface.

### weapon-shots-torpedo
Torpedo: a chunky dark-gray elongated torpedo body with red warning band and a bright orange exhaust at the tail; launch flash puff; impact a large orange-white explosion with dark smoke ring.

### weapon-shots-flak
Point-defense tracer: short yellow tracer dashes in a tight burst; small muzzle flash; impact tiny flak puffs of gray smoke with sparks.

---

## Пачка B — галактика и регионы (к M11)

### Планеты (STYLE-OBJECT, круглая планета, вид сверху, лёгкая атмосфера по краю)
- **planets-lava** — volcanic world, black crust with glowing orange lava rivers and cracks.
- **planets-toxic** — toxic world, sickly yellow-green swirling clouds with dark brown land patches.
- **planets-ocean** — ocean world, deep blue sea with scattered small islands and white cloud spirals.
- **planets-barren** — airless gray moon-like planet with many craters, no atmosphere.
- **planets-jungle** — lush jungle world, dark green continents, turquoise shallow seas, thick clouds.
- **planets-ringed** — pale beige gas giant with a wide flat ring system seen from above.

### Станции (STYLE-OBJECT)
- **stations-pirate** — pirate hideout station built into a hollowed asteroid, scrap docking arms, red lights, patched hull.
- **stations-outpost** — small frontier outpost: a single cross-shaped module with two docking arms and solar panels, worn and lonely.
- **stations-trade** — large trade hub: central hexagonal core with six long docking piers full of small container stacks.
- **stations-ranger** — ranger base: star-shaped fortified station, navy-and-white livery with light-blue chevrons, landing bays, sensor dishes.

### Звёзды (STYLE-OBJECT, светящийся шар с короной, без лучей-крестов)
- **suns-white** — small intense white dwarf with a tight bright corona.
- **suns-binary** — two stars close together: a yellow and a small red one with merging coronas.

### Фоны регионов (не прозрачные, 2048×2048, бесшовно тайлятся)
Distant deep-space parallax background, seamless tileable, very dark so ships remain readable, no bright stars larger than 3 px, no planets.
- **bg-core** — calm dark navy space with faint blue nebula wisps.
- **bg-frontier** — dark space with faint violet and amber dust clouds.
- **bg-rim** — dark space with a faint blood-red and teal nebula, sparse cold stars.

---

## Пачка C — товары, задания, репутация (к M12–M14)

### Товары (STYLE-ICON, заменить последнюю фразу на "This is a trade commodity container, not a weapon.")
- **goods-food** — sealed stackable food ration crate with green markings (symbols only, no text) and a small transparent window showing packed rations.
- **goods-medicine** — white medical supply case with a red-free abstract medical cross symbol in cyan, cold vapor from cooling vents.
- **goods-machinery** — heavy crate with an exposed industrial gear assembly and hydraulic parts.
- **goods-luxury** — elegant dark case with gold trim, open lid showing glowing gems and fine fabric.
- **goods-weapons** — military crate of stacked rifle-like weapon cases, olive and black, looks illicit.
- **goods-fuel-cells** — rack of four glowing cyan cylindrical fuel cells in a metal frame.

### Задания
- **item-letter** (STYLE-ICON) — sealed secure courier tube with an amber wax-like seal and a small cyan lock light; precious and official.
- **mission-escort** / **mission-patrol** / **mission-courier** / **mission-meteor** / **mission-ground** — flat monochrome UI mission icons, white on transparent, 256×256, simple bold silhouette: shield over a cargo box / three chevrons in formation / sealed envelope with wings / cracked rock with crosshair / mech helmet. Clean vector style, no gradients.

### Репутация (плоские UI-значки, белые на прозрачном, 256×256)
- **rep-enemy** (скрещённые клинки), **rep-distrust** (перечёркнутый глаз), **rep-neutral** (круг), **rep-friend** (рукопожатие), **rep-hero** (звезда с лавровыми ветвями).

### Корабль на площадке — сейчас времянка (M15.7)

Сцены `shipyard` и `hangar` рисуют корабль поверх фона, а спрайт корпуса — 256 px. Он растягивался до
420 px, и на площадке были видны пиксели. Пока нет фонов, рассчитанных на крупный корабль, спрайт
уменьшен вчетверо (`.dock-scene-ship` в `client/src/style.css`: `width: min(12%, 105px)`).

Что нужно, чтобы вернуть прежний размер: либо спрайты корпусов в 1024 px, либо фоны площадок, у
которых посадочный круг занимает малую часть кадра и мелкий корабль на нём смотрится верно. Правится
одной строкой CSS.

### Дополнительная сцена дока (STYLE-SCENE)
- **station-ranger-office** — Ranger headquarters briefing room on an orbital station. One stern female ranger commander in navy-and-white uniform with light-blue chevrons stands at a tactical holo-table showing an abstract star map; face x50% y34%. Background: large window to space with ranger corvettes docked outside.

---

## Пачка D — планеты и посадка (к M15)

Готовы фоны только для земного поселения (`client/public/dock/planet-*.webp`). Нужны поселения на других видах планет. Стиль: STYLE-SCENE + "This is a PLANET SURFACE settlement, not an orbital station." Для каждого вида четыре сцены, как в `art/dock/planet-prompts.md`: **office**, **trader**, **shipyard** (верфь сверху, пустая площадка), **hangar** (посадочная площадка сверху, пустая).

- **desert-*** — sand-colored domes and wind-worn metal, orange dusty sky, mesa cliffs.
- **ice-*** — pressurized habitats half buried in snow, pale cyan ice cliffs, aurora in the sky.
- **lava-*** — heat-shielded black basalt bunkers, orange glow from distant lava flows, ash in the air.
- **jungle-*** — elevated platforms among giant alien trees, humid mist, bioluminescent plants.
- **barren-*** — sealed dome colony on a gray cratered moon, black sky with the parent planet on the horizon.
- **orbital-platform-*** — for gas giants: platform floating in clouds, gas-giant bands filling the sky.

Плюс для анимации посадки:
- **landing-<kind>** (1600×1200, без прозрачности) — вид из кабины при снижении сквозь атмосферу к поселению: облака, внизу огни посёлка. По одной картинке на вид планеты.

---

## Пачка E — мехи (к M16–M17)

Опора: GDD мехов, §3–23. Нужно два вида картинок: **иконки деталей** для ангара (STYLE-ICON, заменить последнюю фразу на "This is a walking combat mech part, not a spaceship part.") и **юниты на поле** (STYLE-MECH).

### Иконки деталей (STYLE-ICON)
- Корпуса: **mech-body-light** (компактный торс с большим фонарём кабины), **mech-body-medium** (сбалансированный бронированный торс), **mech-body-heavy** (массивный торс-бункер), **mech-body-sniper** (узкий торс с длинным сенсорным модулем).
- Шасси: **mech-legs-light** (две тонкие ноги с обратным коленом), **mech-legs-medium** (две мощные ноги), **mech-legs-heavy** (четыре ноги, как у паука), **mech-legs-tracks** (гусеничная платформа).
- Оружие рук: **mech-arm-mg** (лёгкий пулемёт), **mech-arm-cannon** (тяжёлая пушка), **mech-arm-shotgun**, **mech-arm-rifle** (снайперская винтовка), **mech-arm-rockets** (ракетный блок), **mech-arm-sword** (энергоклинок), **mech-arm-experimental** (мощная экспериментальная пушка, светящиеся катушки).
- Щиты: **mech-shield-light** (круглый), **mech-shield-heavy** (ростовой прямоугольный).
- Рюкзаки: **mech-pack-accuracy** (сенсорная мачта), **mech-pack-armor** (дополнительные бронеплиты), **mech-pack-damage** (силовая установка с конденсаторами), **mech-pack-mobility** (прыжковые двигатели), **mech-pack-repair** (ремонтный дрон на креплении).

### Юниты на поле (STYLE-MECH)
Один спрайт на корпус, поворачивается кодом. Руки и оружие будут отдельными слоями, поэтому корпус без рук.
- **mech-unit-light**, **mech-unit-medium**, **mech-unit-heavy**, **mech-unit-sniper** — корпус + ноги сверху, blue-gray player livery.
- **mech-unit-enemy-bandit**, **mech-unit-enemy-militia**, **mech-unit-enemy-heavy** — rust-red/black scavenged livery, like pirate ships.
- **mech-arm-layer-*** — те же виды оружия рук, что в иконках, но вид сверху, ствол вверх, прозрачный фон, 512×512, чтобы накладывать на корпус.

### Тайлы поля (вид строго сверху, 512×512, бесшовно, без прозрачности, если не сказано иное)
Для каждого биома (desert, ice, jungle, barren, station-interior): **ground** (2 варианта), **rough** (труднопроходимый грунт), **wall** (прозрачный фон, кусок стены или скалы), **building** (крыша здания сверху, прозрачный фон), **crate** (контейнер-укрытие, прозрачный фон), **high-ground** (возвышенность с тенью края).

### Эффекты и сцены
- **mech-fx-hit**, **mech-fx-explosion**, **mech-fx-shield-block** — лист из 6–8 кадров, как `explosions.png`.
- **dock-mech-hangar** (STYLE-SCENE) — ангар мехов на поселении: два пустых ремонтных стенда, краны, инструменты, без мехов и людей в центре (мех рисуется поверх).

---

## Пачка F — защитные модули (к M15.6)

Четыре вспомогательных модуля из `shared/modules.json`. Сейчас идут в доке без иконки вовсе: иконка по
слоту убрана нарочно — она была картинкой грузового контейнера, и её надевал бы каждый новый utility-модуль.

С сеткой слотов на вкладке «Модули» пробел стало видно: в кружке вспомогательного слота вместо картинки
модуля стоит точка. Пока иконок нет, так и задумано — подменять их чужой картинкой не будем.

### Иконки (STYLE-ICON)
- **modules-thrusters** — cluster of four small vectoring manoeuvring nozzles on a short mounting block, thin hydraulic actuators, faint cyan vapour at the nozzle mouths; reads as agility, not as a main engine.
- **modules-dust-cloud** — squat aerosol dispenser canister with a wide flared spray head and a ring of small charge tubes, pale particulate haze escaping the head.
- **modules-reactive-armor** — rectangular slab of bolted-on explosive reactive armour tiles, angled plates with visible seams and warning chevrons (symbols only, no text), one tile slightly scorched.
- **modules-anti-missile** — compact turret with a short twin autocannon and a small radar dish folded beside it, belt feed visible; clearly a point-defence mount, not a ship weapon.

---

## Пачка S — стартовый экран (к M15.7)

Один кадр во весь экран: он лежит за окном входа, и это первое, что видит игрок. Поверх его середины
стоит окно логина и пароля 360 px, поэтому центр кадра должен быть тихим и тёмным — но не пустым:
на телефоне стоймя от кадра 16:9 остаётся как раз центральная колонка. Стиль: STYLE-SPLASH.
Исходник класть в `art/splash/splash-login.png`, нарезка — `python tools/splash.py`.

### splash-login
Epic space battle in deep space above a planet, seen from a cinematic three-quarter angle, not top-down. LEFT THIRD: a wedge-hulled ranger patrol corvette and two heavier ranger cruisers in navy-and-white livery with light-blue chevrons sweep inward, firing thin white-cyan railgun streaks and launching chunky dark torpedoes with bright orange exhaust trails. RIGHT THIRD: ragged pirate raiders welded from mismatched scavenged plates, rust-red and soot-black armour with scorched patches, return fire with amber tracers; one pirate hull is breaking apart in an orange-white explosion with a dark smoke ring and slowly tumbling debris, another trails burning venting gas. UPPER AREA: a small cold white star with a tight corona rim-lighting every hull from the upper left, faint navy and violet nebula dust. LOWER THIRD: the curved limb of a blue-green world with thin cloud bands and a thin atmospheric glow along the horizon, dark and low in contrast. MIDDLE OF THE FRAME: quiet dark space between the two formations — only the planet limb, sparse cold stars and faint dust, no ships and no flashes there. Scale reads large: hulls are small and sharp at distance, and the battle fills the frame.
