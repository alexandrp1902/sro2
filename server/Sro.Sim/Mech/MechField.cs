namespace Sro.Sim.Mech;

/// <summary>
/// Поле наземного боя (M21). Легенда клеток:
/// «.» и «,» — земля (два рисунка), «r» — камни, «c» — ящик, «w» — стена, «b» — здание (квадрат 2×2).
/// Всё, кроме земли, непроходимо; линию огня закрывают только стена и здание — через ящик и камни стреляют.
///
/// Направления — 8, как нарисован риг: 0 — север (вверх по экрану), дальше по часовой. Ход — 8 соседей,
/// диагональ стоит одну клетку, но срезать угол непроходимой клетки нельзя. Дальность — по Чебышёву.
/// Зеркало — client/src/mech/rules.ts.
/// </summary>
public sealed class MechField
{
    public const string Legend = ".,rcwb";

    /// <summary>Шаг по x и y для каждого из 8 направлений.</summary>
    public static readonly (int Dx, int Dy)[] Steps =
        [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];

    private readonly string[] _map;

    public MechField(string[] map)
    {
        _map = map;
        Height = map.Length;
        Width = map.Length > 0 ? map[0].Length : 0;
    }

    public int Width { get; }

    public int Height { get; }

    public int Index(int x, int y) => y * Width + x;

    public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public char Cell(int x, int y) => _map[y][x];

    public bool Passable(int x, int y) => Inside(x, y) && Cell(x, y) is '.' or ',';

    /// <summary>Закрывает ли клетка выстрел: только стена и здание.</summary>
    public bool Blocks(int x, int y) => Cell(x, y) is 'w' or 'b';

    public static int Distance(int ax, int ay, int bx, int by) => Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

    /// <summary>Направление с (0,0) на (dx,dy): ближайшее из 8 по углу.</summary>
    public static int Direction(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return 0;
        // В целых, чтобы оба языка решали одинаково: tan 22.5° ≈ 0.4142 заменён на 12/29. На поле до 24 клеток
        // ничьих нет (29a = 12b требует b, кратного 29), а разница с настоящим углом не видна глазу.
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        int dir;
        if (29 * ax < 12 * ay) dir = dy < 0 ? 0 : 4;
        else if (29 * ay < 12 * ax) dir = dx > 0 ? 2 : 6;
        else dir = dx > 0 ? (dy < 0 ? 1 : 3) : (dy < 0 ? 7 : 5);
        return dir;
    }

    /// <summary>
    /// Куда можно дойти за range шагов: клетка → число шагов. Своя клетка не входит. occupied — клетки,
    /// где стоят живые мехи: сквозь них не ходят.
    /// </summary>
    public Dictionary<int, int> Reach(int x, int y, int range, ISet<int> occupied)
    {
        var dist = Flood(x, y, range, occupied, out _);
        dist.Remove(Index(x, y));
        return dist;
    }

    /// <summary>Путь от (x,y) до (tx,ty) — список индексов клеток без стартовой; null — не дойти за range.</summary>
    public List<int>? Path(int x, int y, int tx, int ty, int range, ISet<int> occupied)
    {
        var dist = Flood(x, y, range, occupied, out var parent);
        var target = Index(tx, ty);
        if (!dist.ContainsKey(target) || target == Index(x, y)) return null;
        var path = new List<int>();
        for (var at = target; at != Index(x, y); at = parent[at]) path.Add(at);
        path.Reverse();
        return path;
    }

    /// <summary>Шагов до каждой клетки без ограничения хода — поле расстояний для ИИ.</summary>
    public Dictionary<int, int> Distances(int x, int y, ISet<int> occupied) =>
        Flood(x, y, int.MaxValue, occupied, out _);

    private Dictionary<int, int> Flood(int x, int y, int range, ISet<int> occupied, out Dictionary<int, int> parent)
    {
        var start = Index(x, y);
        var dist = new Dictionary<int, int> { [start] = 0 };
        parent = new Dictionary<int, int>();
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            var d = dist[at];
            if (d >= range) continue;
            var cx = at % Width;
            var cy = at / Width;
            foreach (var (dx, dy) in Steps)
            {
                var nx = cx + dx;
                var ny = cy + dy;
                if (!Passable(nx, ny)) continue;
                // Угол срезать нельзя: диагональ открыта, только если обе прямые соседки проходимы.
                if (dx != 0 && dy != 0 && (!Passable(cx + dx, cy) || !Passable(cx, cy + dy))) continue;
                var next = Index(nx, ny);
                if (occupied.Contains(next) || dist.ContainsKey(next)) continue;
                dist[next] = d + 1;
                parent[next] = at;
                queue.Enqueue(next);
            }
        }
        return dist;
    }

    /// <summary>
    /// Линия огня между центрами клеток. Идём по большей оси целыми шагами; по меньшей точка может лечь ровно
    /// между двумя клетками — тогда шаг закрыт, только если закрыты обе. Так правило симметрично: из A в B
    /// видно ровно тогда, когда из B в A. Концы не проверяются.
    /// </summary>
    public bool LineOfFire(int ax, int ay, int bx, int by)
    {
        var n = Distance(ax, ay, bx, by);
        for (var i = 1; i < n; i++)
        {
            var (x0, x1) = Axis(ax, bx - ax, i, n);
            var (y0, y1) = Axis(ay, by - ay, i, n);
            if (Blocks(x0, y0) && Blocks(x1, y0) && Blocks(x0, y1) && Blocks(x1, y1)) return false;
        }
        return true;
    }

    /// <summary>Клетка(и) по одной оси на шаге i из n: одна, если точка не ровно на границе, иначе две соседние.</summary>
    private static (int, int) Axis(int a, int d, int i, int n)
    {
        var t = a * n + d * i; // точка, умноженная на n; на поле всегда >= 0
        var cell = t / n;
        var rest = t % n;
        if (2 * rest == n) return (cell, cell + 1);
        return 2 * rest < n ? (cell, cell) : (cell + 1, cell + 1);
    }

    /// <summary>Левые верхние углы зданий; null — где-то «b» не складывается в квадраты 2×2.</summary>
    public List<(int X, int Y)>? Buildings()
    {
        var seen = new bool[Width * Height];
        var list = new List<(int, int)>();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (Cell(x, y) != 'b' || seen[Index(x, y)]) continue;
                if (!(Inside(x + 1, y + 1) && Cell(x + 1, y) == 'b' && Cell(x, y + 1) == 'b' && Cell(x + 1, y + 1) == 'b'))
                    return null;
                foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                {
                    if (seen[Index(x + dx, y + dy)]) return null;
                    seen[Index(x + dx, y + dy)] = true;
                }
                list.Add((x, y));
            }
        }
        return list;
    }
}
