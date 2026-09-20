using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Репутация в комнате (M13): что её двигает, что она закрывает и как переживает выход из игры.
/// Баланс здесь свой (<see cref="TestBalance.Reputation"/>), чтобы тюнинг shared/ не ронял тесты.
/// </summary>
public sealed class ReputationTests : IDisposable
{
    private const string Password = "secret";
    private const string Sys = "sys:" + GalaxyRules.DefaultSystem;
    private const string Place = "st:" + GalaxyRules.DefaultSystem;

    private static readonly ShopRules Shop = new(
        StartCredits: 10000,
        RepairPrice: 1,
        Hulls: new Dictionary<string, int> { ["light"] = 0, ["heavy"] = 900, ["cruiser"] = 6000 },
        Items: new Dictionary<string, int> { ["pulse"] = 100, ["laser"] = 300 },
        Tiers: [new TierDef(1.5, 2.5), new TierDef(1.5, 2.5)]);

    private static readonly LootRules Loot = new(
        Items: new Dictionary<string, LootItem> { ["metal"] = new("Металл", Volume: 1, Price: 10) });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-rep-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private Room _room;
    private readonly Dictionary<FakeConnection, string> _ids = [];
    private int _nextConnection;

    public ReputationTests()
    {
        _accounts = new AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _room = NewRoom(_accounts);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Room NewRoom(AccountStore accounts) => new(
        TestBalance.Create(new CombatRules(ProtectionSeconds: 3, SpawnJitter: 0), loot: Loot, shop: Shop,
            reputation: TestBalance.Reputation(), missions: TestBalance.Missions()),
        NullLogger.Instance,
        accounts: accounts);

    private FakeConnection Pilot(string name = "Alice")
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        var connection = new FakeConnection(++_nextConnection);
        _room.JoinAccount(connection, login.Id, login.Name);
        _room.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        _ids[connection] = login.Id;
        return connection;
    }

    /// <summary>
    /// Заново войти тем же аккаунтом в свежую комнату: так проверяется настоящий путь — профиль с диска,
    /// распад на входе и доска, посчитанная уже по загруженной репутации.
    /// </summary>
    private FakeConnection Rejoin(FakeConnection old)
    {
        var id = _ids[old];
        _room = NewRoom(_accounts);
        var connection = new FakeConnection(++_nextConnection);
        _room.JoinAccount(connection, id, "Alice");
        _ids[connection] = id;
        return connection;
    }

    /// <summary>Записать репутацию в профиль: стыковка сохраняет пилота целиком.</summary>
    private void Persist(FakeConnection connection)
    {
        var player = PlayerOf(connection);
        if (player.Docked) _room.Dock(connection, false);
        Docked(connection);
    }

    private Player PlayerOf(FakeConnection connection) => _room.Pilot(connection.Last<WelcomeMsg>().Id)!;

