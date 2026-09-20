using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Выбор пути при заведении аккаунта (M15.5): рейнджер — ровно сегодняшний старт, торговец начинает
/// в другом месте, с грузовым корпусом и товаром в трюме. Путь берётся только у нового аккаунта,
/// запертый отклоняется, а профиль без пути читается как рейнджер.
/// </summary>
public sealed class CareerTests : IDisposable
{
    private const string Password = "secret";

    private static readonly LootRules Loot = new(
        Items: new Dictionary<string, LootItem>
        {
            ["metal"] = new("Металл", Volume: 1, Price: 10),
            ["food"] = new("Продовольствие", Volume: 1, Price: 30),
        });

    private static readonly ShopRules Shop = new(StartCredits: 1000);

    private static readonly IReadOnlyDictionary<string, ModuleParams> Modules = new Dictionary<string, ModuleParams>
    {
        ["engineS"] = new("Двигатель", Fitting.EngineSlot, Power: 5),
        ["shieldS"] = new("Щит", Fitting.ShieldSlot, Power: 10, Shield: 150, ShieldRegen: 20),
        ["radarS"] = new("Радар", Fitting.RadarSlot, Power: 5, Radar: TestBalance.Radar),
        ["tankS"] = new("Бак", Fitting.TankSlot, Fuel: 100),
        ["generatorS"] = new("Генератор", Fitting.GeneratorSlot, Output: 300),
    };

    /// <summary>Рейнджер — стартовый набор без правок; торговец — «тяжёлый» корпус, меньше денег и груз в трюме.</summary>
    private static readonly CareerRules Careers = new(
        Default: "ranger",
        Careers: new Dictionary<string, CareerDef>
        {
            ["ranger"] = new("Рейнджер", "Боевой корабль", Hull: "light", Weapons: ["pulse"], Credits: 1000),
            ["trader"] = new(
                "Торговец", "Грузовой трюм",
                Hull: "heavy", Weapons: ["pulse"], Credits: 500,
                Cargo: new Dictionary<string, int> { ["food"] = 40 },
                Rep: new Dictionary<string, double> { ["st:test"] = 15 }),
            ["pirate"] = new("Пират", "Скоро", Enabled: false),
        });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-career-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly Room _room;
    private int _nextConnection;

    public CareerTests()
    {
        _accounts = new AccountStore(_dir, NullLogger.Instance, iterations: 1000, autoFlush: false);
        _room = NewRoom();
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Room NewRoom() => new(
        TestBalance.Create(new CombatRules(ProtectionSeconds: 0, SpawnJitter: 0), loot: Loot, shop: Shop, reputation: TestBalance.Reputation()) with
        {
            Modules = Modules,
            CareerSet = Careers,
        },
        NullLogger.Instance,
        accounts: _accounts);

    private (FakeConnection Connection, string Id) Login(string name)
    {
        var login = _accounts.Login(name, Password);
        Assert.True(login.Ok);
        return (new FakeConnection(++_nextConnection), login.Id);
    }

    private FakeConnection Pilot(string name, string? career)
    {
        var (connection, id) = Login(name);
        _room.JoinAccount(connection, id, name, career);
        return connection;
    }

    private Player PlayerOf(FakeConnection connection) => _room.Pilot(connection.Last<WelcomeMsg>().Id)!;

    [Fact]
    public void ARangerStartsExactlyAsPilotsDidBefore()
    {
        var a = Pilot("Alice", "ranger");
        var hangar = a.Last<HangarMsg>();

        Assert.Equal(1000, a.Last<CargoMsg>().Credits);
        Assert.Equal(("light", "pulse"), (hangar.Hull, hangar.Fit.Weapons[0]));
        Assert.Empty(a.Last<CargoMsg>().Items);
    }

    [Fact]
    public void ATraderGetsTheCargoHull_TheGoodsAndItsOwnPlace()
    {
        var a = Pilot("Bob", "trader");
        var hangar = a.Last<HangarMsg>();
        var cargo = a.Last<CargoMsg>();

        Assert.Equal("heavy", hangar.Hull);
        Assert.Equal(500, cargo.Credits);
        // Часть капитала уже в товаре: везти есть что с первой минуты.
        Assert.Equal(40, cargo.Items["food"]);
        Assert.Equal("trader", PlayerOf(a).Career);
    }

    [Fact]
    public void TheWorldAlreadyKnowsTheTrader_ButOnlyAfterTheDecayWasApplied()
    {
        var a = Pilot("Cora", "trader");
        Assert.Equal(15, PlayerOf(a).Rep.Values["st:test"], 6);
        // Прибавка не должна показаться лентой как событие: пилот ничего ещё не сделал.
        Assert.DoesNotContain(a.Messages.OfType<RepMsg>(), m => m.Change is not null);
    }

    [Fact]
    public void NoCareerAtAll_ReadsAsTheDefaultOne()
    {
        var a = Pilot("Dana", null);
        Assert.Equal("light", a.Last<HangarMsg>().Hull);
        Assert.Equal(1000, a.Last<CargoMsg>().Credits);
        Assert.Equal("ranger", PlayerOf(a).Career);
    }

    [Fact]
    public void ACareerIsIgnoredForAPilotWhoAlreadyHasAProfile()
    {
        var (first, id) = Login("Erik");
        _room.JoinAccount(first, id, "Erik", "trader");
        Assert.Equal("heavy", first.Last<HangarMsg>().Hull);
        _room.Disconnect(first);

        // Вернувшийся пилот путь не меняет: он уже записан в профиле.
        var second = new FakeConnection(++_nextConnection);
        _room.JoinAccount(second, id, "Erik", "ranger");
        Assert.Equal("heavy", second.Last<HangarMsg>().Hull);
        Assert.Equal("trader", PlayerOf(second).Career);
    }

    [Fact]
    public void ACareerSurvivesARestart()
    {
        var (first, id) = Login("Fox");
        _room.JoinAccount(first, id, "Fox", "trader");
        Assert.Equal("trader", PlayerOf(first).Career);
        _room.Disconnect(first);

        // Профиль уезжает в память AccountStore целиком: путь и дом — соседние строковые поля,
        // и перепутать их местами нельзя.
        var profile = _accounts.Profile(id)!;
        Assert.Equal("trader", profile.Career);
        Assert.NotEqual(profile.Career, profile.Place);
    }

    [Fact]
    public void ALockedCareerIsNotPlayable()
    {
        // Серой кнопки в клиенте мало: отказывает сервер, и делает это до того, как заведёт аккаунт.
        Assert.False(Careers.IsPlayable("pirate"));
        Assert.False(Careers.IsPlayable("nobody"));
        Assert.True(Careers.IsPlayable("trader"));
    }

    [Fact]
    public void AnUnknownCareerFallsBackToTheDefault_RatherThanBreakingTheStart()
    {
        var a = Pilot("Gena", "nobody");
        Assert.Equal("light", a.Last<HangarMsg>().Hull);
        Assert.Equal(1000, a.Last<CargoMsg>().Credits);
    }
}
