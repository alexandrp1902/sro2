namespace Sro.Sim.Mech;

/// <summary>Мех на поле. Hp — по частям, в порядке <see cref="MechPart"/>.</summary>
public sealed class MechUnit
{
    public required string Id { get; init; }
    public required string Side { get; init; }
    public required string Unit { get; init; }
    public required MechUnitDef Def { get; init; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Dir { get; set; }
    public required int[] Hp { get; init; }
    public required int[] Max { get; init; }
    public bool Activated { get; set; }

    public bool Alive => Hp[(int)MechPart.Body] > 0;

    public bool PartAlive(MechPart part) => Hp[(int)part] > 0;
}

/// <summary>Что случилось — для анимации на клиенте. Поля, не нужные виду события, остаются по умолчанию.</summary>
public sealed record MechEvent(
    string Kind,
    string Unit,
    string? Target = null,
    int[]? Path = null,
    string? Part = null,
    int Dmg = 0,
    int Chance = 0,
    int Dir = -1,
    string? Weapon = null,
    int N = 0)
{
    public const string Move = "move";
    public const string Face = "face";
    public const string Shot = "shot";
    public const string Miss = "miss";
    public const string Block = "block";
    public const string Hit = "hit";
    public const string PartDown = "partDown";
    public const string MechDown = "mechDown";
    public const string Round = "round";
}

/// <summary>Команда хода: move (x, y), attack (target, part — прицельный), end (dir — поворот напоследок).</summary>
public sealed record MechCommand(string Act, int X = 0, int Y = 0, int? Dir = null, string? Target = null, string? Part = null)
{
    public const string MoveAct = "move";
    public const string AttackAct = "attack";
    public const string EndAct = "end";
}

/// <summary>Мех в сообщении клиенту.</summary>
public sealed record MechUnitView(
    string Id, string Side, string Unit, string Name, int X, int Y, int Dir, int[] Hp, int[] Max, bool Activated);

/// <summary>Всё поле в одном сообщении: оно маленькое, а клиенту не нужно ничего склеивать.</summary>
public sealed record MechBattleView(
    string Mission, string[] Map, int Round, string Turn, string? Current, bool Moved, int Steps, int MoveRange,
    MechUnitView[] Units, string? Winner);

/// <summary>
/// Наземный бой одной миссии (M21), пошагово (баланс мехов §11–14): в раунде стороны активируют мехов по
/// очереди, начиная с игрока; у кого кончились неактивированные — другая сторона добирает подряд.
/// Активация: одно перемещение (можно пропустить), затем атака — она завершает ход — или «конец хода»
/// с бесплатным поворотом. Атака разворачивает стрелка к цели.
///
/// Чистые правила: ни сети, ни времени. Броски — только из <see cref="MechRandom"/> от сида.
/// </summary>
public sealed class MechBattle
{
    public const string PlayerSide = "player";
    public const string EnemySide = "enemy";

    private readonly MechRandom _random;

    public MechBattle(MechRules rules, string missionId, uint seed)
    {
        Rules = rules;
        MissionId = missionId;
        Mission = rules.Mission(missionId) ?? throw new ArgumentException($"unknown mech mission '{missionId}'", nameof(missionId));
        Field = new MechField(Mission.Map);
        _random = new MechRandom(seed);
        var n = 0;
        foreach (var s in Mission.Player) Units.Add(Create(s, PlayerSide, $"p{++n}"));
        n = 0;
        foreach (var s in Mission.Enemy) Units.Add(Create(s, EnemySide, $"e{++n}"));
        StartRound([]);
    }

    public MechRules Rules { get; }
    public string MissionId { get; }
    public MechMission Mission { get; }
    public MechField Field { get; }
    public List<MechUnit> Units { get; } = [];
    public int Round { get; private set; }
    public string Turn { get; private set; } = PlayerSide;
    public MechUnit? Current { get; private set; }
    public bool Moved { get; private set; }
    public int Steps { get; private set; }
    public int MoveRange { get; private set; }

