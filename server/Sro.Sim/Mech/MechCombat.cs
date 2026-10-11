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
    /// Дальность после штрафа повреждённого шасси делится пополам с округлением вверх.
    /// Пока шасси живо, остаётся хотя бы одна клетка; разбитое шасси двигаться не может.
    /// </summary>
    public static int MoveRange(int baseMove, int chassisHp, int chassisMax)
    {
        if (chassisHp <= 0) return 0;
        var penalty = 2 * chassisHp > chassisMax ? 0 : 4 * chassisHp > chassisMax ? 1 : 2;
        return Math.Max(1, (Math.Max(1, baseMove - penalty) + 1) / 2);
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
    /// Точность оружия и движение минус штраф за каждую клетку дальше первой и прицельный штраф.
    /// Сторона цели не влияет; потолок 90 % оставляет шанс промаха даже в упор.
    /// </summary>
    public static int HitChance(MechCombatDef c, MechWeapon w, int distance, int steps, int moveRange, MechSide side, bool aimed)
    {
        // Every extra cell reduces accuracy. Facing gives no combat advantage.
        var range = Math.Max(0, distance - 1) * c.RangeStep;
        var move = steps == 0 ? c.StillBonus : 2 * steps <= moveRange ? 0 : -c.FarMovePenalty;
        var chance = w.Accuracy - range + move - (aimed ? c.AimedPenalty : 0);
        return Math.Clamp(chance, c.HitMin, Math.Min(90, c.HitMax));
    }

    /// <summary>
    /// Исправный щит блокирует с одинаковой вероятностью со всех сторон. Нет щита — 0.
    /// </summary>
    public static int BlockChance(MechCombatDef c, MechShield? shield, MechPart shieldArm, bool shieldAlive, MechSide side)
    {
        if (shield is null || !shieldAlive) return 0;
        return Math.Clamp(shield.Block, 0, c.BlockMax);
    }

    /// <summary>
    /// Единые веса частей со всех сторон, [корпус, левая, правая, шасси]. Разбитую руку и шасси
    /// уже не выбить второй раз — их доля уходит в корпус.
    /// </summary>
    public static int[] PartWeights(MechCombatDef c, MechSide side, int[] hp)
    {
        var table = c.Front;
        var weights = (int[])table.Clone();
        for (var i = 1; i < 4; i++)
        {
            if (hp[i] > 0) continue;
            weights[0] += weights[i];
            weights[i] = 0;
        }
        return weights;
    }

    /// <summary>Удвоенный урон после брони: 2 × raw × (1 − armor / (armor + 100)), до целого.</summary>
    public static int Damage(double raw, int armor) => Round(2 * raw * (1 - armor / (armor + 100.0)));

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
