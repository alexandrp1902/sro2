using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// События спроса (M15.5): вспыхивают только в красной зоне, поднимают цену на просимые товары,
/// убывают вместе с квотой и кончаются — досрочно, если довезли, и молча, если не успели.
/// Просимое пропадает с прилавков всей системы: его везут из других систем (плейтест 2026-09-26).
/// </summary>
public sealed class DemandTests
{
    private const string Medicine = "medicine";
    private const string Ore = "ore";

    private static readonly LootRules Loot = new(
        StationRange: 400,
        Items: new Dictionary<string, LootItem>
        {
            [Medicine] = new("Медикаменты", Volume: 1, Price: 60),
            [Ore] = new("Руда", Volume: 1, Price: 8),
        });

    /// <summary>free — красная зона со станцией и поселением; core — безопасная: события там не бывать.</summary>
    private static readonly GalaxyRules Galaxy = new(
        // Пилоты начинают прямо в красной зоне: перелёт между системами к событию отношения не имеет.
        StartSystem: "free",
        Systems: new Dictionary<string, SystemDef>
        {
            ["core"] = new("Core", Pvp: GalaxyRules.PvpOff, Gates: [new GateDef("free", 3000, 0)]),
            ["free"] = new(
                "Free",
                Pvp: GalaxyRules.PvpFree,
                Gates: [new GateDef("core", -3000, 0)],
                Planets: [new PlanetDef("Пепел", "lava", 60, new OrbitDef(1200), Id: "ash", Settlement: new SettlementDef("Приют"))]),
        },
        Links: [new LinkDef("core", "free", 10)]);

    private static readonly MarketRules Market = new(
        Goods: new Dictionary<string, MarketGood> { [Medicine] = new(100), [Ore] = new(100) },
        Places: new Dictionary<string, MarketStation>
        {
            ["st:core"] = new(Produces: [Medicine]),
            ["st:free"] = new(Produces: [Ore]),
            ["pl:ash"] = new(Produces: [Ore]),
        });

    /// <summary>Открывается сразу, идёт минуту, квота крошечная — её видно набрать в тесте.</summary>
    private static readonly DemandRules Rules = new(
        FirstMinutes: 0,
        IntervalMinutes: 10,
        AnnounceSeconds: 1,
        DurationSeconds: 60,
        Quota: 10,
        Mul: 3,
        MulEnd: 2,
        Cases: [new DemandCase("plague", "Эпидемия", [Medicine])]);

    private readonly Galaxy _galaxy;
    private int _nextConnection;

    public DemandTests() : this(Rules) { }

    private DemandTests(DemandRules rules)
    {
        _galaxy = new Galaxy(
            TestBalance.Create(new CombatRules(ProtectionSeconds: 0, SpawnJitter: 0), loot: Loot, shop: new ShopRules(StartCredits: 1000), market: Market) with
            {
                GalaxySet = Galaxy,
                DemandSet = rules,
            },
            NullLogger.Instance, roll: () => 0, random: seed => new Random(seed));
    }

    private FakeConnection Guest(string name = "Pilot")
    {
        var connection = new FakeConnection(++_nextConnection);
        _galaxy.Join(connection, null, name, null);
        _galaxy.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private Player PlayerOf(FakeConnection connection) => _galaxy.RoomOf(connection)!.Pilot(IdOf(connection))!;

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _galaxy.Step();
    }

