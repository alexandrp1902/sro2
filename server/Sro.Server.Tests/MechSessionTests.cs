using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;
using Sro.Sim.Mech;

namespace Sro.Server.Tests;

/// <summary>
/// Наземный бой у ретранслятора (M21): кто может начать, что сервер отвергает, что бывает при обрыве связи,
/// почему нельзя улететь посреди боя и за что платят. Правила самого боя — в Sro.Sim.Tests.
/// </summary>
public sealed class MechSessionTests : IDisposable
{
    private const string Password = "secret";
    private const string Campaign = "deal";

    private static readonly GalaxyRules Galaxy = new(
        GateRange: 250,
        JumpSeconds: 1,
        ArrivalOffset: 250,
        StartSystem: "home",
        Systems: new Dictionary<string, SystemDef>
        {
            ["home"] = new("Home", Gates: [new GateDef("port", 3000, 0)]),
            ["port"] = new("Port", Gates: [new GateDef("home", -3000, 0)]),
        },
        Links: [new LinkDef("home", "port", 10)]);

    private static readonly LootRules Loot = new(
        FadeSeconds: 0,
        Items: new Dictionary<string, LootItem> { ["metal"] = new("Металл", Volume: 1, Price: 10) });

    private static readonly StoryRules Story = new(new Dictionary<string, StoryCampaign>
    {
        [Campaign] = new(
            "Сделка",
            Missions:
            [
                new("one", "Одна", "st:home", "Ева", "инженер",
                    Objective: "Привезите металл", Kind: MissionRules.CollectKind, Item: "metal", Count: 1,
                    Dest: "st:home", Reward: 10),
            ]),
    });

    private static readonly MechRules Mechs = new(
        Bodies: new Dictionary<string, MechFrame> { ["medium"] = new("Средний", 2200, 25) },
        Chassis: new Dictionary<string, MechFrame> { ["medium"] = new("Среднее", 1100, 20, 4) },
        Weapons: new Dictionary<string, MechWeapon> { ["gun"] = new("Автопушка", 240, 82, 2, 3, 5, 7, 600, 15) },
        Shields: new Dictionary<string, MechShield> { ["light"] = new("Щит", 1100, 25, 30) },
        Units: new Dictionary<string, MechUnitDef>
        {
            ["proto"] = new("Прототип", "medium", "medium", "light", "gun"),
            ["raider"] = new("Налётчик", "medium", "medium", "light", "gun"),
        },
        Missions: new Dictionary<string, MechMission>
        {
            [MechRules.FirstSortie] = new(
                "Первая вылазка", "Уничтожить противников",
                ["........", "........", "........", "........", "........", "........", "........", "........"],
                [new("proto", 0, 7, 1)],
                [new("raider", 5, 2, 5), new("raider", 6, 2, 5)],
                Reward: 1500,
                Intro: new MechLines("Ева", "инженер", ["На связи."]),
                Win: new MechLines("Ева", "инженер", ["Победа."]),
                Lose: new MechLines("Ева", "инженер", ["Связь потеряна."])),
        });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-mech-" + Guid.NewGuid().ToString("N"));
    private readonly Accounts.AccountStore _accounts;
    private readonly Galaxy _galaxy;
    private int _nextConnection;

