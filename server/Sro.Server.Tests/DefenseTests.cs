using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Защита (M15.6): динамическая защита и аэрозольная завеса отбивают попадание по виду урона,
/// противоракетный комплекс сбивает ракеты без оружейного слота, маневровые дюзы роняют шанс попасть.
/// </summary>
public sealed class DefenseTests
{
    /// <summary>Бросок: 0 — всё удаётся, 0.999 — всё мимо. Один и тот же бросок идёт и на попадание, и на блок.</summary>
    private const double Always = 0;
    private const double Never = 0.999;

    private static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams>
    {
        // Уклонения нет: шанс попадания задаёт только точность пушки, и блок проверяется на чистом опыте.
        ["light"] = TestBalance.Hulls["light"] with
        {
            Class = EquipClass.S, WeaponSlots = [EquipClass.S, EquipClass.S], UtilitySlots = 3, Evasion = 0, MoveEvasion = 0,
        },
    };

    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        // Бьют во все стороны, и с броском 0 не мажут: разворот и дальность здесь ни при чём.
        // Точность 80, а не 100: иначе потолок шанса в 95 % съел бы прибавку дюз к уклонению.
        ["kinetic"] = new("Баллистическая", 100, 80, 1.0, 700, 700, 0, Arc: 180, DamageType: DamageTypes.Kinetic),
        ["energy"] = new("Лазер", 100, 100, 1.0, 700, 700, 0, Arc: 180, Kind: "beam", DamageType: DamageTypes.Energy),
        // Площадная кинетика: на ней проверяется, что отбитый выстрел не рвётся и что осколки брони не знают.
        ["blast"] = new("Площадная", 100, 100, 1.0, 700, 700, 0, Arc: 180, DamageType: DamageTypes.Kinetic,
            BlastRadius: 200, BlastShare: 0.5),
        ["missiles"] = new("Ракетница", 200, 100, 1.0, 700, 700, 0, Arc: 180, Kind: WeaponParams.MissileKind,
            Missile: new MissileParams(Speed: 200, TurnRate: 180, Lifetime: 5, HitRadius: 10)),
        // Мажет почти всегда: на ней проверяется, что по промаху блок не бросают.
        ["sloppy"] = new("Мазила", 100, Combat.MinHitChance, 1.0, 700, 700, 0, Arc: 180, DamageType: DamageTypes.Kinetic),
    };

    private static readonly IReadOnlyDictionary<string, ModuleParams> Modules = new Dictionary<string, ModuleParams>
    {
        ["engineS"] = new("Двигатель", Fitting.EngineSlot, Power: 5),
        ["shieldS"] = new("Щит", Fitting.ShieldSlot, Power: 10, Shield: 150, ShieldRegen: 20),
        ["radarS"] = new("Радар", Fitting.RadarSlot, Power: 5, Radar: TestBalance.Radar),
        ["generatorS"] = new("Генератор", Fitting.GeneratorSlot, Output: 300),
        // Блок 100 %: с ним бросок на блок не нужно подгадывать — отбивается всё, что попало бы.
        ["reactiveArmor"] = new("Динамическая защита", Fitting.UtilityKind, Power: 10, BlockKinetic: Fitting.MaxBlock),
        ["dustCloud"] = new("Аэрозольная завеса", Fitting.UtilityKind, Power: 8, BlockEnergy: Fitting.MaxBlock),
        ["thrusters"] = new("Маневровые дюзы", Fitting.UtilityKind, Power: 6, Evasion: 6),
        ["antiMissile"] = new("Противоракетный комплекс", Fitting.UtilityKind, Power: 14,
            Intercept: new InterceptParams(Range: 500, Chance: 100, Cooldown: 1.2, Damage: 500)),
    };

    private double _roll = Always;
    private readonly Room _room;
    private int _nextConnection;

    public DefenseTests() => _room = new Room(
        new Balance(Hulls, Weapons, new CombatRules(ProtectionSeconds: 0, SpawnJitter: 0, ShieldRegenDelay: 100), Modules: Modules),
        NullLogger.Instance,
        () => _roll);

    private FakeConnection Connect(string name, string? weapon = null)
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, null, name, null, weapon);
        _room.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Player PlayerOf(FakeConnection connection) => _room.Pilot(IdOf(connection))!;

    private void Place(FakeConnection connection, double x, double y) =>
        PlayerOf(connection).Ship = new ShipState { X = x, Y = y };

    /// <summary>Гость ставит что угодно куда угодно — склад ему не нужен.</summary>
    private void Fit(FakeConnection connection, string slot, string id) => _room.Fit(connection, slot, id);

    /// <summary>A стреляет в B с 300: в секторе, в оптимуме, без штрафов.</summary>
    private (FakeConnection A, FakeConnection B) Duel(string weapon)
    {
        var a = Connect("A", weapon);
        var b = Connect("B");
        Place(a, 0, 0);
        Place(b, 0, -300);
        _room.SetTarget(a, IdOf(b));
        _room.SetFire(a, true);
        return (a, b);
    }

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private static List<ShotDto> Shots(FakeConnection observer) =>
        [.. observer.Messages.OfType<SnapshotMsg>().SelectMany(s => s.Shots ?? [])];

    [Fact]
    public void ReactiveArmor_StopsAKineticHit_WithoutDamageAndWithoutSplash()
    {
        var (a, b) = Duel("blast");
        var c = Connect("C");
        Place(c, 0, -350); // в 50 от B: в радиусе осколков
        Fit(b, Fitting.UtilitySlot(0), "reactiveArmor");
        var target = PlayerOf(b);
        var bystander = PlayerOf(c);
        var (hp, shield) = (target.Hp, target.Shield);

        Steps(2);

        var shot = Shots(a).Single(s => s.To == IdOf(b));
        Assert.True(shot.Blk, "kinetic hit must be blocked");
        Assert.False(shot.Hit); // урона нет, поэтому и попаданием это не считается
        Assert.Equal(0, shot.Dmg);
        Assert.Equal((hp, shield), (target.Hp, target.Shield));
        // Отбитый выстрел не рвётся: соседа не задело, и осколочного выстрела в снапшоте нет вовсе.
        Assert.Equal(bystander.Hp, PlayerOf(c).Hp);
        Assert.DoesNotContain(Shots(a), s => s.W == Combat.SplashWeapon);
    }

    [Fact]
    public void Armor_DoesNotStopEnergy_AndTheCloudDoesNotStopKinetic()
    {
        var (a, b) = Duel("energy");
        Fit(b, Fitting.UtilitySlot(0), "reactiveArmor"); // броня против кинетики — луч ей не по адресу
        var shield = PlayerOf(b).Shield;

        Steps(2);

        var shot = Shots(a).Single(s => s.To == IdOf(b));
        Assert.False(shot.Blk);
        Assert.True(shot.Hit);
        Assert.True(PlayerOf(b).Shield < shield, "the laser must get through kinetic armour");
    }

    [Fact]
    public void DustCloud_ScattersAnEnergyHit()
    {
        var (a, b) = Duel("energy");
        Fit(b, Fitting.UtilitySlot(0), "dustCloud");
        var (hp, shield) = (PlayerOf(b).Hp, PlayerOf(b).Shield);

        Steps(2);

        Assert.True(Shots(a).Single(s => s.To == IdOf(b)).Blk);
        Assert.Equal((hp, shield), (PlayerOf(b).Hp, PlayerOf(b).Shield));
    }

    /// <summary>
    /// Бросок на блок делается только по выстрелу, который иначе попал бы: промах отбивать нечего,
    /// и «блок» на промахе соврал бы пилоту, что защита сработала.
    /// </summary>
    [Fact]
    public void Block_IsNotRolledOnAMiss()
    {
        _roll = Never;
        var (a, b) = Duel("sloppy");
        Fit(b, Fitting.UtilitySlot(0), "reactiveArmor");
        var (hp, shield) = (PlayerOf(b).Hp, PlayerOf(b).Shield);

        Steps(2);

        var shot = Shots(a).Single(s => s.To == IdOf(b));
        Assert.False(shot.Hit);
        Assert.False(shot.Blk); // промахнулся стрелок — защите тут нечего отбивать
        Assert.Equal((hp, shield), (PlayerOf(b).Hp, PlayerOf(b).Shield));
    }

    [Fact]
    public void Splash_IgnoresTheArmourOfTheNeighbour()
    {
        var (a, b) = Duel("blast");
        var c = Connect("C");
        Place(c, 0, -350);
        Fit(c, Fitting.UtilitySlot(0), "reactiveArmor"); // броня у соседа, а не у цели
        var bystander = PlayerOf(c).Shield;

        Steps(2);

        // Цель получила прямой урон, сосед — осколки: броня соседа их не отбивает, она про прямые попадания.
        Assert.True(Shots(a).Single(s => s.To == IdOf(b)).Hit);
        var splash = Shots(a).Single(s => s.W == Combat.SplashWeapon && s.To == IdOf(c));
        Assert.True(splash.Hit);
        Assert.False(splash.Blk);
        Assert.True(PlayerOf(c).Shield < bystander, "splash must reach the neighbour");
    }

    [Fact]
    public void AntiMissile_ShootsDownAMissileWithoutAWeaponSlot()
    {
        var (a, b) = Duel("missiles");
        Fit(b, Fitting.UtilitySlot(0), "antiMissile");
        var hp = PlayerOf(b).Hp;
        var shield = PlayerOf(b).Shield;

        Steps(40); // ракета летит 200 в секунду: 300 она бы прошла за полторы секунды

        var guard = Shots(b).Where(s => s.W == Combat.GuardWeapon).ToList();
        Assert.NotEmpty(guard);
        Assert.All(guard, s => Assert.Equal(IdOf(b), s.From));
        Assert.Contains(guard, s => s.Hit);
        // Ракета сбита: ни корпус, ни щит её не почувствовали, хотя оружейные слоты у B пусты.
        Assert.Equal((hp, shield), (PlayerOf(b).Hp, PlayerOf(b).Shield));
        Assert.Empty(PlayerOf(b).WeaponIds.Where(id => id is not null));
    }

    /// <summary>Комплекс один на корабль и со своей перезарядкой: 1.2 с — это выстрел раз в 24 тика.</summary>
    [Fact]
    public void AntiMissile_KeepsItsOwnCooldown()
    {
        var (a, b) = Duel("missiles");
        Fit(b, Fitting.UtilitySlot(0), "antiMissile");
        Fit(b, Fitting.UtilitySlot(1), "antiMissile"); // второй ничего не добавляет: работает один

        Steps(80);

        var guardTicks = b.Messages.OfType<SnapshotMsg>()
            .Where(s => (s.Shots ?? []).Any(shot => shot.W == Combat.GuardWeapon))
            .Select(s => s.Tick)
            .ToList();
        Assert.NotEmpty(guardTicks);
        for (var i = 1; i < guardTicks.Count; i++)
        {
            Assert.True(guardTicks[i] - guardTicks[i - 1] >= 24, $"guard fired again after {guardTicks[i] - guardTicks[i - 1]} ticks");
        }
    }

    [Fact]
    public void Thrusters_LowerTheChanceTheEnemyReports()
    {
        var (a, b) = Duel("kinetic");
        Steps(2);
        var bare = Shots(a).Single(s => s.To == IdOf(b)).Ch;

        Fit(b, Fitting.UtilitySlot(0), "thrusters");
        Steps(21); // перезарядка 1 с

        var withThrusters = Shots(a).Last(s => s.To == IdOf(b)).Ch;
        Assert.Equal(80, bare);
        Assert.Equal(bare - 6, withThrusters);
    }
}
