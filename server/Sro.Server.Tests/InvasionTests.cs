using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>«Вторжение пиратов» (GDD §38): расписание, анонс, волны у станции, итог и доли по урону.</summary>
public sealed class InvasionTests
{
    /// <summary>home — станция, опасность 2, врата в wild; wild — без станции: вторжению там не бывать.</summary>
    private static readonly GalaxyRules Rules = new(
        FuelPerDistance: 1,
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new("Home", Danger: 2, Gates: [new GateDef("wild", 3000, 0)],
                Pirates: new RaidRules(MaxGroups: 1, IntervalSeconds: 1, Groups: [new RaidGroup("pirate")])),
            ["wild"] = new("Wild", Station: false, Gates: [new GateDef("home", -3000, 0)]),
        },
        Links: [new LinkDef("home", "wild", 10)]);

    private static readonly NpcRules Npcs = new(
        RespawnSeconds: 1,
        Types: new Dictionary<string, NpcType>
        {
            ["pirate"] = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320, RetreatHp: 0.25),
        });

    /// <summary>Анонс секунда, волны: один пират, потом два; на всё — минута; фонд 1000.</summary>
    private static readonly InvasionRules Invasion = new(
        IntervalMinutes: 10,
        FirstMinutes: 0,
        AnnounceSeconds: 1,
        DurationSeconds: 60,
        WaveGapSeconds: 1,
        Fund: 1000,
        FailShare: 0.3,
        MinShare: 100,
        PointOffset: 1400,
        Waves: [[new InvasionGroup("pirate")], [new InvasionGroup("pirate", Level: 2, Count: 2)]]);

    private readonly Galaxy _galaxy;
    private int _nextConnection;

    public InvasionTests()
    {
        _galaxy = new Galaxy(
            TestBalance.Create(new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), Npcs, shop: new ShopRules(StartCredits: 1000)) with
            {
                GalaxySet = Rules,
                InvasionSet = Invasion,
            },
            NullLogger.Instance, roll: () => 0, random: seed => new Random(seed));
    }

    private FakeConnection Guest(string name)
    {
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.Join(connection, null, name, null);
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Room Home => _galaxy["home"];

    private Player PlayerOf(FakeConnection connection) => _galaxy.RoomOf(connection)!.Pilot(IdOf(connection))!;

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private List<Pirate> Invaders() =>
        [.. Enumerable.Range(1, 1000).Select(Home.Entity).OfType<Pirate>().Where(p => p.IsInvader && !p.IsDead)];

    /// <summary>Шагает, пока не придёт сообщение о вторжении в этом состоянии.</summary>
    private InvasionMsg StepUntil(FakeConnection observer, string state, int maxTicks = 120 * SimConfig.TickRate)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (observer.Messages.OfType<InvasionMsg>().LastOrDefault() is { } last && last.State == state) return last;
            _galaxy.Step();
        }
        throw new Xunit.Sdk.XunitException($"no invasion '{state}' in {maxTicks} ticks");
    }

    /// <summary>Пилот бьёт пирата вплотную пушкой weapon, пока тот не умрёт (или ticks тиков).</summary>
    private void Shoot(FakeConnection connection, Pirate target, string weapon, int ticks)
    {
        var player = PlayerOf(connection);
        player.WeaponId = weapon;
        player.Ship = new ShipState { X = target.Ship.X, Y = target.Ship.Y + 200 };
        _galaxy.With(connection, r => r.SetTarget(connection, target.Id));
        _galaxy.With(connection, r => r.SetFire(connection, true));
        for (var i = 0; i < ticks && !target.IsDead; i++)
        {
            player.Ship = new ShipState { X = target.Ship.X, Y = target.Ship.Y + 200 };
            _galaxy.Step();
        }
        _galaxy.With(connection, r => r.SetFire(connection, false));
    }

    [Fact]
    public void Invasion_IsAnnounced_ThenWavesComeThroughTheGate_AndRaidsWait()
    {
        var a = Guest("Alice");
        var announce = StepUntil(a, Protocol.InvasionAnnounce);
        Assert.Equal(("home", "Home", 2), (announce.System, announce.SystemName, announce.Waves));
        Assert.True(announce.SecondsLeft is > 0 and <= 1);

        var wave = StepUntil(a, Protocol.InvasionWave);
        Assert.Equal((1, 2, 1), (wave.Wave, wave.Waves, wave.Remaining));
        // Точка сбора — за станцией (она в центре — значит, к «низу» карты), на pointOffset.
        Assert.Equal((0, -1400), (wave.X, wave.Y));
        Assert.NotEqual(0, Home.InvasionId);

        var invader = Assert.Single(Invaders());
        Assert.Equal("Пират Ур.2", invader.Name); // уровень 1 + опасность 2 − 1
        Assert.True(Math.Abs(invader.Ship.X - 3000) < 200, $"arrived at {invader.Ship.X:0}, {invader.Ship.Y:0}");
        Assert.Equal(PirateState.Return, invader.State);

        // Налёты стоят, пока идёт вторжение: новых налётчиков нет.
        var raiders = Enumerable.Range(1, 1000).Select(Home.Entity).OfType<Pirate>().Count(p => p.IsRaider && !p.IsInvader);
        Steps(5 * SimConfig.TickRate);
        Assert.Equal(raiders, Enumerable.Range(1, 1000).Select(Home.Entity).OfType<Pirate>().Count(p => p.IsRaider && !p.IsInvader && !p.Gone));
    }

    [Fact]
    public void Newcomer_LearnsAboutTheInvasionAtOnce()
    {
        var a = Guest("Alice");
        StepUntil(a, Protocol.InvasionAnnounce);
        var b = Guest("Bob");
        Assert.Equal(Protocol.InvasionAnnounce, b.Last<InvasionMsg>().State);
    }

    [Fact]
    public void Repelled_PaysTheFundByDamage_WithAMinimumShare()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        StepUntil(a, Protocol.InvasionWave);

        // Bob чуть задел первого пирата, Alice добила — и потом всю вторую волну.
        var first = Assert.Single(Invaders());
        Shoot(b, first, "pulse", 2);
        Assert.True(_galaxy.Invasion.DamageOf(IdOf(b)) > 0);
        Shoot(a, first, "doom", 2 * SimConfig.TickRate);
        Assert.True(first.IsDead);

        for (var i = 0; i < 5 * SimConfig.TickRate && Invaders().Count < 2; i++) _galaxy.Step();
        Assert.Equal(2, a.Last<InvasionMsg>().Wave);
        var wave2 = Invaders();
        Assert.Equal(2, wave2.Count);
        Assert.All(wave2, p => Assert.Equal("Пират Ур.3", p.Name));
        var creditsA = PlayerOf(a).Credits;
        var creditsB = PlayerOf(b).Credits;
        foreach (var pirate in wave2) Shoot(a, pirate, "doom", 2 * SimConfig.TickRate);
        Assert.All(wave2, p => Assert.True(p.IsDead, $"{p} alive: {p.Hp} {a.Last<InvasionMsg>()}"));

        var won = StepUntil(a, Protocol.InvasionWon, 2);
        var damageA = _galaxy.Invasion.DamageOf(IdOf(a));
        var damageB = _galaxy.Invasion.DamageOf(IdOf(b));
        var shareB = Math.Max(100, (int)Math.Round(1000 * damageB / (damageA + damageB)));
        var shareA = (int)Math.Round(1000 * damageA / (damageA + damageB));
        Assert.Equal(shareA, won.Reward);
        Assert.Equal(shareB, b.Last<InvasionMsg>().Reward);
        Assert.Equal(creditsA + shareA, PlayerOf(a).Credits);
        Assert.Equal(creditsB + shareB, PlayerOf(b).Credits);
        Assert.Equal(["Alice", "Bob"], won.Results!.Select(r => r.Name));
        Assert.Equal(0, Home.InvasionId);
        Assert.Equal(InvasionDirector.Phase.Idle, _galaxy.Invasion.State);
    }

    [Fact]
    public void NotRepelledInTime_Fails_AndThePiratesLeave()
    {
        var a = Guest("Alice");
        StepUntil(a, Protocol.InvasionWave);
        var invader = Assert.Single(Invaders());
        Shoot(a, invader, "pulse", 2);
        var damage = _galaxy.Invasion.DamageOf(IdOf(a));
        Assert.True(damage > 0);

        var lost = StepUntil(a, Protocol.InvasionLost, 61 * SimConfig.TickRate);
        Assert.Equal(300, lost.Reward); // 30% фонда — единственному участнику
        Assert.Equal(PirateState.Leave, invader.State);
    }

    [Fact]
    public void Invaders_FightToTheEnd()
    {
        var a = Guest("Alice");
        StepUntil(a, Protocol.InvasionWave);
        var invader = Assert.Single(Invaders());
        Assert.Equal(0, invader.RetreatHp);
    }

    [Fact]
    public void Shares_AreByDamage_WithAMinimum_AndNothingForNoDamage()
    {
        Assert.Equal([900, 100, 0], Invasion.Shares([90, 10, 0], won: true));
        Assert.Equal([990, 100], Invasion.Shares([99, 1], won: true));
        Assert.Equal([150, 150], Invasion.Shares([5, 5], won: false));
    }
}