    /// <summary>Кто победил; null — бой идёт.</summary>
    public string? Winner { get; private set; }

    public bool Over => Winner is not null;

    public MechUnit? Unit(string? id) => Units.FirstOrDefault(u => u.Id == id);

    public IEnumerable<MechUnit> Living(string side) => Units.Where(u => u.Side == side && u.Alive);

    /// <summary>Клетки, занятые живыми мехами, кроме указанного.</summary>
    public HashSet<int> Occupied(MechUnit? except = null) =>
        Units.Where(u => u.Alive && u != except).Select(u => Field.Index(u.X, u.Y)).ToHashSet();

    /// <summary>Рука, из которой стреляет мех: первое живое оружие, правая раньше левой. null — стрелять нечем.</summary>
    public (MechPart Arm, MechWeapon Weapon)? Gun(MechUnit u)
    {
        foreach (var (arm, id) in new[] { (MechPart.Right, u.Def.Right), (MechPart.Left, u.Def.Left) })
            if (Rules.Weapon(id) is { } w && u.PartAlive(arm)) return (arm, w);
        return null;
    }

    /// <summary>Щит меха и в какой руке он стоит; null — щита нет.</summary>
    public (MechPart Arm, MechShield Shield)? ShieldOf(MechUnit u)
    {
        if (Rules.Shield(u.Def.Left) is { } l) return (MechPart.Left, l);
        if (Rules.Shield(u.Def.Right) is { } r) return (MechPart.Right, r);
        return null;
    }

    /// <summary>Броня каждой части цели — для вилки урона и для самого удара.</summary>
    public int Armor(MechUnit u, MechPart part) => part switch
    {
        MechPart.Body => Rules.BodyMap[u.Def.Body].Armor,
        MechPart.Chassis => Rules.ChassisMap[u.Def.Chassis].Armor,
        MechPart.Left => Rules.Arm(u.Def.Left).Armor,
        _ => Rules.Arm(u.Def.Right).Armor,
    };

    public int BaseMove(MechUnit u) => Rules.ChassisMap[u.Def.Chassis].Move;

    public int MoveRangeOf(MechUnit u) =>
        MechCombat.MoveRange(BaseMove(u), u.Hp[(int)MechPart.Chassis], u.Max[(int)MechPart.Chassis]);

    /// <summary>Куда может дойти текущий мех; после перемещения — никуда.</summary>
    public Dictionary<int, int> Reach()
    {
        if (Current is not { } u || Moved) return [];
        return Field.Reach(u.X, u.Y, MoveRange, Occupied(u));
    }

    /// <summary>
    /// Шанс попасть текущим мехом по цели, если бы он стрелял из (x, y), пройдя steps клеток. null — отсюда
    /// не выстрелить: нечем, далеко, близко или стена.
    /// </summary>
    public int? Chance(MechUnit shooter, int x, int y, int steps, int moveRange, MechUnit target, bool aimed)
    {
        if (Gun(shooter) is not { } gun) return null;
        var d = MechField.Distance(x, y, target.X, target.Y);
        if (d < gun.Weapon.MinRange || d > gun.Weapon.MaxRange) return null;
        if (!Field.LineOfFire(x, y, target.X, target.Y)) return null;
        var side = MechCombat.Side(x, y, target.X, target.Y, target.Dir);
        return MechCombat.HitChance(Rules.CombatDef, gun.Weapon, d, steps, moveRange, side, aimed);
    }

