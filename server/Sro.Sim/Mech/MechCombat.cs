namespace Sro.Sim.Mech;

/// <summary>Части меха (§2). Порядок — порядок столбцов в таблицах попаданий.</summary>
public enum MechPart { Body = 0, Left = 1, Right = 2, Chassis = 3 }

/// <summary>С какой стороны цели пришёл выстрел (§19, §23): четыре сектора, хотя направлений восемь.</summary>
public enum MechSide { Front, Left, Right, Rear }

/// <summary>Коды отказов наземного боя — они же уходят клиенту в mechRefused.</summary>
public static class MechCodes
{
    /// <summary>Ретранслятора здесь нет: не тот док или кампания не пройдена.</summary>
    public const string NoRelay = "noRelay";
    public const string NoBattle = "noBattle";
    public const string NotYourTurn = "notYourTurn";
    public const string AlreadyMoved = "alreadyMoved";
    public const string Unreachable = "unreachable";
    public const string NoTarget = "noTarget";
    public const string OutOfRange = "outOfRange";
    public const string NoLine = "noLine";
    public const string ArmDown = "armDown";
    public const string BadPart = "badPart";
    public const string BadAct = "badAct";
    public const string Over = "over";
}

/// <summary>
/// Формулы наземного боя (баланс мехов §10, §15–43). Всё, что показывает прогноз, считается здесь и в
/// зеркале client/src/mech/rules.ts, и сверяется вектором shared/test-vectors/mech.json.
/// </summary>
public static class MechCombat
{
    public static readonly string[] PartNames = ["body", "left", "right", "chassis"];
    public static readonly string[] SideNames = ["front", "left", "right", "rear"];

    public static string Name(MechPart part) => PartNames[(int)part];

    public static MechPart? ParsePart(string? name) => Array.IndexOf(PartNames, name) is var i and >= 0 ? (MechPart)i : null;

    /// <summary>
    /// Ход с повреждённым шасси (§10): больше половины — полный, от четверти до половины — на 1 меньше,
    /// меньше четверти — на 2, но не меньше 1; шасси нет — стоит, хотя стрелять может.
    /// </summary>
    public static int MoveRange(int baseMove, int chassisHp, int chassisMax)
    {
        if (chassisHp <= 0) return 0;
        if (2 * chassisHp > chassisMax) return baseMove;
        if (4 * chassisHp > chassisMax) return Math.Max(1, baseMove - 1);
        return Math.Max(1, baseMove - 2);
    }

    /// <summary>
    /// Сторона цели, с которой стоит стрелок. f — насколько он впереди цели, s — насколько справа.
    /// Ровно на диагонали (|f| = |s|) — бок: фланг вознаграждается, а не спорит с фронтом.
    /// </summary>
    public static MechSide Side(int ax, int ay, int tx, int ty, int targetDir)
    {
        var (fx, fy) = MechField.Steps[targetDir];
        var rx = ax - tx;
        var ry = ay - ty;
        var f = rx * fx + ry * fy;
        // «Вправо» от взгляда: поворот вперёд-вектора на 90° по часовой в экранных координатах (y вниз).
        var s = rx * -fy + ry * fx;
        if (f > Math.Abs(s)) return MechSide.Front;
        if (-f > Math.Abs(s)) return MechSide.Rear;
        return s > 0 ? MechSide.Right : MechSide.Left;
    }

    /// <summary>
    /// Шанс попасть (§20): точность оружия + дальность + своё движение + сторона цели − прицельный,
    /// в пределах hitMin..hitMax. steps — сколько клеток стрелок прошёл в этот ход, moveRange — его ход.
    /// </summary>
    public static int HitChance(MechCombatDef c, MechWeapon w, int distance, int steps, int moveRange, MechSide side, bool aimed)
    {
        var range = distance < w.OptimalMin ? (w.OptimalMin - distance) * c.RangeStep
            : distance > w.OptimalMax ? (distance - w.OptimalMax) * c.RangeStep
            : 0;
        var move = steps == 0 ? c.StillBonus : 2 * steps <= moveRange ? 0 : -c.FarMovePenalty;
        var flank = side switch { MechSide.Rear => c.RearBonus, MechSide.Front => 0, _ => c.FlankBonus };
        var chance = w.Accuracy - Math.Min(range, c.RangeFloor) + move + flank - (aimed ? c.AimedPenalty : 0);
        return Math.Clamp(chance, c.HitMin, c.HitMax);
    }

    /// <summary>
    /// Шанс блока (§39–40): щит жив и выстрел не с тыла. Щит слева лучше всего держит левый бок, хуже — правый;
    /// щит справа — наоборот. Нет щита — 0.
    /// </summary>
    public static int BlockChance(MechCombatDef c, MechShield? shield, MechPart shieldArm, bool shieldAlive, MechSide side)
    {
        if (shield is null || !shieldAlive || side == MechSide.Rear) return 0;
        var mod = side == MechSide.Front ? 0
            : (side == MechSide.Left) == (shieldArm == MechPart.Left) ? c.ShieldSame
            : c.ShieldOpposite;
        return Math.Clamp(shield.Block + mod, 0, c.BlockMax);
    }

    /// <summary>
    /// Веса частей для стороны (§24–26), [корпус, левая, правая, шасси]. Разбитую руку и шасси
    /// уже не выбить второй раз — их доля уходит в корпус.
    /// </summary>
    public static int[] PartWeights(MechCombatDef c, MechSide side, int[] hp)
    {
        var table = side switch
        {
            MechSide.Front => c.Front,
            MechSide.Rear => c.Rear,
            MechSide.Left => c.Left,
            _ => [c.Left[0], c.Left[2], c.Left[1], c.Left[3]],
        };
        var weights = (int[])table.Clone();
        for (var i = 1; i < 4; i++)
        {
            if (hp[i] > 0) continue;
            weights[0] += weights[i];
            weights[i] = 0;
        }
        return weights;
    }

    /// <summary>Урон после брони (§29–31): raw × (1 − armor / (armor + 100)), до целого.</summary>
    public static int Damage(double raw, int armor) => Round(raw * (1 - armor / (armor + 100.0)));

    /// <summary>Вилка урона для прогноза: самый слабый бросок в самую крепкую броню и самый сильный — в самую слабую.</summary>
    public static (int Min, int Max) DamageRange(MechCombatDef c, MechWeapon w, IEnumerable<int> armors)
    {
        var list = armors.ToList();
        if (list.Count == 0) return (0, 0);
        return (Damage(w.Damage * c.RollMin, list.Max()), Damage(w.Damage * c.RollMax, list.Min()));
    }

    /// <summary>Округление как Math.round в JS для положительных: половина — вверх.</summary>
    public static int Round(double x) => (int)Math.Floor(x + 0.5);
}