    /// <summary>Шагает, пока не придёт сообщение о событии в этом состоянии.</summary>
    private DemandMsg StepUntil(FakeConnection observer, string state, int maxTicks = 120 * SimConfig.TickRate)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (observer.Messages.OfType<DemandMsg>().LastOrDefault() is { } last && last.State == state) return last;
            _galaxy.Step();
        }
        throw new Xunit.Sdk.XunitException($"no demand '{state}' in {maxTicks} ticks");
    }

    /// <summary>Ставит пилота в док места и возвращает его.</summary>
    private Player Dock(FakeConnection connection, string place)
    {
        var player = PlayerOf(connection);
        var room = _galaxy.RoomOf(connection)!;
        var def = room.Balance.Place(place)!;
        var (x, y) = room.PlacePosition(def);
        player.Ship = new ShipState { X = x, Y = y };
        room.Dock(connection, true, place);
        Assert.True(connection.Last<HangarMsg>().Docked, $"pilot must be docked at {place}");
        return player;
    }

    private static MarketItemDto Quote(FakeConnection connection, string good) =>
        connection.Last<MarketMsg>().Items.Single(i => i.Id == good);

    [Fact]
    public void TheEventOnlyEverLandsInARedZone()
    {
        var a = Guest();
        var open = StepUntil(a, Protocol.DemandOpen);
        // В Ядре это был бы безопасный извоз: всё событие держится на том, что гружёный корабль там отнимут.
        Assert.Equal("free", open.System);
        Assert.Contains(open.Place, new[] { "st:free", "pl:ash" });
        Assert.Equal(Medicine, Assert.Single(open.Goods));
    }

    [Fact]
    public void ItIsAnnouncedBeforeTheDoorsOpen()
    {
        var a = Guest();
        var announce = StepUntil(a, Protocol.DemandAnnounce);
        // Объявляем заранее и говорим, что именно просят: иначе выигрывал бы тот, кто и так был рядом.
        Assert.Equal(10, announce.Quota);
        Assert.NotEmpty(announce.Goods);
        Assert.True(announce.SecondsLeft >= 0);
    }

    /// <summary>Цена штуки события на месте: обычная цена товара × множитель, минус половина спреда.</summary>
    private static int EventUnit(int left) =>
        (int)Math.Floor(60 * DemandRules.Taper(3, 2, left, 10) * (1 - Market.Spread / 2) + 1e-9);

    [Fact]
    public void OpeningPaysThreefold_AndTakesTheGoodOffTheShelf()
    {
        var a = Guest();
        var open = StepUntil(a, Protocol.DemandOpen);
        var b = Guest("Buyer");
        Dock(b, open.Place);

        var here = Quote(b, Medicine);
        var elsewhere = Quote(b, Ore);
        // Табло «×3» платит втрое от обычной цены — при любом складе места.
        Assert.Equal(EventUnit(10), here.Sell);
        Assert.False(here.Sells, "просимое на месте события не купить");
        Assert.True(elsewhere.Sell < 60, "остальных товаров событие не касается");
    }

    [Fact]
    public void SellingDrainsTheQuotaAndTapersThePrice()
    {
        var a = Guest();
        var open = StepUntil(a, Protocol.DemandOpen);
        var b = Guest("Buyer");
        var player = Dock(b, open.Place);
        player.Cargo.Add(Medicine, 4);

        var before = Quote(b, Medicine).Sell;
        _galaxy.With(b, r => r.Sell(b, Medicine, 4));
        var after = Quote(b, Medicine).Sell;

        Assert.True(after < before, $"после сдачи {after} должно быть дешевле {before}");
        // Рассылка о событии идёт раз в секунду — дожидаемся свежей, а не читаем прежнюю.
        Steps(SimConfig.TickRate + 1);
        var now = b.Messages.OfType<DemandMsg>().Last();
        Assert.Equal(6, now.Left);
        Assert.True(now.Mul < open.Mul, "множитель тает вместе с квотой");
    }

    [Fact]
    public void FillingTheQuotaEndsTheEventEarly_AndTheRestGoesAtThePlainPrice()
    {
        var a = Guest();
        var open = StepUntil(a, Protocol.DemandOpen);
        var b = Guest("Buyer");
        var player = Dock(b, open.Place);
        player.Cargo.Add(Medicine, 20);
        var credits = player.Credits;

        _galaxy.With(b, r => r.Sell(b, Medicine, 20));
        Steps(2);

        // Десять штук — по цене события, тающей со штукой; остальные десять — как в обычный день.
        var local = Market.Local(open.Place, null);
        var expected = local.Trade(Medicine, 60, local.Norm(Medicine), 10, buying: false).Credits;
        for (var left = 10; left > 0; left--) expected += EventUnit(left);
        Assert.Equal(credits + expected, player.Credits);

        var filled = a.Messages.OfType<DemandMsg>().Last();
        Assert.Equal(Protocol.DemandFilled, filled.State);
        Assert.True(Quote(b, Medicine).Sells, "событие кончилось — товар снова в продаже");
    }

    [Fact]
    public void TheWholeSystemIsShort_ButOnlyTheEventPlacePaysDear()
    {
        var a = Guest();
        var open = StepUntil(a, Protocol.DemandOpen);
        var other = open.Place == "pl:ash" ? "st:free" : "pl:ash";
        var b = Guest("Buyer");
        var player = Dock(b, other);

        // Купить у соседки и сдать за углом нельзя: просимое пропало со всех прилавков системы.
        var quote = Quote(b, Medicine);
        Assert.False(quote.Sells);
        Assert.True(quote.Sell <= 60 * 2.2 + 1, "соседка платит обычную цену");
        var demand = b.Last<MarketMsg>().Demand;
        Assert.NotNull(demand);
        Assert.False(demand!.Here);
        Assert.False(string.IsNullOrEmpty(demand.PlaceName));

        var credits = player.Credits;
        _galaxy.With(b, r => r.BuyGoods(b, Medicine, 1));
        Assert.Equal(Protocol.ShortageNotice, b.Last<NoticeMsg>().Code);
        Assert.Equal(credits, player.Credits);
        Assert.Equal(0, player.Cargo.Count(Medicine));
    }

    [Fact]
    public void TheMarketMessageCarriesTheLocalProfileAndTheDemand()
    {
        var a = Guest();
        var open = StepUntil(a, Protocol.DemandOpen);
        var b = Guest("Buyer");
        Dock(b, open.Place);

        var market = b.Last<MarketMsg>();
        Assert.NotNull(market.Station); // без профиля места клиент считал бы цену по чужой витрине
        Assert.NotNull(market.Demand);
        Assert.Equal(Medicine, Assert.Single(market.Demand!.Goods));
        Assert.Equal(10, market.Demand.Quota);
        // Клиент считает кнопку той же формулой: ему нужны оба конца множителя.
        Assert.True(market.Demand.Here);
        Assert.Equal((3.0, 2.0), (market.Demand.Start, market.Demand.End));
    }

    [Fact]
    public void WhenNobodyMadeItInTimeTheEventJustGoesOut()
    {
        var a = Guest();
        StepUntil(a, Protocol.DemandOpen);
        var over = StepUntil(a, Protocol.DemandOver, 90 * SimConfig.TickRate);

        // Ни штрафов, ни виноватых: не довезли — значит не довезли.
        Assert.Equal(10, over.Quota);
        Assert.True(over.Left > 0);
    }

    [Fact]
    public void APilotWhoJoinsMidEventIsToldAboutItAtOnce()
    {
        var a = Guest();
        StepUntil(a, Protocol.DemandOpen);
        var late = Guest("Late");
        Assert.Contains(late.Messages.OfType<DemandMsg>(), m => m.State == Protocol.DemandOpen);
    }
}
