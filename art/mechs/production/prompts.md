# Фактические промпты — 2026-09-23

## reference-medium-8dir-v1

Use case: stylized-concept. Generate ONE production reference sprite sheet for SRO tactical mech game. Image 1 is ONLY the approved gameplay camera/material/style reference; do NOT reproduce its UI or environment. A single identical medium combat mech seen at eight fixed compass headings, one mech per cell. Genuine transparent RGBA background, not black paint or checkerboard. Landscape EXACT 2:1 aspect ratio, target 2048x1024. EXACT regular 4 columns x 2 rows of EQUAL SQUARE cells, invisible grid, no text, no labels, no separators, no shadows outside robot. Every cell contains one entire same mech, same scale, comfortably inside central 72% cell, soles at 76% cell height, ground pivot x50% y72%.

CAMERA fixed orthographic tactical game view from elevated south looking north at ground, elevation 60 degrees above horizon. See predominantly tops plus some vertical sides. Camera NEVER rotates or changes pitch, lighting always world upper-left. Robot changes its yaw in world. Do not simply rotate a flat illustration. Consistent square-grid game camera, NOT an isometric diamond scene.
EXACT cell order left to right:
TOP ROW: N (BACK visible, looking away toward top of screen); NE (back and right side visible, looking upper-right); E (right-facing PROFILE, muzzle to screen right); SE (front 3/4 looking lower-right).
BOTTOM ROW: S (FRONT visible, looking toward bottom of screen); SW (front 3/4 looking lower-left); W (left-facing PROFILE, muzzle to screen left); NW (back 3/4 looking upper-left).
Recognizably different FRONT and BACK: front chest pale armored plate with tiny CYAN horizontal visor high center; BACK has dark rectangular radiator grille and two tiny AMBER lights, no visor. EAST/WEST views are actual side profiles. Heads look along facing, hips and feet point same direction.

MODEL invariants all cells: squat sturdy medium biped combat mech, worn pale steel plates and blue-gray gunmetal, chunky rectangular torso, two sturdy articulated legs, large functional feet, restrained surface detail readable at 80px. Its ANATOMICAL RIGHT arm is a medium autocannon with one short thick forward-pointing barrel; its ANATOMICAL LEFT arm carries one compact rectangular PHYSICAL LIGHT SHIELD, blue-gray face with pale steel rim. Shield vertical at its side, not a glowing energy bubble. No handheld rifle with two hands. No backpack initially. Fixed identical plate design and dimensions throughout all eight views. In S front view autocannon appears viewer LEFT, shield viewer RIGHT. In N back view autocannon viewer RIGHT, shield viewer LEFT. Both mounted arms move with torso, stay connected; correct occlusion in profile. This is a turnaround of ONE specific robot, not eight designs.
Balanced neutral studio shading, clean transparent edges and large clear margins, no cast ground shadow, no platform, no ground plane, no battlefield, no environment, no interface, no lettering, no insignia, no numbers or watermark. This sheet is a visual rig reference to later extract separately generated torso/chassis/arm/shield component sheets.


## reference-medium-8dir-v2

Edit the provided mech turnaround sheet. KEEP same exact medium mech design, colors, two legs, RIGHT autocannon and LEFT physical shield, 4x2 grid, actual transparent alpha and same eight headings. Correct ONLY its camera and facing geometry for a high overhead tactical sprite.
Raise fixed camera to 65 degrees ABOVE the horizontal ground plane, looking DOWN, so large top/roof surfaces dominate and the legs are visibly foreshortened. NOT eye-level robot portraits. Ground footprint is compact. Whole robot remains upright on ground. Show FRONT and BACK differently under this fixed overhead camera. Camera and light never turn.
The eight facing directions MUST be:
row1col1 N: view the BACK of robot; weapon aimed AWAY from viewer toward upper edge, see rear/breech of cannon not its muzzle opening.
row1col2 NE: BACK three-quarter, aim upper-right.
row1col3 E: right profile, aim right.
row1col4 SE: FRONT three-quarter, aim lower-right.
row2col1 S: FRONT, aim down toward viewer.
row2col2 SW: FRONT three-quarter, aim lower-left.
row2col3 W: left profile, aim left.
row2col4 NW: BACK three-quarter, aim upper-left.
SE and SW must be visibly distinct opposite headings. N versus S must be distinct back/front. Anatomical right arm is always cannon, left always shield. N has cannon on screen right, S has cannon on screen left. Keep SAME armor silhouette and parts across all frames. No labels or grid lines or ground shadow. More transparent padding: each robot inside central 70% of its equal square cell. Equal 4x2 cells on exact2:1 landscape canvas. Do not add anything.