    /// <summary>Применить команду текущего меха. null — принято, иначе код отказа (<see cref="MechCodes"/>).</summary>
    public string? Apply(MechCommand command, List<MechEvent> events)
    {
        if (Over) return MechCodes.Over;
        if (Current is not { } u) return MechCodes.NotYourTurn;
        switch (command.Act)
        {
            case MechCommand.MoveAct:
                return Move(u, command.X, command.Y, events);
            case MechCommand.AttackAct:
                return Attack(u, command.Target, command.Part, events);
            case MechCommand.EndAct:
                if (command.Dir is { } dir)
                {
                    if (dir is < 0 or > 7) return MechCodes.BadAct;
                    if (dir != u.Dir)
                    {
                        u.Dir = dir;
                        events.Add(new MechEvent(MechEvent.Face, u.Id, Dir: dir));
                    }
                }
                Finish(u, events);
                return null;
            default:
                return MechCodes.BadAct;
        }
    }

    private string? Move(MechUnit u, int x, int y, List<MechEvent> events)
    {
        if (Moved) return MechCodes.AlreadyMoved;
        if (!Field.Inside(x, y)) return MechCodes.Unreachable;
        var path = Field.Path(u.X, u.Y, x, y, MoveRange, Occupied(u));
        if (path is null) return MechCodes.Unreachable;
        var last = path.Count > 1 ? path[^2] : Field.Index(u.X, u.Y);
        u.Dir = MechField.Direction(x - last % Field.Width, y - last / Field.Width);
        u.X = x;
        u.Y = y;
        Moved = true;
        Steps = path.Count;
        events.Add(new MechEvent(MechEvent.Move, u.Id, Path: [.. path], Dir: u.Dir));
        return null;
    }

    private string? Attack(MechUnit u, string? targetId, string? partName, List<MechEvent> events)
    {
        var target = Unit(targetId);
        if (target is null || !target.Alive || target.Side == u.Side) return MechCodes.NoTarget;
        if (Gun(u) is not { } gun) return MechCodes.ArmDown;
        MechPart? aimed = null;
        if (partName is not null)
        {
            aimed = MechCombat.ParsePart(partName);
            // Целиться в разбитую или пустую руку нечем: попадать там не во что.
            if (aimed is not { } p || !target.PartAlive(p)) return MechCodes.BadPart;
        }
        var d = MechField.Distance(u.X, u.Y, target.X, target.Y);
        if (d < gun.Weapon.MinRange || d > gun.Weapon.MaxRange) return MechCodes.OutOfRange;
        if (!Field.LineOfFire(u.X, u.Y, target.X, target.Y)) return MechCodes.NoLine;

        var c = Rules.CombatDef;
        var side = MechCombat.Side(u.X, u.Y, target.X, target.Y, target.Dir);
        var chance = MechCombat.HitChance(c, gun.Weapon, d, Steps, MoveRange, side, aimed is not null);
        u.Dir = MechField.Direction(target.X - u.X, target.Y - u.Y);
        var weaponId = gun.Arm == MechPart.Right ? u.Def.Right : u.Def.Left;
        events.Add(new MechEvent(MechEvent.Shot, u.Id, target.Id, Chance: chance, Dir: u.Dir, Weapon: weaponId));

        // Порядок бросков (§77): попадание → часть → блок → урон. Тот же порядок — тот же бой по сиду.
        if (_random.Next(100) >= chance)
        {
            events.Add(new MechEvent(MechEvent.Miss, u.Id, target.Id));
            Finish(u, events);
            return null;
        }
        var part = aimed ?? Pick(MechCombat.PartWeights(c, side, target.Hp));
        var raw = gun.Weapon.Damage * (c.RollMin + (c.RollMax - c.RollMin) * _random.NextDouble());
        if (ShieldOf(target) is { } shield && part != shield.Arm)
        {
            var block = MechCombat.BlockChance(c, shield.Shield, shield.Arm, target.PartAlive(shield.Arm), side);
            if (block > 0 && _random.Next(100) < block)
            {
                var soaked = MechCombat.Damage(raw, shield.Shield.Armor);
                var left = target.Hp[(int)shield.Arm];
                Hurt(target, shield.Arm, Math.Min(soaked, left), events, MechEvent.Block);
                // Щит не выдержал (§43): остаток идёт туда, куда летел выстрел.
                if (soaked > left) Hurt(target, part, soaked - left, events, MechEvent.Hit);
                Finish(u, events);
                return null;
            }
        }
        Hurt(target, part, MechCombat.Damage(raw, Armor(target, part)), events, MechEvent.Hit);
        Finish(u, events);
        return null;
    }

