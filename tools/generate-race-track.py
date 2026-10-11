"""Build the versioned canyon geometry. Coordinates are world units, Y points down."""
import json, math
from pathlib import Path

corners = [(0, 0), (150, 0), (200, 50), (200, 120), (130, 120),
           (130, 65), (70, 65), (70, 145), (-40, 145), (-70, 80), (-70, 0)]
points = []
for i, p in enumerate(corners):
    prev, nxt = corners[i - 1], corners[(i + 1) % len(corners)]
    def inset(q):
        d = math.dist(p, q)
        r = min(16, d * .3)
        return (p[0] + (q[0] - p[0]) * r / d, p[1] + (q[1] - p[1]) * r / d)
    a, b = inset(prev), inset(nxt)
    for j in range(13):
        t = j / 12
        points.append(((1-t)**2*a[0]+2*t*(1-t)*p[0]+t*t*b[0],
                       (1-t)**2*a[1]+2*t*(1-t)*p[1]+t*t*b[1]))

def pt(p):
    return dict(x=round(p[0], 6), y=round(p[1], 6))

edges = [[], []]
for i, p in enumerate(points):
    a, b = points[i-1], points[(i+1) % len(points)]
    dx, dy = b[0]-a[0], b[1]-a[1]
    d = math.hypot(dx, dy)
    width = 3.1 if 65 < p[0] < 75 and p[1] > 85 else 4
    for side, sign in enumerate([-1, 1]):
        edges[side].append((p[0]-sign*dy/d*width, p[1]+sign*dx/d*width))
walls = [dict(id=f'wall-{side}-{i}', a=pt(p), b=pt(edge[(i+1) % len(edge)]), reason='WALL')
         for side, edge in enumerate(edges) for i, p in enumerate(edge)]
def gate(i):
    p, nxt = points[i], points[(i+1) % len(points)]
    dx, dy = nxt[0]-p[0], nxt[1]-p[1]
    d = math.hypot(dx, dy)
    return dict(a=pt((p[0]-dy/d*4, p[1]+dx/d*4)),
                b=pt((p[0]+dy/d*4, p[1]-dx/d*4)), normal=pt((dx/d, dy/d)))
# Spawn on the first straight, before the finish; CP1 starts beyond it.
finish = dict(a=pt((30, -4)), b=pt((30, 4)), normal=pt((1, 0)))
track = dict(id='red-canyon', version='1', name='Красный каньон', lapCount=3,
             spawn=pt((26, 0)), spawnHeading=0, environment='desert-industrial', isActive=True,
             centerline=[pt(p) for p in points], walls=walls,
             obstacles=[dict(id='column-1', center=pt((102, 68.3)), radius=.6, reason='COLUMN'),
                        dict(id='rock-1', center=pt((-66.5, 45)), radius=.65, reason='ROCK')],
             checkpoints=[gate(i) for i in range(20, len(points), 13)], finishLine=finish,
             length=round(sum(math.dist(p, points[(i+1) % len(points)]) for i, p in enumerate(points))))
path = Path(__file__).resolve().parents[1] / 'shared/race/tracks/red-canyon.json'
path.parent.mkdir(parents=True, exist_ok=True)
path.write_text(json.dumps(track, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
print(f'{path.name}: {track["length"]} units, {len(walls)} walls, {len(track["checkpoints"])} checkpoints')