## body-medium-8dir-v1

Use case: precise-object-edit. Input image is the EDIT TARGET, the eight-heading medium mech rig sheet. Produce ONE modular component sprite sheet by removing unwanted parts from that exact sheet. Keep original canvas dimensions/aspect 2:1 and exact 4 columns x 2 rows grid. DO NOT move, rescale, recenter or change yaw of retained parts. Every pixel region left by removed parts is genuinely alpha-transparent. Preserve the existing elevated camera, pale steel blue-gray material, light and the particular geometry. Eight fixed cells ordered N,NE,E,SE / S,SW,W,NW. No labels, grid lines, shadows, floor or background. THIS COMPONENT: Retain ONLY central armored TORSO, small head/sensor and shoulder attachment socket rings of each mech. REMOVE both complete arms including arm shoulder armor, cannon, shield, pelvis, thighs, lower legs and feet. Keep torso at EXACT original coordinates and size. Show small empty shoulder sockets, not arm stumps extending down. The entire full-size cell remains, mostly transparent: this sheet must be compositable with other extracted components at the same origin. Do not enlarge small parts to fill their cell. Exactly one component per original mech cell, preserving original alignment.


## chassis-medium-biped-8dir-v1

Use case: precise-object-edit. Input image is the EDIT TARGET, the eight-heading medium mech rig sheet. Produce ONE modular component sprite sheet by removing unwanted parts from that exact sheet. Keep original canvas dimensions/aspect 2:1 and exact 4 columns x 2 rows grid. DO NOT move, rescale, recenter or change yaw of retained parts. Every pixel region left by removed parts is genuinely alpha-transparent. Preserve the existing elevated camera, pale steel blue-gray material, light and the particular geometry. Eight fixed cells ordered N,NE,E,SE / S,SW,W,NW. No labels, grid lines, shadows, floor or background. THIS COMPONENT: Retain ONLY PELVIS, HIP JOINTS, BOTH THIGHS, KNEES, SHINS and FEET of each mech. REMOVE head, chest torso, shoulders, both arms, cannon and shield. Keep hip/legs/feet at EXACT original coordinates, orientation and size within each cell. Keep both legs separated by a transparent gap. Only lower locomotion assembly, with exposed upper waist mount. The entire full-size cell remains, mostly transparent: this sheet must be compositable with other extracted components at the same origin. Do not enlarge small parts to fill their cell. Exactly one component per original mech cell, preserving original alignment.


## arm-autocannon-right-8dir-v1

Use case: precise-object-edit. Input image is the EDIT TARGET, the eight-heading medium mech rig sheet. Produce ONE modular component sprite sheet by removing unwanted parts from that exact sheet. Keep original canvas dimensions/aspect 2:1 and exact 4 columns x 2 rows grid. DO NOT move, rescale, recenter or change yaw of retained parts. Every pixel region left by removed parts is genuinely alpha-transparent. Preserve the existing elevated camera, pale steel blue-gray material, light and the particular geometry. Eight fixed cells ordered N,NE,E,SE / S,SW,W,NW. No labels, grid lines, shadows, floor or background. THIS COMPONENT: Retain ONLY robot's ANATOMICAL RIGHT COMPLETE ARM WITH AUTOCANNON, including shoulder attachment, upper arm, elbow, forearm and barrel. REMOVE torso, head, legs, pelvis, shield and LEFT arm. Keep surviving arm at EXACT original pose, original cell position and scale. Do NOT center it. It stays screen-right in N/back, screen-left in S/front. Reconstruct only hidden arm geometry where occluded by torso. Barrel follows original heading. The entire full-size cell remains, mostly transparent: this sheet must be compositable with other extracted components at the same origin. Do not enlarge small parts to fill their cell. Exactly one component per original mech cell, preserving original alignment.