    private MechPart Pick(int[] weights)
    {
        var roll = _random.Next(100);
        for (var i = 0; i < 4; i++)
        {
            if (roll < weights[i]) return (MechPart)i;
            roll -= weights[i];
        }
        return MechPart.Body;
    }

    private static void Hurt(MechUnit target, MechPart part, int damage, List<MechEvent> events, string kind)
    {
        var i = (int)part;
        var was = target.Hp[i];
        target.Hp[i] = Math.Max(0, was - damage);
        events.Add(new MechEvent(kind, target.Id, Part: MechCombat.Name(part), Dmg: damage));
        if (was <= 0 || target.Hp[i] > 0) return;
        events.Add(part == MechPart.Body
            ? new MechEvent(MechEvent.MechDown, target.Id)
            : new MechEvent(MechEvent.PartDown, target.Id, Part: MechCombat.Name(part)));
    }

    private void Finish(MechUnit u, List<MechEvent> events)
    {
        u.Activated = true;
        Current = null;
        if (!Living(EnemySide).Any()) Winner = PlayerSide;
        else if (!Living(PlayerSide).Any()) Winner = EnemySide;
        if (Over) return;
        var other = u.Side == PlayerSide ? EnemySide : PlayerSide;
        if (Ready(other)) Begin(other);
        else if (Ready(u.Side)) Begin(u.Side);
        else StartRound(events);
    }

    private bool Ready(string side) => Living(side).Any(m => !m.Activated);

    private void StartRound(List<MechEvent> events)
    {
        Round++;
        foreach (var m in Units) m.Activated = false;
        if (Round > 1) events.Add(new MechEvent(MechEvent.Round, "", N: Round));
        Begin(Ready(PlayerSide) ? PlayerSide : EnemySide);
    }

    private void Begin(string side)
    {
        Turn = side;
        Current = Living(side).First(m => !m.Activated);
        Moved = false;
        Steps = 0;
        MoveRange = MoveRangeOf(Current);
    }

    /// <summary>Ходы ИИ, пока очередь у противника и бой не кончен.</summary>
    public void RunEnemies(List<MechEvent> events)
    {
        // Предохранитель: одна активация — не больше двух команд, а мехов на поле единицы.
        for (var guard = 0; guard < 64 && !Over && Turn == EnemySide; guard++)
        {
            foreach (var command in MechAi.Decide(this))
            {
                if (Apply(command, events) is not null) Apply(new MechCommand(MechCommand.EndAct), events);
                if (Current is null || Turn != EnemySide) break;
            }
        }
    }

    /// <summary>Сдача: все мехи игрока выведены из боя, победа за противником.</summary>
    public void Surrender() => Winner ??= EnemySide;

    public MechBattleView View() => new(
        MissionId,
        Mission.Map,
        Round,
        Turn,
        Current?.Id,
        Moved,
        Steps,
        MoveRange,
        [.. Units.Select(u => new MechUnitView(u.Id, u.Side, u.Unit, u.Def.Name, u.X, u.Y, u.Dir, (int[])u.Hp.Clone(), u.Max, u.Activated))],
        Winner);

    private MechUnit Create(MechSpawn s, string side, string id)
    {
        var def = Rules.UnitMap[s.Unit];
        int[] max =
        [
            Rules.BodyMap[def.Body].Hp,
            Rules.Arm(def.Left).Hp,
            Rules.Arm(def.Right).Hp,
            Rules.ChassisMap[def.Chassis].Hp,
        ];
        return new MechUnit
        {
            Id = id, Side = side, Unit = s.Unit, Def = def, X = s.X, Y = s.Y, Dir = s.Dir,
            Hp = (int[])max.Clone(), Max = max,
        };
    }
}