    public MechSessionTests()
    {
        _accounts = new Accounts.AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _galaxy = new Galaxy(
            TestBalance.Create(
                new CombatRules(RespawnSeconds: 1, ProtectionSeconds: 0, SpawnJitter: 0), loot: Loot,
                shop: new ShopRules(StartCredits: 1000)) with
            {
                GalaxySet = Galaxy,
                StorySet = Story,
                MechSet = Mechs,
            }, NullLogger.Instance, _accounts, roll: () => 0, random: seed => new Random(seed));
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ------------------------------------------------------------------ вспомогательное

    private FakeConnection Pilot(string name = "Alice")
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.JoinAccount(connection, login.Id, login.Name);
        Do(connection, r => r.Mission(connection, Protocol.SkipTutorial, null));
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Room RoomOf(FakeConnection connection) => _galaxy.RoomOf(connection)!;

    private Player PlayerOf(FakeConnection connection) => RoomOf(connection).Pilot(IdOf(connection))!;

    private void Do(FakeConnection connection, Action<Room> command) => _galaxy.With(connection, command);

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    private void Dock(FakeConnection connection)
    {
        PlayerOf(connection).Ship = new ShipState { X = 0, Y = 50 };
        Do(connection, r => r.Dock(connection, true));
        Assert.True(connection.Last<HangarMsg>().Docked);
    }

    /// <summary>Кампания пройдена, пилот в доке, где она кончилась, — ретранслятор открыт.</summary>
    private FakeConnection AtRelay(string name = "Alice")
    {
        var c = Pilot(name);
        if (connectionDocked(c)) Do(c, r => r.Dock(c, false));
        PlayerOf(c).StoryOf(Campaign).Done.Add("one");
        Dock(c);
        Assert.True(c.Last<MissionsMsg>().Story?.Relay);
        return c;

        static bool connectionDocked(FakeConnection c) => c.Last<HangarMsg>().Docked;
    }

    private void Act(FakeConnection c, string act, int x = 0, int y = 0, int? dir = null, string? target = null, string? part = null) =>
        _galaxy.Mech(c, new MechActMsg(act, x, y, dir, target, part));

    private MechBattle BattleOf(FakeConnection c) => _galaxy.MechOf(PlayerOf(c).AccountId)!.Battle;

    /// <summary>Налётчики без пушек и на последнем издыхании: бой выигрывается за несколько ходов, не случайно.</summary>
    private void Soften(FakeConnection c)
    {
        foreach (var enemy in BattleOf(c).Living(MechBattle.EnemySide))
        {
            enemy.Hp[(int)MechPart.Body] = 1;
            enemy.Hp[(int)MechPart.Right] = 0;
        }
    }

    /// <summary>Играть за пилота тем же ИИ, что за налётчиков, пока не придёт итог.</summary>
    private MechEndMsg PlayOut(FakeConnection c)
    {
        var ends = c.Count<MechEndMsg>();
        for (var guard = 0; guard < 200 && c.Count<MechEndMsg>() == ends; guard++)
        {
            var battle = BattleOf(c);
            foreach (var command in MechAi.Decide(battle))
            {
                Act(c, command.Act, command.X, command.Y, command.Dir, command.Target, command.Part);
                if (c.Count<MechEndMsg>() > ends || _galaxy.MechOf(PlayerOf(c).AccountId)?.Battle.Current?.Side != MechBattle.PlayerSide) break;
            }
        }
        return c.Last<MechEndMsg>();
    }

    private static int Credits(FakeConnection connection) => connection.Last<CargoMsg>().Credits;

    // ------------------------------------------------------------------ старт

    [Fact]
    public void WithoutTheRelay_TheBattleDoesNotStart()
    {
        var c = Pilot();
        Act(c, Protocol.MechStart);
        Assert.Equal(MechCodes.NoRelay, c.Last<MechRefusedMsg>().Code);
        Assert.Equal(0, c.Count<MechStateMsg>());
    }

    [Fact]
    public void AtTheRelay_TheBattleStarts_WithRulesAndEvasWords()
    {
        var c = AtRelay();
        Act(c, Protocol.MechStart);
        var state = c.Last<MechStateMsg>();
        Assert.NotNull(state.Rules);
        Assert.Equal(MechBattle.PlayerSide, state.Battle.Turn);
        Assert.Equal(3, state.Battle.Units.Length);
        Assert.Equal(["На связи."], c.Last<DialogMsg>().Lines);

        // Повторный «старт» — не новый бой, а то же поле ещё раз.
        var seed = BattleOf(c);
        Act(c, Protocol.MechStart);
        Assert.Same(seed, BattleOf(c));
        Assert.Equal(2, c.Count<MechStateMsg>());
    }

    [Fact]
    public void AGuest_GetsNoBattle()
    {
        var c = new FakeConnection(++_nextConnection);
        _galaxy.Join(c, "guest-token", "Гость", null);
        Act(c, Protocol.MechStart);
        Assert.Equal(0, c.Count<MechStateMsg>());
        Assert.Equal(0, c.Count<MechRefusedMsg>());
    }

    // ------------------------------------------------------------------ ходы

    [Fact]
    public void BadMoves_AreRefusedWithACode_AndChangeNothing()
    {
        var c = AtRelay();
        Act(c, MechCommand.MoveAct, 0, 7);
        Assert.Equal(MechCodes.NoBattle, c.Last<MechRefusedMsg>().Code);

        Act(c, Protocol.MechStart);
        var states = c.Count<MechStateMsg>();
        Act(c, MechCommand.MoveAct, 7, 0);
        Assert.Equal(MechCodes.Unreachable, c.Last<MechRefusedMsg>().Code);
        Act(c, MechCommand.AttackAct, target: "p1");
        Assert.Equal(MechCodes.NoTarget, c.Last<MechRefusedMsg>().Code);
        Act(c, "teleport");
        Assert.Equal(MechCodes.BadAct, c.Last<MechRefusedMsg>().Code);
        Assert.Equal(states, c.Count<MechStateMsg>());
        Assert.Equal(0, c.Count<MechEventsMsg>());
    }

    [Fact]
    public void AMove_AnswersWithEvents_AndTheEnemiesTurnRightAfter()
    {
        var c = AtRelay();
        Act(c, Protocol.MechStart);
        Act(c, MechCommand.MoveAct, 2, 5);
        Act(c, MechCommand.EndAct, dir: 1);
        var events = c.Last<MechEventsMsg>().Events;
        // Ход игрока и оба хода налётчиков — одним пакетом: клиенту не надо ждать и опрашивать.
        Assert.Contains(events, e => e.Unit == "e1");
        Assert.Contains(events, e => e.Unit == "e2");
        var state = c.Last<MechStateMsg>().Battle;
        Assert.Equal(2, state.Round);
        Assert.Equal("p1", state.Current);
    }

    // ------------------------------------------------------------------ док и связь

    [Fact]
    public void YouCannotUndock_WhileTheBattleIsOn()
    {
        var c = AtRelay();
        Act(c, Protocol.MechStart);
        Do(c, r => r.Dock(c, false));
        Assert.Equal(Protocol.MechBusyNotice, c.Last<NoticeMsg>().Code);
        Assert.True(c.Last<HangarMsg>().Docked);

        Act(c, Protocol.MechQuit);
        Do(c, r => r.Dock(c, false));
        Assert.False(c.Last<HangarMsg>().Docked);
    }

    [Fact]
    public void AfterALostConnection_TheBattleWaits_AndComesBack()
    {
        var c = AtRelay();
        Act(c, Protocol.MechStart);
        Act(c, MechCommand.MoveAct, 1, 6);
        var account = PlayerOf(c).AccountId!;
        _galaxy.Disconnect(c);
        // Комната забывает пилота через минуту, а бой ждёт дольше.
        Steps(Room.ReconnectGraceTicks + 1);
        Assert.NotNull(_galaxy.MechOf(account));

        var back = new FakeConnection(++_nextConnection);
        var login = _accounts.Login("Alice", Password);
        _galaxy.JoinAccount(back, login.Id, login.Name);
        var state = back.Last<MechStateMsg>();
        Assert.NotNull(state.Rules);
        Assert.True(state.Battle.Moved);
        Assert.Equal(1, state.Battle.Units.First(u => u.Id == "p1").X);
    }

    [Fact]
    public void AnAbandonedBattle_IsLostAfterFiveMinutes()
    {
        var c = AtRelay();
        Act(c, Protocol.MechStart);
        var account = PlayerOf(c).AccountId!;
        _galaxy.Disconnect(c);
        Steps(Game.Galaxy.MechGraceTicks + 1);
        Assert.Null(_galaxy.MechOf(account));
    }

    // ------------------------------------------------------------------ итог

    [Fact]
    public void TheFirstWinPays_TheNextDoNot()
    {
        var c = AtRelay();
        var before = Credits(c);
        Act(c, Protocol.MechStart);
        Soften(c);
        var end = PlayOut(c);
        Assert.True(end.Won);
        Assert.True(end.First);
        Assert.Equal(1500, end.Reward);
        Assert.Equal(before + 1500, Credits(c));
        Assert.Equal(["Победа."], c.Last<DialogMsg>().Lines);
        Assert.True(c.Last<MissionsMsg>().Story?.SortieWon);
        Assert.Null(_galaxy.MechOf(PlayerOf(c).AccountId));

        Act(c, Protocol.MechStart);
        Soften(c);
        var again = PlayOut(c);
        Assert.True(again.Won);
        Assert.False(again.First);
        Assert.Equal(0, again.Reward);
        Assert.Equal(before + 1500, Credits(c));
    }

    [Fact]
    public void Quitting_IsADefeat_WithEvasWords()
    {
        var c = AtRelay();
        Act(c, Protocol.MechStart);
        Act(c, Protocol.MechQuit);
        var end = c.Last<MechEndMsg>();
        Assert.False(end.Won);
        Assert.Equal(0, end.Reward);
        Assert.Equal(["Связь потеряна."], c.Last<DialogMsg>().Lines);
        Assert.False(c.Last<MissionsMsg>().Story?.SortieWon ?? false);
    }
}