    private Player Docked(FakeConnection connection)
    {
        var player = PlayerOf(connection);
        player.Ship = new ShipState { X = 0, Y = 50 };
        _room.Dock(connection, true);
        Assert.True(player.Docked, "the pilot was expected to dock");
        return player;
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private double Rep(Player player, string key) => player.Rep.Value(key, Now, TestBalance.Reputation());

    /// <summary>Поставить пилоту такое отношение системы, как будто он его заработал.</summary>
    private static void SetRep(Player player, string key, double value) =>
        player.Rep.Add(key, value, Now, TestBalance.Reputation());

    [Fact]
    public void Welcome_CarriesTheRulesAndAnEmptyScale()
    {
        var a = Pilot();
        Assert.NotNull(a.Last<WelcomeMsg>().Reputation);
        var rep = a.Last<RepMsg>();
        Assert.Empty(rep.Systems);
        Assert.Empty(rep.Places);
    }

    [Fact]
    public void Docking_TellsWhereYouStand()
    {
        var a = Pilot();
        var player = Docked(a);
        SetRep(player, Place, 42);
        _room.Dock(a, false);
        Docked(a);

        var here = a.Last<RepMsg>().Here;
        Assert.NotNull(here);
        Assert.Equal(Place, here!.Place);
        Assert.Equal(42, here.Value);
        Assert.Equal("friend", here.Level);
    }

    [Fact]
    public void AbandoningAMission_CostsThePlaceThatIssuedIt()
    {
        var a = Pilot();
        var player = Docked(a);
        var offer = a.Last<MissionsMsg>().Offers.FirstOrDefault();
        Assert.NotNull(offer);
        _room.Mission(a, Protocol.AcceptMission, offer!.Id);

        _room.Mission(a, Protocol.AbandonMission, null);

        Assert.Equal(-5, Rep(player, offer.From), 6); // From — уже ключ места (M15)
        Assert.Equal(Protocol.RepMissionAbandon, a.Last<RepMsg>().Change!.Code);
    }

    [Fact]
    public void AnEnemyOfTheSystem_FindsTheDockClosed()
    {
        var a = Pilot();
        var player = PlayerOf(a);
        SetRep(player, Sys, -80);
        player.Ship = new ShipState { X = 0, Y = 50 };

        _room.Dock(a, true);

        Assert.False(player.Docked);
        Assert.Equal(Protocol.DockClosedNotice, a.Last<NoticeMsg>().Code);
    }

    [Fact]
    public void AFriendOfTheSystem_DocksAsUsual()
    {
        var a = Pilot();
        var player = PlayerOf(a);
        SetRep(player, Sys, 40);
        player.Ship = new ShipState { X = 0, Y = 50 };

        _room.Dock(a, true);

        Assert.True(player.Docked);
    }

    [Fact]
    public void TopHulls_AreSoldOnlyToFriends()
    {
        var a = Pilot();
        var player = Docked(a);

        _room.Buy(a, Protocol.HullItem, "heavy");
        Assert.DoesNotContain("heavy", player.Hulls);
        Assert.Equal(Protocol.NeedRepNotice, a.Last<NoticeMsg>().Code);

        SetRep(player, Place, 40);
        _room.Buy(a, Protocol.HullItem, "heavy");
        Assert.Contains("heavy", player.Hulls);
    }

    [Fact]
    public void AHero_PaysLessAndAnEnemyPaysMore()
    {
        var friend = Docked(Pilot("Alice"));
        var foe = Docked(Pilot("Bob"));
        SetRep(friend, Place, 80); // Герой: ×0.90
        SetRep(foe, Place, -80);   // Враг: ×1.10

        var before = (friend.Credits, foe.Credits);
        _room.Buy(ConnectionOf(friend), Protocol.ItemKind, "laser");
        _room.Buy(ConnectionOf(foe), Protocol.ItemKind, "laser");

        Assert.Equal(270, before.Item1 - friend.Credits); // 300 × 0.90
        Assert.Equal(330, before.Item2 - foe.Credits);    // 300 × 1.10
    }

    [Fact]
    public void RepairIsCheaperForFriends()
    {
        var a = Pilot();
        var player = Docked(a);
        player.Hp -= 100;
        SetRep(player, Place, 80);
        var before = player.Credits;

        _room.Repair(a);

        // 100 единиц по 1 кр, «Герой» — ×0.90.
        Assert.Equal(90, before - player.Credits);
    }

    [Fact]
    public void TheDistrusted_GetAShorterBoard()
    {
        var a = Pilot();
        Assert.Equal(4, a.Last<MissionsMsg>().Offers.Count);

        SetRep(PlayerOf(a), Place, -20);
        Persist(a);

        Assert.Equal(2, Rejoin(a).Last<MissionsMsg>().Offers.Count);
    }

    [Fact]
    public void FriendsGetAnEliteContract()
    {
        var a = Pilot();
        var plain = a.Last<MissionsMsg>().Offers;
        Assert.DoesNotContain(plain, o => o.Elite);

        SetRep(PlayerOf(a), Place, 40);
        Persist(a);

        var board = Rejoin(a).Last<MissionsMsg>().Offers;
        // Особый — последним: он читается как «лучшее, что тут есть», а не теряется в середине списка.
        Assert.True(board[^1].Elite);
        // Сравниваем с той же работой на обычной доске, а не с соседней строкой: доска собирается по сиду
        // пилота, и «дороже первого предложения» было бы правдой не при каждом сиде.
        Assert.Equal(plain[^1] with { Reward = board[^1].Reward, Elite = true }, board[^1]);
        Assert.Equal((int)Math.Round(plain[^1].Reward * 1.5), board[^1].Reward);
    }

    [Fact]
    public void Reputation_SurvivesLogoutAndComesBackFaded()
    {
        var a = Pilot();
        SetRep(PlayerOf(a), Place, 40);
        Persist(a);

        var profile = _accounts.Profile(_ids[a]);
        Assert.NotNull(profile!.Reputation);
        Assert.Equal(40, profile.Reputation![Place], 6);

        // Тот же профиль, но записанный трое суток назад: распад 3 очка в сутки съедает девять.
        _accounts.Save(_ids[a], profile with { RepAt = profile.RepAt - 3 * 24 * 60 * 60 });

        Assert.Equal(31, Rep(PlayerOf(Rejoin(a)), Place), 6);
    }

    [Fact]
    public void AGuest_EarnsReputationButLeavesItBehind()
    {
        var guest = new FakeConnection(++_nextConnection);
        _room.Join(guest, null, "Guest", null);
        _room.Undock(guest); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        var player = _room.Pilot(guest.Last<WelcomeMsg>().Id)!;

        SetRep(player, Sys, 20);

        Assert.Equal(20, Rep(player, Sys), 6);
        Assert.Empty(Directory.GetFiles(_dir)); // профиля нет — сохранять некуда
    }

    private FakeConnection ConnectionOf(Player player) =>
        _ids.Keys.First(c => c.Last<WelcomeMsg>().Id == player.Id);
}
