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
    public string Control { get; set; } = "player";
    public string? Owner { get; set; }
    public string? AccountOwner { get; set; }
    public string[] Skills { get; set; } = [];
    public Dictionary<string, int> Cooldowns { get; } = [];
    public bool Repaired { get; set; }
    public bool Stance { get; set; }
    public int AccuracyPenalty { get; set; }
    public int Xp { get; set; }

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
public sealed record MechCommand(string Act, int X = 0, int Y = 0, int? Dir = null, string? Target = null, string? Part = null,
    string? Unit = null, string? Hand = null)
{
    public const string MoveAct = "move";
    public const string AttackAct = "attack";
    public const string EndAct = "end";
}

/// <summary>Мех в сообщении клиенту.</summary>
public sealed record MechUnitView(
    string Id, string Side, string Unit, string Name, int X, int Y, int Dir, int[] Hp, int[] Max, bool Activated,
    string Control = "player", string? Owner = null, MechUnitDef? Assembly = null, string[]? Skills = null,
    int Level = 1, bool Repaired = false, bool Stance = false, int AccuracyPenalty = 0, int DashRange = 0, int JumpRange = 0,
    string? AccountOwner = null);

/// <summary>Всё поле в одном сообщении: оно маленькое, а клиенту не нужно ничего склеивать.</summary>
public sealed record MechBattleView(
    string Mission, string[] Map, int Round, string Turn, string? Current, bool Moved, int Steps, int MoveRange,
    MechUnitView[] Units, string? Winner, string? Goal = null, int Progress = 0, int Required = 0,
    string? ObjectiveUnit = null, string? Reason = null, string[]? Actions = null, string[]? Ready = null,
    string? Biome = null, bool Interacted = false, MechPoint[]? Evacuation = null, MechPoint[]? Route = null);

