"""Build the versioned canyon geometry. Coordinates are world units, Y points down."""
import json, math
from pathlib import Path

corners = [(0, 0), (150, 0), (200, 50), (200, 120), (130, 120),
           (130, 65), (70, 65), (70, 145), (-40, 145), (-70, 80), (-70, 0)]
curve = []
for i, p in enumerate(corners):
    prev, nxt = corners[i - 1], corners[(i + 1) % len(corners)]
    def inset(q):
        d = math.dist(p, q)
        r = min(32, d * .48)
        return (p[0] + (q[0] - p[0]) * r / d, p[1] + (q[1] - p[1]) * r / d)
    a, b = inset(prev), inset(nxt)
    for j in range(13):
        t = j / 12
        curve.append(((1-t)**2*a[0]+2*t*(1-t)*p[0]+t*t*b[0],
                       (1-t)**2*a[1]+2*t*(1-t)*p[1]+t*t*b[1]))

# Sample the straights too, so rock spurs can shape every part of the canyon.
points = []
for i, p in enumerate(curve):
    q = curve[(i+1) % len(curve)]
    for j in range(max(1, math.ceil(math.dist(p, q) / 3))):
        t = j / max(1, math.ceil(math.dist(p, q) / 3))
        points.append((p[0] + (q[0]-p[0])*t, p[1] + (q[1]-p[1])*t))

distances = [0.0]
for a, b in zip(points, points[1:]):
    distances.append(distances[-1] + math.dist(a, b))
length = distances[-1] + math.dist(points[-1], points[0])

def half_width(i, side):
    phase = distances[i] / length * math.tau
    # Independent banks: broad basins, narrow passes and local rock projections.
    width = 16 + 1.8*math.sin(phase*3 + side*1.4) + .9*math.sin(phase*11 + side*2)
    for center, depth, spread in [(0.19, 3.6, .023), (.46, 4.5, .028), (.73, 3.8, .024)]:
        delta = (distances[i]/length - center - side*.018 + .5) % 1 - .5
        width -= depth*math.exp(-(delta/spread)**2)
    # Keep the offset inside the local bend radius, with no folded inner bank.
    a, p, b = points[i-1], points[i], points[(i+1) % len(points)]
    cross = abs((p[0]-a[0])*(b[1]-p[1]) - (p[1]-a[1])*(b[0]-p[0]))
    if cross > 1e-8:
        radius = math.dist(a,p)*math.dist(p,b)*math.dist(a,b)/(2*cross)
        width = min(width, radius*.82)
    return max(9.5, width)

def pt(p):
    return dict(x=round(p[0], 6), y=round(p[1], 6))

edges = [[], []]
for i, p in enumerate(points):
    a, b = points[i-1], points[(i+1) % len(points)]
    dx, dy = b[0]-a[0], b[1]-a[1]
    d = math.hypot(dx, dy)
    for side, sign in enumerate([-1, 1]):
        width = half_width(i, side)
        edges[side].append((p[0]-sign*dy/d*width, p[1]+sign*dx/d*width))
walls = [dict(id=f'wall-{side}-{i}', a=pt(p), b=pt(edge[(i+1) % len(edge)]), reason='WALL')
         for side, edge in enumerate(edges) for i, p in enumerate(edge)]
def gate(i):
    p, nxt = points[i], points[(i+1) % len(points)]
    dx, dy = nxt[0]-p[0], nxt[1]-p[1]
    d = math.hypot(dx, dy)
    return dict(a=pt(edges[1][i]), b=pt(edges[0][i]), normal=pt((dx/d, dy/d)))
# Spawn on the first straight, before the finish; CP1 starts beyond it.
finish_index = min(range(len(points)), key=lambda i: math.dist(points[i], (30,0)))
finish_gate = gate(finish_index)
finish = dict(a=pt((30, finish_gate['b']['y'])), b=pt((30, finish_gate['a']['y'])), normal=pt((1, 0)))
checkpoint_indices = [min(range(len(points)), key=lambda i: abs(distances[i] - (distances[finish_index] + length*j/11) % length)) for j in range(1,11)]
track = dict(id='red-canyon', version='2', name='Красный каньон', lapCount=3,
             spawn=pt((26, 0)), spawnHeading=0, environment='desert-industrial', isActive=True,
             centerline=[pt(p) for p in points], walls=walls,
             obstacles=[dict(id='column-1', center=pt((102, 77)), radius=1.5, reason='COLUMN'),
                        dict(id='rock-1', center=pt((-58, 45)), radius=1.8, reason='ROCK')],
             checkpoints=[gate(i) for i in checkpoint_indices], finishLine=finish,
             length=round(length))
path = Path(__file__).resolve().parents[1] / 'shared/race/tracks/red-canyon.json'
path.parent.mkdir(parents=True, exist_ok=True)
path.write_text(json.dumps(track, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
print(f'{path.name}: {track["length"]} units, {len(walls)} walls, {len(track["checkpoints"])} checkpoints')
