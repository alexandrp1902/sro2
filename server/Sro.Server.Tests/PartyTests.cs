using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>Группы (GDD §37): приглашение, состав, выход, огонь по своим, делёж награды за голову и счёт заданий.</summary>
public sealed class PartyTests
{
    private const int JumpTicks = SimConfig.TickRate; // jumpSeconds: 1

    /// <summary>home — станция, PvP везде (чтобы проверить огонь по своим), логово пирата; wild — за вратами.</summary>
    private static readonly GalaxyRules Rules = new(
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new("Home", Pvp: GalaxyRules.PvpFree, Gates: [new GateDef("wild", 3000, 0)],
                Spawns: [new NpcSpawn("pirate", 2, 0, -2500)]),
            ["wild"] = new("Wild", Station: false, Gates: [new GateDef("home", -3000, 0)]),
        },
        Links: [new LinkDef("home", "wild", 10)]);

    private static readonly NpcRules Npcs = new(
        RespawnSeconds: 1,
        LevelScaling: new NpcLevelScaling(Bounty: 0.5),
        Types: new Dictionary<string, NpcType>
        {
            ["pirate"] = new("Пират", "light", "pulse", Hp: 300, Shield: 100, Damage: 0.45, HoldRange: 320, Bounty: 100),
        });

    private readonly Galaxy _galaxy;
    private int _nextConnection;

    public PartyTests() : this(null) { }

    private PartyTests(PartyRules? party)
    {
        _galaxy = new Galaxy(
            TestBalance.Create(new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), Npcs, shop: new ShopRules(StartCredits: 1000)) with
            {
                GalaxySet = Rules,
                PartySet = party ?? new PartyRules(MaxSize: 3, InviteSeconds: 2, ShareRange: 1000, ShareBonus: 0.2),
            },
            NullLogger.Instance, roll: () => 0, random: seed => new Random(seed));
    }

    private FakeConnection Guest(string name)
    {
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.Join(connection, null, name, null);
        _galaxy.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Room RoomOf(FakeConnection connection) => _galaxy.RoomOf(connection)!;

    private Player PlayerOf(FakeConnection connection) => RoomOf(connection).Pilot(IdOf(connection))!;

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private void Do(FakeConnection connection, Action<Room> command) => _galaxy.With(connection, command);

    private void Place(FakeConnection connection, double x, double y) =>
        PlayerOf(connection).Ship = new ShipState { X = x, Y = y };

    private void Party(FakeConnection connection, string action, FakeConnection? other = null) =>
        _galaxy.Party(connection, action, other is null ? 0 : IdOf(other));

    /// <summary>a зовёт b, b соглашается.</summary>
    private void Join(FakeConnection a, FakeConnection b)
    {
        Party(a, PartyCodes.InviteAction, b);
        Party(b, PartyCodes.AcceptAction, a);
    }

    private static PartyEventMsg LastEvent(FakeConnection connection) => connection.Last<PartyEventMsg>();

    private static int Credits(FakeConnection connection) => connection.Last<CargoMsg>().Credits;

    private int PirateId(FakeConnection observer) =>
        observer.Last<PlayersMsg>().Players.First(p => p.Kind == Protocol.PirateKind).Id;

    private void Kill(FakeConnection connection, int id)
    {
        var target = RoomOf(connection).Entity(id)!;
        PlayerOf(connection).WeaponId = "doom";
        Place(connection, target.Ship.X, target.Ship.Y + 200);
        Do(connection, r => r.SetTarget(connection, id));
        Do(connection, r => r.SetFire(connection, true));
        Steps(2);
        Do(connection, r => r.SetFire(connection, false));
        Assert.True(target.IsDead);
    }

    [Fact]
    public void Invite_ThenAccept_FormsAParty_AndBothSeeIt()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        Party(a, PartyCodes.InviteAction, b);
        Assert.Equal(new PartyInviteMsg(IdOf(a), "Alice", 2), b.Last<PartyInviteMsg>());
        Assert.Equal(new PartyEventMsg(PartyCodes.Invited, "Bob"), LastEvent(a));

        Party(b, PartyCodes.AcceptAction, a);
        Assert.Equal(new PartyEventMsg(PartyCodes.Joined, "Bob"), LastEvent(a));
        Assert.Equal(new PartyEventMsg(PartyCodes.Joined), LastEvent(b));
        foreach (var c in new[] { a, b })
        {
            var state = c.Last<PartyStateMsg>();
            Assert.Equal(IdOf(a), state.Leader);
            Assert.Equal(["Alice", "Bob"], state.Members.Select(m => m.Name));
            Assert.All(state.Members, m => Assert.Equal(("home", "Home", true), (m.System, m.SystemName, m.Online)));
        }
        Assert.True(_galaxy.SameParty(IdOf(a), IdOf(b)));

        // Состояние приходит и само, раз в statusSeconds.
        var before = a.Count<PartyStateMsg>();
        Steps(SimConfig.TickRate);
        Assert.True(a.Count<PartyStateMsg>() > before);
    }

    [Fact]
    public void Invite_Expires_AndTheInviterIsTold()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        Party(a, PartyCodes.InviteAction, b);
        Steps(2 * SimConfig.TickRate + 1);
        Assert.Equal(new PartyEventMsg(PartyCodes.Expired, "Bob"), LastEvent(a));

        Party(b, PartyCodes.AcceptAction, a);
        Assert.Equal(PartyCodes.Expired, LastEvent(b).Code);
        Assert.False(_galaxy.SameParty(IdOf(a), IdOf(b)));
    }

    [Fact]
    public void Decline_TellsTheInviter()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        Party(a, PartyCodes.InviteAction, b);
        Party(b, PartyCodes.DeclineAction, a);
        Assert.Equal(new PartyEventMsg(PartyCodes.Declined, "Bob"), LastEvent(a));
        Party(b, PartyCodes.AcceptAction, a);
        Assert.False(_galaxy.SameParty(IdOf(a), IdOf(b)));
    }

    [Fact]
    public void Party_HasALimit_AndOneCannotBeInTwo()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        var c = Guest("Carol");
        var d = Guest("Dave");
        Join(a, b);
        Join(a, c);
        Party(a, PartyCodes.InviteAction, d);
        Assert.Equal(new PartyEventMsg(PartyCodes.Full, "Dave"), LastEvent(a));

        Party(d, PartyCodes.InviteAction, b);
        Assert.Equal(new PartyEventMsg(PartyCodes.Busy, "Bob"), LastEvent(d));

        // Участник тоже может звать; но мест нет, даже если приглашение ушло до того, как группа заполнилась.
        Assert.Equal(3, c.Last<PartyStateMsg>().Members.Count);
    }

    [Fact]
    public void Leave_PassesTheLead_AndTheLastTwoDisband()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        var c = Guest("Carol");
        Join(a, b);
        Join(a, c);

        Party(a, PartyCodes.LeaveAction);
        Assert.Empty(a.Last<PartyStateMsg>().Members);
        Assert.Equal(new PartyEventMsg(PartyCodes.Left, "Alice"), LastEvent(b));
        Assert.Equal(IdOf(b), c.Last<PartyStateMsg>().Leader);
        Assert.Equal(["Bob", "Carol"], c.Last<PartyStateMsg>().Members.Select(m => m.Name));

        Party(c, PartyCodes.LeaveAction);
        Assert.Equal(PartyCodes.Disbanded, LastEvent(b).Code);
        Assert.Empty(b.Last<PartyStateMsg>().Members);
        Assert.False(_galaxy.SameParty(IdOf(b), IdOf(c)));
        Assert.Null(_galaxy.Parties.PartyOf(IdOf(b)));
    }

    [Fact]
    public void LeavingTheGame_LeavesTheParty()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        Join(a, b);
        _galaxy.Disconnect(b); // гость без сессии уходит сразу
        Assert.Equal(PartyCodes.Disbanded, LastEvent(a).Code);
        Assert.Null(_galaxy.Parties.PartyOf(IdOf(a)));
    }

    [Fact]
    public void Members_DoNotHurtEachOther_EvenWherePvpIsFree()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        var c = Guest("Carol");
        Join(a, b);
        Place(a, 0, 1000);
        Place(b, 0, 1300);
        Place(c, 300, 1000);
        PlayerOf(a).WeaponId = "turret";
        var hp = PlayerOf(b).Hp + PlayerOf(b).Shield;
        Do(a, r => r.SetTarget(a, IdOf(b)));
        Do(a, r => r.SetFire(a, true));
        Steps(3 * SimConfig.TickRate);
        Assert.Equal(hp, PlayerOf(b).Hp + PlayerOf(b).Shield);

        // Не в группе — честная цель.
        var carol = PlayerOf(c).Hp + PlayerOf(c).Shield;
        Do(a, r => r.SetTarget(a, IdOf(c)));
        Steps(3 * SimConfig.TickRate);
        Assert.True(PlayerOf(c).Hp + PlayerOf(c).Shield < carol);
    }

    [Fact]
    public void Members_AreNotSplashed_EvenWherePvpIsFree()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        var c = Guest("Carol");
        Join(a, b);
        Place(a, 0, 1000);
        Place(c, 0, 700);  // цель — чужой пилот
        Place(b, 60, 700); // товарищ по группе стоит вплотную к ней
        PlayerOf(a).WeaponId = "blast";
        var mate = PlayerOf(b).Hp + PlayerOf(b).Shield;
        var stranger = PlayerOf(c).Hp + PlayerOf(c).Shield;
        Do(a, r => r.SetTarget(a, IdOf(c)));
        Do(a, r => r.SetFire(a, true));
        Steps(SimConfig.TickRate);

        // Осколки — тоже огонь по своим (GDD §37): группу они не задевают даже там, где PvP свободен.
        Assert.Equal(mate, PlayerOf(b).Hp + PlayerOf(b).Shield);
        Assert.True(PlayerOf(c).Hp + PlayerOf(c).Shield < stranger);
    }

    [Fact]
    public void Bounty_GoesToALoneKiller_InFull()
    {
        var a = Guest("Alice");
        var credits = Credits(a);
        Kill(a, PirateId(a));
        // Ур.2: 100 × (1 + 0.5).
        Assert.Equal(new BountyMsg(150, 1, "Пират Ур.2"), a.Last<BountyMsg>());
        Assert.Equal(credits + 150, Credits(a));
    }

    [Fact]
    public void Bounty_IsSharedWithMembersNearby_AndTheKillCountsForTheirMissions()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        var c = Guest("Carol");
        Join(a, b);
        Join(a, c);
        var offer = new MissionOffer("k1", MissionRules.KillKind, "home", null, null, 2, 500, "home");
        PlayerOf(b).Missions.Active = new ActiveMission(offer);
        var pirate = RoomOf(a).Entity(PirateId(a))!;
        Place(b, pirate.Ship.X + 300, pirate.Ship.Y); // рядом
        Place(c, pirate.Ship.X, pirate.Ship.Y + 5000); // далеко — не делит
        var (ca, cb, cc) = (Credits(a), Credits(b), Credits(c));

        Kill(a, pirate.Id);
        // Двое делят 150 × 1.2 = 180 — по 90.
        Assert.Equal(new BountyMsg(90, 2, "Пират Ур.2"), a.Last<BountyMsg>());
        Assert.Equal(new BountyMsg(90, 2, "Пират Ур.2"), b.Last<BountyMsg>());
        Assert.Equal((ca + 90, cb + 90, cc), (Credits(a), Credits(b), Credits(c)));
        Assert.Equal(0, c.Count<BountyMsg>());
        Assert.Equal(1, PlayerOf(b).Missions.Active!.Progress);
    }

    [Fact]
    public void Party_SpansSystems()
    {
        var a = Guest("Alice");
        var b = Guest("Bob");
        Join(a, b);
        Place(b, 3000, 0);
        Do(b, r => r.Jump(b, "wild"));
        Steps(JumpTicks + SimConfig.TickRate);
        Assert.Equal("wild", RoomOf(b).SystemId);
        var bob = a.Last<PartyStateMsg>().Members.Single(m => m.Name == "Bob");
        Assert.Equal(("wild", "Wild"), (bob.System, bob.SystemName));
        Assert.True(_galaxy.SameParty(IdOf(a), IdOf(b)));
    }

    [Fact]
    public void Share_AddsABonusPerMember()
    {
        var rules = new PartyRules(ShareBonus: 0.2);
        Assert.Equal(150, rules.Share(150, 1));
        Assert.Equal(90, rules.Share(150, 2));
        Assert.Equal(70, rules.Share(150, 3)); // 150 × 1.4 / 3
    }
}