public sealed record MechUnitSave(MechUnitView View, int Xp, Dictionary<string,int> Cooldowns);
public sealed record MechBattleSave(MechRules Rules, string Mission, uint Random, int Round, string Turn, string? Current,
    bool Moved, int Steps, int MoveRange, bool ActivationStarted, string? Winner, string? Reason, int Progress,
    bool Interacted, int EnemyNumber, MechSpawn[] PendingWaves, MechUnitSave[] Units, string[] ActivationOrder, bool CaptureHeld = false);

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
    private int _enemyNumber;
    private readonly List<MechSpawn> _pendingWaves = [];
    public int Progress { get; private set; }
    public string? Reason { get; private set; }
    public MechUnit? ObjectiveUnit { get; private set; }
    public bool Interacted { get; private set; }
    private bool _captureHeld;
    public string[] ActivationOrder { get; set; } = [];

    public MechBattleSave Save() => new(Rules, MissionId, _random.State, Round, Turn, Current?.Id, Moved, Steps, MoveRange,
        _activationStarted, Winner, Reason, Progress, Interacted, _enemyNumber, _pendingWaves.ToArray(),
        View().Units.Select(v => new MechUnitSave(v, Unit(v.Id)!.Xp, new(Unit(v.Id)!.Cooldowns))).ToArray(), ActivationOrder, _captureHeld);

    public static MechBattle Restore(MechBattleSave s)
    {
        var b = new MechBattle(s.Rules, s.Mission, 1);
        b.Units.Clear();
        foreach (var saved in s.Units)
        {
            var v = saved.View;
            var u = new MechUnit { Id=v.Id, Side=v.Side, Unit=v.Unit, Def=v.Assembly!, X=v.X,Y=v.Y,Dir=v.Dir,
                Hp=v.Hp.ToArray(), Max=v.Max.ToArray(),Activated=v.Activated,Control=v.Control,Owner=v.Owner,AccountOwner=v.AccountOwner,
                Skills=v.Skills ?? [],Repaired=v.Repaired,Stance=v.Stance,AccuracyPenalty=v.AccuracyPenalty,Xp=saved.Xp };
            foreach (var p in saved.Cooldowns) u.Cooldowns[p.Key]=p.Value;
            b.Units.Add(u);
        }
        b._random.State=s.Random;b.Round=s.Round;b.Turn=s.Turn;b.Current=b.Unit(s.Current);b.Moved=s.Moved;b.Steps=s.Steps;
        b.MoveRange=s.MoveRange;b._activationStarted=s.ActivationStarted;b.Winner=s.Winner;b.Reason=s.Reason;
        b.Progress=s.Progress;b.Interacted=s.Interacted;b._enemyNumber=s.EnemyNumber;b.ActivationOrder=s.ActivationOrder;b._captureHeld=s.CaptureHeld;
        b._pendingWaves.Clear();b._pendingWaves.AddRange(s.PendingWaves);b.ObjectiveUnit=b.Unit("objective");return b;
    }

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
        _enemyNumber = n;
        n = 0;
        foreach (var s in Mission.Allies ?? [])
        {
            var ally = Create(s, PlayerSide, $"a{++n}"); ally.Control = "ai"; Units.Add(ally);
        }
        if (Mission.Objective?.Object is { } obj)
        {
            ObjectiveUnit = Create(obj, Mission.Objective.Kind == "sabotage" ? EnemySide : PlayerSide, "objective"); ObjectiveUnit.Control = "objective";
            Units.Add(ObjectiveUnit);
        }
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
    private bool _activationStarted;

    /// <summary>Кто победил; null — бой идёт.</summary>
    public string? Winner { get; private set; }

    public bool Over => Winner is not null;

    public MechUnit? Unit(string? id) => Units.FirstOrDefault(u => u.Id == id);

    public IEnumerable<MechUnit> Living(string side) => Units.Where(u => u.Side == side && u.Alive);

    /// <summary>Клетки, занятые живыми мехами, кроме указанного.</summary>
    public HashSet<int> Occupied(MechUnit? except = null) =>
        Units.Where(u => u.Alive && u != except).Select(u => Field.Index(u.X, u.Y)).ToHashSet();

    /// <summary>Рука, из которой стреляет мех: первое живое оружие, правая раньше левой. null — стрелять нечем.</summary>
    public (MechPart Arm, MechWeapon Weapon)? Gun(MechUnit u, string? hand = null)
    {
        foreach (var (arm, id) in new[] { (MechPart.Right, u.Def.Right), (MechPart.Left, u.Def.Left) })
        {
            if (hand is not null && MechCombat.Name(arm) != hand) continue;
            if (Rules.Weapon(id) is { } w && u.PartAlive(arm)) return (arm, w);
        }
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
    public double Pack(MechUnit u, string effect) => u.Def.Backpack is { } id && Rules.BackpackMap.TryGetValue(id, out var p) && p.Effect == effect ? p.Value : 0;

    public int Armor(MechUnit u, MechPart part) => (part switch
    {
        MechPart.Body => Rules.BodyMap[u.Def.Body].Armor,
        MechPart.Chassis => Rules.ChassisMap[u.Def.Chassis].Armor,
        MechPart.Left => Rules.Arm(u.Def.Left).Armor,
        _ => Rules.Arm(u.Def.Right).Armor,
    }) + (int)Pack(u, "armor") + (u.Stance ? Rules.SkillMap.GetValueOrDefault("defensiveStance")?.Bonus ?? 0 : 0);

    public int BaseMove(MechUnit u) => Rules.ChassisMap[u.Def.Chassis].Move + (int)Pack(u, "mobility");

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
    public int? Chance(MechUnit shooter, int x, int y, int steps, int moveRange, MechUnit target, bool aimed, string? hand = null)
    {
        if (Gun(shooter, hand) is not { } gun) return null;
        var d = MechField.Distance(x, y, target.X, target.Y);
        if (d < gun.Weapon.MinRange || d > gun.Weapon.MaxRange) return null;
        if (!Field.LineOfFire(x, y, target.X, target.Y)) return null;
        var side = MechCombat.Side(x, y, target.X, target.Y, target.Dir);
        if (target == ObjectiveUnit && Mission.Objective?.FiringCells is { Length: > 0 } cells && !cells.Any(p => p.X == x && p.Y == y)) return null;
        return MechCombat.HitChance(Rules.CombatDef, EffectiveWeapon(shooter, gun.Weapon), d, steps, moveRange, side, aimed);
    }

    /// <summary>Применить команду текущего меха. null — принято, иначе код отказа (<see cref="MechCodes"/>).</summary>
    public string? Apply(MechCommand command, List<MechEvent> events)
    {
        if (Over) return MechCodes.Over;
        if (Current is not { } u) return MechCodes.NotYourTurn;
        if (command.Act == "select")
        {
            var selected = Unit(command.Unit);
            if (Turn != PlayerSide || _activationStarted || selected is null || !selected.Alive || selected.Activated || selected.Side != PlayerSide || selected.Control != "player"
                || ActivationOrder.Length > 0 && selected.AccountOwner != u.AccountOwner) return MechCodes.NotYourTurn;
            Current = selected; Moved = false; Steps = 0; MoveRange = MoveRangeOf(selected);
            return null;
        }
        if (command.Unit is not null && command.Unit != u.Id) return MechCodes.NotYourTurn;
        switch (command.Act)
        {
            case "interact":
                if (Mission.Objective?.Kind is not ("sabotage" or "rescue") || Interacted || ObjectiveUnit is null
                    || MechField.Distance(u.X,u.Y,ObjectiveUnit.X,ObjectiveUnit.Y) != 1) return "badInteraction";
                StartActivation(u);Interacted=true;
                if (Mission.Objective.Kind == "sabotage") ObjectiveUnit.Hp[0]=0;
                events.Add(new("interact",u.Id,Target:ObjectiveUnit.Id));Finish(u,events);return null;
            case MechCommand.MoveAct:
                return Move(u, command.X, command.Y, events);
            case MechCommand.AttackAct:
                return Attack(u, command.Target, command.Part, events, command.Hand);
            case "repair":
                var repairPart = MechCombat.ParsePart(command.Part);
                if (repairPart is not { } rp || Pack(u, "repair") <= 0 || u.Repaired || !u.PartAlive(rp) || u.Hp[(int)rp] >= u.Max[(int)rp]) return "badRepair";
                var healed = Math.Min(u.Max[(int)rp] - u.Hp[(int)rp], MechCombat.Round(u.Max[(int)rp] * Pack(u, "repair")));
                StartActivation(u);
                u.Hp[(int)rp] += healed; u.Repaired = true;
                events.Add(new("repair", u.Id, Part: command.Part, Dmg: healed));
                Finish(u, events); return null;
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
                StartActivation(u);
                if (Trigger(u, "defensiveStance", events)) u.Stance = true;
                Finish(u, events);
                return null;
            default:
                return MechCodes.BadAct;
        }
    }

    private string? Move(MechUnit u, int x, int y, List<MechEvent> events)
    {
        if (Moved) return MechCodes.AlreadyMoved;
        if (!Field.Passable(x, y) || Occupied(u).Contains(Field.Index(x, y)) || (u.X == x && u.Y == y) || MoveRange == 0) return MechCodes.Unreachable;
        var path = Field.Path(u.X, u.Y, x, y, MoveRange, Occupied(u));
        var dashBonus = Rules.SkillMap.GetValueOrDefault("dash")?.Bonus ?? 0;
        var longer = HasSkill(u, "dash") ? Field.Path(u.X, u.Y, x, y, MoveRange + dashBonus, Occupied(u)) : null;
        var canJump = HasSkill(u, "jump") && MechField.Distance(u.X, u.Y, x, y) <= (Rules.SkillMap.GetValueOrDefault("jump")?.Bonus ?? 0);
        if (path is null && longer is null && !canJump) return MechCodes.Unreachable;
        StartActivation(u);
        if (path is null && longer is null)
        {
            Moved = true;
            if (!Trigger(u, "jump", events)) { events.Add(new("skillFailed", u.Id, Weapon: "jump")); return null; }
            path = [Field.Index(x, y)];
        }
        else
        {
            var dashed = Trigger(u, "dash", events);
            if (dashed) { path = longer ?? path; u.AccuracyPenalty = Rules.SkillMap["dash"].AccuracyPenalty; }
            if (path is null) { Moved = true; events.Add(new("skillFailed", u.Id, Weapon: "dash")); return null; }
        }
        var last = path.Count > 1 ? path[^2] : Field.Index(u.X, u.Y);
        u.Dir = MechField.Direction(x - last % Field.Width, y - last / Field.Width);
        u.X = x;
        u.Y = y;
        CheckCaptureHold();
        Moved = true;
        Steps = path.Count;
        events.Add(new MechEvent(MechEvent.Move, u.Id, Path: [.. path], Dir: u.Dir));
        return null;
    }

    private int AccuracyPenaltyOf(MechUnit u) => u == Current && !_activationStarted ? 0 : u.AccuracyPenalty;

    private MechWeapon EffectiveWeapon(MechUnit u, MechWeapon w) => w with
    {
        Accuracy = w.Accuracy + (int)Pack(u, "accuracy") - AccuracyPenaltyOf(u),
        Damage = MechCombat.Round(w.Damage * (Pack(u, "damage") is var boost && boost > 0 ? boost : 1)),
    };

    public bool HasSkill(MechUnit u, string id) => u.Skills.Contains(id) && MechDevelopment.Compatible(Rules, u.Def, id)
        && u.Cooldowns.GetValueOrDefault(id) <= Round;

    private bool Trigger(MechUnit u, string id, List<MechEvent> events)
    {
        if (!HasSkill(u, id) || !Rules.SkillMap.TryGetValue(id, out var skill)) return false;
        if (_random.Next(100) >= Math.Min(id == "evade" ? 25 : 100, skill.Chance)) return false;
        u.Cooldowns[id] = Round + skill.Cooldown;
        events.Add(new("skill", u.Id, Weapon: id)); return true;
    }

    private string? Attack(MechUnit u, string? targetId, string? partName, List<MechEvent> events, string? hand = null,
        bool reaction = false)
    {
        var target = Unit(targetId);
        if (target is null || !target.Alive || target.Side == u.Side) return MechCodes.NoTarget;
        if (hand is not null && hand is not ("left" or "right")) return MechCodes.BadAct;
        if (Gun(u, hand) is not { } gun) return MechCodes.ArmDown;
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
        if (target == ObjectiveUnit && Mission.Objective?.FiringCells is { Length: > 0 } cells && !cells.Any(p => p.X == u.X && p.Y == u.Y)) return MechCodes.OutOfRange;
        if (!reaction) StartActivation(u);

        var c = Rules.CombatDef;
        var w = EffectiveWeapon(u, gun.Weapon);
        if (aimed is not null && Trigger(u, "targetingExpert", events)) c = c with { AimedPenalty = Math.Max(0, c.AimedPenalty - Rules.SkillMap["targetingExpert"].Bonus) };
        var side = MechCombat.Side(u.X, u.Y, target.X, target.Y, target.Dir);
        var chance = MechCombat.HitChance(c, w, d, reaction ? 0 : Steps, reaction ? MoveRangeOf(u) : MoveRange, side, aimed is not null);
        u.Dir = MechField.Direction(target.X - u.X, target.Y - u.Y);
        var weaponId = gun.Arm == MechPart.Right ? u.Def.Right : u.Def.Left;
        events.Add(new MechEvent(MechEvent.Shot, u.Id, target.Id, Chance: chance, Dir: u.Dir, Weapon: weaponId));

        // Порядок бросков (§77): попадание → часть → блок → урон. Тот же порядок — тот же бой по сиду.
        if (_random.Next(100) >= chance)
        {
            events.Add(new MechEvent(MechEvent.Miss, u.Id, target.Id));
            CompleteAttack(); return null;
        }
        if (Trigger(target, "evade", events)) { events.Add(new(MechEvent.Miss, u.Id, target.Id)); CompleteAttack(); return null; }
        var part = aimed ?? Pick(MechCombat.PartWeights(c, side, target.Hp));
        var raw = w.Damage * (c.RollMin + (c.RollMax - c.RollMin) * _random.NextDouble());
        if (ShieldOf(target) is { } shield && part != shield.Arm)
        {
            var block = MechCombat.BlockChance(c, shield.Shield, shield.Arm, target.PartAlive(shield.Arm), side);
            if (block > 0)
            {
                if (target.Stance) block += Rules.SkillMap.GetValueOrDefault("defensiveStance")?.BlockBonus ?? 0;
                if (Trigger(target, "shieldMastery", events)) block += Rules.SkillMap["shieldMastery"].Bonus;
                block = Math.Min(c.BlockMax, block);
            }
            if (block > 0 && _random.Next(100) < block)
            {
                var soaked = MechCombat.Damage(raw, Armor(target, shield.Arm));
                var left = target.Hp[(int)shield.Arm];
                Hurt(target, shield.Arm, Math.Min(soaked, left), events, MechEvent.Block);
                // Щит не выдержал (§43): остаток идёт туда, куда летел выстрел.
                if (soaked > left) Hurt(target, part, soaked - left, events, MechEvent.Hit);
                CompleteAttack();
                return null;
            }
        }
        Hurt(target, part, MechCombat.Damage(raw, Armor(target, part)), events, MechEvent.Hit);
        CompleteAttack();
        return null;

        void CompleteAttack()
        {
            if (!target.Alive) u.Xp += MechDevelopment.KillXp(Rules, u.Def.Level, target.Def.Level);
            if (reaction) return;
            var otherHand = gun.Arm == MechPart.Right ? "left" : "right";
            if (u.Alive && target.Alive && Gun(u, otherHand) is not null && Chance(u, u.X, u.Y, Steps, MoveRange, target, false, otherHand) is not null
                && Trigger(u, "doubleAttack", events)) Attack(u, targetId, null, events, otherHand, reaction: true);
            if (u.Alive && target.Alive && Chance(target, target.X, target.Y, 0, MoveRangeOf(target), u, false) is not null
                && Trigger(target, "counterAttack", events)) Attack(target, u.Id, null, events, reaction: true);
            Finish(u, events);
        }
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
        CheckLoss();
        if (!Over && (Mission.Objective?.Kind ?? "eliminate") == "eliminate" && !Living(EnemySide).Any()) Winner = PlayerSide;
        if (Over) return;
        var other = u.Side == PlayerSide ? EnemySide : PlayerSide;
        if (Ready(other)) Begin(other);
        else if (Ready(u.Side)) Begin(u.Side);
        else
        {
            EndRound(events);
            if (!Over) StartRound(events);
        }
    }

    private bool Ready(string side) => Living(side).Any(m => !m.Activated && m.Control != "objective");

    private void CheckLoss()
    {
        CheckCaptureHold();
        if (!Living(PlayerSide).Any(u => u.Control == "player")) { Winner = EnemySide; Reason = "squadDestroyed"; }
        else if (ObjectiveUnit is { Alive: false } && Mission.Objective?.Kind is "escort" or "defend") { Winner = EnemySide; Reason = Mission.Objective?.Kind == "escort" ? "tractorDestroyed" : "objectDestroyed"; }
    }

    private void CheckCaptureHold()
    {
        if (!_captureHeld || Mission.Objective is not { Kind: "capture", FullRounds: true } o) return;
        bool Inside(MechUnit u) => o.Zone!.Any(p=>p.X==u.X&&p.Y==u.Y);
        _captureHeld=Living(PlayerSide).Any(u=>u.Control!="objective"&&Inside(u))&&!Living(EnemySide).Any(Inside);
    }

    private void EndRound(List<MechEvent> events)
    {
        var o = Mission.Objective;
        if (o?.Kind == "escort" && ObjectiveUnit is { Alive: true } tractor && o.Route is { } route && Progress < route.Length)
        {
            var p = route[Progress];
            if (Field.Passable(p.X, p.Y) && !Occupied(tractor).Contains(Field.Index(p.X, p.Y)))
            {
                tractor.Dir = MechField.Direction(p.X - tractor.X, p.Y - tractor.Y); tractor.X = p.X; tractor.Y = p.Y; Progress++;
                events.Add(new(MechEvent.Move, tractor.Id, Path: [Field.Index(p.X, p.Y)], Dir: tractor.Dir));
            }
        }
        CheckLoss(); if (Over) return;
        switch (o?.Kind)
        {
            case "sabotage":
            case "rescue":
                if ((o.Kind == "sabotage" ? ObjectiveUnit is { Alive:false } : Interacted)
                    && Living(PlayerSide).Any(u => u.Control == "player" && o.Evacuation!.Any(p => p.X==u.X && p.Y==u.Y)))
                { Winner=PlayerSide;Reason="evacuated";Progress=1; }
                break;
            case "defend": Progress = Round; if (Round >= o.Rounds) Winner = PlayerSide; break;
            case "escort": if (Progress == o.Route!.Length) Winner = PlayerSide; break;
            case "capture":
                bool Inside(MechUnit u) => o.Zone!.Any(p => p.X == u.X && p.Y == u.Y);
                var held=Living(PlayerSide).Any(u => u.Control != "objective" && Inside(u)) && !Living(EnemySide).Any(Inside);
                Progress = held && (!o.FullRounds || _captureHeld) ? Progress + 1 : 0;
                if (Progress >= o.Rounds) Winner = PlayerSide;
                break;
        }
    }

    private void StartRound(List<MechEvent> events)
    {
        Round++;
        foreach (var wave in Mission.Waves ?? []) if (wave.Round == Round) _pendingWaves.AddRange(wave.Enemies);
        foreach (var spawn in _pendingWaves.ToArray())
        {
            var occupied = Occupied();
            var cells = Enumerable.Range(0, Field.Width * Field.Height).Where(i => Field.Passable(i % Field.Width, i / Field.Width) && !occupied.Contains(i))
                .OrderBy(i => MechField.Distance(spawn.X, spawn.Y, i % Field.Width, i / Field.Width)).ThenBy(i => i).ToArray();
            if (cells.Length == 0) continue;
            var cell = cells[0];
            Units.Add(Create(spawn with { X = cell % Field.Width, Y = cell / Field.Width }, EnemySide, $"e{++_enemyNumber}"));
            _pendingWaves.Remove(spawn);
        }
        foreach (var m in Units) m.Activated = false;
        var objective = Mission.Objective;
        _captureHeld=objective?.Kind=="capture" && Living(PlayerSide).Any(u=>u.Control!="objective" && objective.Zone!.Any(p=>p.X==u.X&&p.Y==u.Y))
            && !Living(EnemySide).Any(u=>objective.Zone!.Any(p=>p.X==u.X&&p.Y==u.Y));
        if (Round > 1) events.Add(new MechEvent(MechEvent.Round, "", N: Round));
        Begin(Ready(PlayerSide) ? PlayerSide : EnemySide);
    }

    private void Begin(string side)
    {
        Turn = side;
        var used=Units.Count(u=>u.Side==PlayerSide && u.Control=="player" && u.Activated);
        var desired=ActivationOrder.Length>used ? Unit(ActivationOrder[used])?.AccountOwner : null;
        Current = Living(side).Where(m => !m.Activated && m.Control != "objective")
            .OrderBy(m => m.Control == "player" ? 0 : 1)
            .ThenBy(m => ActivationOrder.Length>0 && side==PlayerSide && m.AccountOwner!=desired ? 1:0)
            .ThenBy(m => ActivationOrder.Length > 0 && side == PlayerSide ? Array.IndexOf(ActivationOrder,m.Id) : 0).First();
        _activationStarted = false;
        Moved = false;
        Steps = 0;
        MoveRange = MoveRangeOf(Current);
    }

    private void StartActivation(MechUnit u)
    {
        if (_activationStarted) return;
        _activationStarted = true; u.Stance = false; u.AccuracyPenalty = 0;
    }

    /// <summary>Ходы ИИ, пока очередь у противника и бой не кончен.</summary>
    public void RunEnemies(List<MechEvent> events)
    {
        // Предохранитель: одна активация — не больше двух команд, а мехов на поле единицы.
        for (var guard = 0; guard < 64 && !Over && Current?.Control == "ai"; guard++)
        {
            foreach (var command in MechAi.Decide(this))
            {
                if (Apply(command, events) is not null) Apply(new MechCommand(MechCommand.EndAct), events);
                if (Current is null || Current.Control != "ai") break;
            }
        }
    }

    /// <summary>Сдача: все мехи игрока выведены из боя, победа за противником.</summary>
    public void Surrender() { Winner ??= EnemySide; Reason ??= "retreat"; }

    public MechBattleView View() => new(
        MissionId,
        Mission.Map,
        Round,
        Turn,
        Current?.Id,
        Moved,
        Steps,
        MoveRange,
        [.. Units.Select(u => new MechUnitView(u.Id, u.Side, u.Unit, u.Def.Name, u.X, u.Y, u.Dir, (int[])u.Hp.Clone(), (int[])u.Max.Clone(), u.Activated,
            u.Control, u.Owner, u.Def, u.Skills, u.Def.Level, u.Repaired, u.Stance, AccuracyPenaltyOf(u),
            HasSkill(u,"dash") ? Rules.SkillMap["dash"].Bonus : 0, HasSkill(u,"jump") ? Rules.SkillMap["jump"].Bonus : 0,u.AccountOwner))],
        Winner, Mission.Goal, Progress, Mission.Objective?.Kind == "escort" ? Mission.Objective.Route!.Length : Mission.Objective?.Rounds ?? Mission.Enemy.Length,
        ObjectiveUnit?.Id, Reason, ["move", "attack", "end", .. Mission.Objective?.Kind is "sabotage" or "rescue" ? new[] {"interact"} : [],
            .. Current is { } current && Pack(current, "repair") > 0 && !current.Repaired ? new[] { "repair" } : []],
        [.. Living(PlayerSide).Where(u => u.Control == "player" && !u.Activated && (ActivationOrder.Length==0 || u.AccountOwner==Current?.AccountOwner)).Select(u => u.Id)],
        Mission.Biome,Interacted,Mission.Objective?.Evacuation,Mission.Objective?.Route);

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
            Control = side == EnemySide ? "ai" : "player",
        };
    }
}