## shield-light-left-8dir-v1

Use case: precise-object-edit. Input image is the EDIT TARGET, the eight-heading medium mech rig sheet. Produce ONE modular component sprite sheet by removing unwanted parts from that exact sheet. Keep original canvas dimensions/aspect 2:1 and exact 4 columns x 2 rows grid. DO NOT move, rescale, recenter or change yaw of retained parts. Every pixel region left by removed parts is genuinely alpha-transparent. Preserve the existing elevated camera, pale steel blue-gray material, light and the particular geometry. Eight fixed cells ordered N,NE,E,SE / S,SW,W,NW. No labels, grid lines, shadows, floor or background. THIS COMPONENT: Retain ONLY robot's ANATOMICAL LEFT COMPLETE ARM WITH PHYSICAL SHIELD, including shoulder attachment, upper arm, elbow and shield. REMOVE torso, head, legs, pelvis, cannon and RIGHT arm. Keep surviving shield arm at EXACT original pose, original cell position and scale. Do NOT center it. It stays screen-left in N/back, screen-right in S/front. Reconstruct hidden portions of this arm and shield previously occluded by torso. The entire full-size cell remains, mostly transparent: this sheet must be compositable with other extracted components at the same origin. Do not enlarge small parts to fill their cell. Exactly one component per original mech cell, preserving original alignment.


## shield-light-left-8dir-v2

Use case: precise-object-edit. Correct the attached modular shield sprite sheet by removing ALL accidental non-shield equipment. It must contain ONLY eight views of ONE PHYSICAL RECTANGULAR LIGHT SHIELD with its short supporting LEFT forearm/elbow and a SMALL rounded shoulder connector. Each is an isolated shield-arm component, NOT a torso with a shield. Transparent background.
In every cell DELETE any cannon barrel, gun muzzle, head, cyan visor, helmet, chest, torso, breastplate, legs, pelvis, sensor, rifle or backpack. Their former positions become transparent. None of those objects may appear anywhere. The large central mass at top of most objects is erroneous robot body: REMOVE it. Only a slim bent mechanical arm and compact shoulder joint remain behind the rectangular shield.
Keep shield faces themselves: worn blue-gray flat metal rectangular shield face, pale steel rim, small corner bolts. Physical shield, not energy bubble. Left-handed complete shield-arm, eight different world headings in fixed elevated 65-degree orthographic camera. Grid exactly4x2 with row1 N NE E SE, row2 S SW W NW. Same component identity, not different shield models. For N/back and S/front the shield faces present differently; E is far-side shield reverse, W shows outward armored face. Reconstruct necessary slim arm linkage but NEVER reconstruct head or torso. Keep original sheet size, equal square cells, positions, scale and headings. No text, no symbols, no cast shadow, no ground.


## chassis-medium-biped-8dir-v2

Edit this exact 4x2 transparent sprite sheet of lower mech legs. Keep its same worn pale-steel / blue-gray medium biped design, camera elevation, scale, square-cell layout and eight poses. Fix ONLY the three rear-facing cells: TOP-LEFT (N), TOP ROW SECOND (NE) and BOTTOM-RIGHT (NW). They currently show front knee plates and front toe armor even when meant to face away. Turn those lower-body assemblies correctly in 3D.
N cell is a REAR VIEW of hips and both legs: black hydraulic cylinders and flexible cables behind knees, small mechanical heel blocks nearest bottom of screen, the longer toes extend AWAY toward top and are hidden behind ankles. No large white forward kneecaps facing camera. Back of pelvis has dark actuator/service panel, NOT the white front groin plate. NE is the same BACK three-quarter view with toes extending upper-right, rear knee pistons visible. NW same BACK three-quarter with toes upper-left.
The other five cells remain the same front and side views, unchanged: E profile facing right, SE front3/4 lower-right, S front towards bottom, SW front3/4 lower-left, W profile facing left.
Keep empty circular torso mounting socket at top of each pelvis. No torso, head, arms, weapons, shield or background. Exact 4x2 invisible grid on same 2:1 sheet, genuine transparent alpha, no letters/numbers/grid/labels/shadows. All legs belong to the same mechanical model. This is a geometry correction, not a new robot design.

