namespace Sro.Sim.Tests;

/// <summary>
/// Флот M19: особенность корпуса, дробовик веером, гаусс-зеркало ионки, ракетный залп и четыре модуля.
/// Проверяется не тюнинг чисел (их правят на плейтесте), а обещания: особенность нельзя описать пустой,
/// потолки не обойти тремя модулями, а гаусс обязан вести себя ровно наоборот ионке.
/// </summary>
public class M19FleetTests
{
    private static Balance Shared()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        return balance!;
    }

    private static ShipFit Fit(params string?[] utility) => Fitting.Starter with { Utility = utility };

    private static WeaponParams TestWeapon() =>
        new("Тест Mk1", Damage: 100, Accuracy: 75, Cooldown: 1, OptimalRange: 400, MaxRange: 600, RangePenalty: 10);

    // --- особенность корпуса ---

    [Theory]
    [InlineData(1, 0, false, "a perk must do something")] // пустая особенность — не особенность
    [InlineData(4, 0, false, "grab")]                     // захват выше потолка
    [InlineData(1, 100, false, "scan")]                   // скан ближе радара соседа — это опечатка, а не роль
    public void APerk_RejectsNonsense(double grab, double scan, bool ram, string expected)
    {
        var error = new HullPerk(grab, scan, ram).Validate();
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void APerk_AcceptsTheThreeItDescribes()
    {
        Assert.Null(new HullPerk(Grab: 2, Ram: true).Validate());
        Assert.Null(new HullPerk(Scan: 4000).Validate());
    }

    [Fact]
    public void ABadPerk_RejectsTheWholeHull()
    {
        var hull = TestHulls.Light with { Perk = new HullPerk(Grab: 99) };
        Assert.Contains("perk:", hull.Validate());
    }

    /// <summary>«Тягач» и «Циркуль» — те самые два корпуса, ради которых поле и заводилось.</summary>
    [Fact]
    public void TheFleet_CarriesItsPerks()
    {
        var hulls = Shared().Hulls;
        Assert.Equal(2, hulls["tug"].Perk!.Grab);
        Assert.True(hulls["tug"].Perk!.Ram);
        Assert.Equal(4000, hulls["surveyor"].Perk!.Scan);
        Assert.Null(hulls["light"].Perk); // остальным особенность не раздавали
    }

    /// <summary>Все десять корпусов пачки J есть в каталоге и у всех есть цена.</summary>
    [Fact]
    public void TheFleet_IsInTheCatalogAndOnSale()
    {
        var balance = Shared();
        string[] fleet =
            ["starterTrader", "needle", "tug", "surveyor", "corsair", "clipper", "runner", "lancer", "dropship", "galleon"];
        foreach (var id in fleet)
        {
            Assert.True(balance.Hulls.ContainsKey(id), $"нет корпуса {id}");
            Assert.True(balance.Economy.HullPrices.ContainsKey(id), $"нет цены на {id}");
        }
    }

    // --- захват, скан, маскировка ---

    [Fact]
    public void AGrapple_MultipliesWithTheHullPerk()
    {
        var balance = Shared();
        var tug = balance.Hulls["tug"];
        var light = balance.Hulls["light"];

        // Корпус сам по себе — ×2, захват сам по себе — ×1.6, вместе — ×3.2.
        Assert.Equal(2, Fitting.Grab(tug, Fit(), balance.Modules), 3);
        Assert.Equal(1.6, Fitting.Grab(light, Fit("grapple"), balance.Modules), 3);
        Assert.Equal(3.2, Fitting.Grab(tug, Fit("grapple"), balance.Modules), 3);
    }

    /// <summary>Три захвата на «Тягаче» дали бы ×8: трюм наполнялся бы, не сходя с места.</summary>
    [Fact]
    public void AGrapple_StopsAtTheCap()
    {
        var balance = Shared();
        var wide = Fitting.Grab(balance.Hulls["tug"], Fit("grapple", "grapple", "grapple"), balance.Modules);
        Assert.Equal(Fitting.MaxGrab, wide, 3);
    }

    /// <summary>Корпус и модуль не складываются: две дальнозоркости — это не вдвое дальше.</summary>
    [Fact]
    public void Scan_TakesTheBetterOfHullAndModule()
    {
        var balance = Shared();
        var surveyor = balance.Hulls["surveyor"];
        var scanner = balance.Modules!["deepScanner"].Scan;
        var both = Fitting.Scan(surveyor, Fit("deepScanner"), balance.Modules);

        Assert.Equal(Math.Max(surveyor.Perk!.Scan, scanner), both, 3);
        Assert.Equal(0, Fitting.Scan(balance.Hulls["light"], Fit(), balance.Modules), 3);
    }

    [Fact]
    public void ACloak_ShrinksTheCircle_AndASecondOneAddsNothing()
    {
        var balance = Shared();
        Assert.Equal(1, Fitting.Stealth(Fit(), balance.Modules), 3);
        Assert.Equal(0.6, Fitting.Stealth(Fit("cloak"), balance.Modules), 3);
        Assert.Equal(0.6, Fitting.Stealth(Fit("cloak", "cloak"), balance.Modules), 3);
    }

    // --- бронеплиты ---

    [Fact]
    public void ArmourPlates_TradeSpeedForHull()
    {
        var balance = Shared();
        var hull = balance.Hulls["galleon"];
        var plated = Fitting.Effective(hull, Fit("armorPlate"), balance.Modules);
        var bare = Fitting.Effective(hull, Fit(), balance.Modules);

        Assert.Equal(bare.Hp * 1.15, plated.Hp, 3);
        Assert.Equal(bare.MaxSpeed * 0.95, plated.MaxSpeed, 3);
        // Разгон и торможение плиты не трогают: корабль остаётся отзывчивым, просто не разгоняется так.
        Assert.Equal(bare.Acceleration, plated.Acceleration, 3);
        Assert.Equal(bare.BrakeAcceleration, plated.BrakeAcceleration, 3);
    }

    /// <summary>
    /// Три плиты дали бы +52 % прочности — потолок срезает их до +30 %. Скорость при этом падает честно,
    /// на 0.95³, потому что до пола 0.85 три плиты ещё не достают: пол страхует от будущих модулей, не от этих.
    /// </summary>
    [Fact]
    public void ThreeArmourPlates_StopAtTheHullCap()
    {
        var balance = Shared();
        var hull = balance.Hulls["galleon"];
        var three = Fitting.Effective(hull, Fit("armorPlate", "armorPlate", "armorPlate"), balance.Modules);

        Assert.True(Math.Pow(1.15, 3) > 1 + Fitting.MaxHullBonus, "иначе потолок ничего не срезает и тест пуст");
        Assert.Equal(hull.Hp * (1 + Fitting.MaxHullBonus), three.Hp, 3);

        Assert.Equal(hull.MaxSpeed * Math.Pow(0.95, 3), three.MaxSpeed, 3);
        Assert.True(three.MaxSpeed >= hull.MaxSpeed * Fitting.MinSpeedFactor, "ниже пола скорость не падает");
    }

    /// <summary>Старший тир хуже не бывает: Mk3-плита крепче Mk1 и ровно настолько же медленная.</summary>
    [Fact]
    public void APlateGrowsWithItsTier_ButNeverGetsSlower()
    {
        var modules = Shared().Modules!;
        var mk1 = modules["armorPlate"];
        var mk3 = modules["armorPlate_mk3"];

        Assert.True(mk3.HpMul > mk1.HpMul, $"Mk3 должна быть крепче: {mk3.HpMul} против {mk1.HpMul}");
        Assert.Equal(mk1.SpeedMul, mk3.SpeedMul, 3);
    }

    // --- пушки ---

    /// <summary>Гаусс — зеркало ионки: по щиту меньше, по корпусу больше.</summary>
    [Fact]
    public void TheGauss_MirrorsTheIon()
    {
        var weapons = Shared().Weapons;
        var gauss = weapons["gauss"];
        var ion = weapons["ion"];

        Assert.True(gauss.ShieldFactor < 1 && gauss.HullFactor > 1, "гаусс: щит держит, корпус нет");
        Assert.True(ion.ShieldFactor > 1 && ion.HullFactor < 1, "ион: наоборот");

        // По голому корпусу гаусс снимает больше, чем написано в файле, — на то и множитель.
        double hp = 10000, shield = 0;
        Combat.ApplyDamage(ref hp, ref shield, gauss);
        var bare = 10000 - hp;
        Assert.True(bare > gauss.Damage, $"по корпусу должно быть больше урона, чем в файле: {bare}");

        // А по щиту той же ёмкости — меньше, чем снял бы ион того же урона.
        double ionHp = 10000, ionShield = 10000;
        double gaussHp = 10000, gaussShield = 10000;
        Combat.ApplyDamage(ref ionHp, ref ionShield, 200, ion.ShieldFactor, ion.HullFactor);
        Combat.ApplyDamage(ref gaussHp, ref gaussShield, 200, gauss.ShieldFactor, gauss.HullFactor);
        Assert.True(gaussShield > ionShield, "щит должен держать гаусс лучше, чем ионку");
    }

    [Fact]
    public void TheShotgunAndTheSalvo_DeclareTheirCounts()
    {
        var weapons = Shared().Weapons;
        Assert.Equal(5, weapons["shotgun"].Pellets);
        Assert.True(weapons["shotgun"].Spread > 0);
        Assert.Equal(4, weapons["salvo"].Salvo);
        Assert.NotNull(weapons["salvo"].Missile);
    }

    /// <summary>Урон в файле — за дробину и за ракету: тир множит его, а их число оставляет.</summary>
    [Fact]
    public void Tiers_DoNotMultiplyTheCountsTwice()
    {
        var weapons = Shared().Weapons;
        Assert.Equal(weapons["shotgun"].Pellets, weapons["shotgun_mk3"].Pellets);
        Assert.Equal(weapons["salvo"].Salvo, weapons["salvo_mk3"].Salvo);
        Assert.True(weapons["shotgun_mk3"].Damage > weapons["shotgun"].Damage);
    }

    [Theory]
    [InlineData(0)]  // ноль дробин — пушка, которая не стреляет
    [InlineData(99)] // и веер на сто дробин тоже не нужен
    public void Pellets_AreBounded(int pellets)
    {
        Assert.Contains("pellets", (TestWeapon() with { Pellets = pellets }).Validate());
    }

    [Fact]
    public void ASalvo_NeedsMissiles_AndPelletsDoNotMixWithThem()
    {
        Assert.Contains("salvo needs a missile block", (TestWeapon() with { Salvo = 4 }).Validate());
        Assert.Contains("spread without pellets", (TestWeapon() with { Spread = 10 }).Validate());

        var launcher = TestWeapon() with { Kind = WeaponParams.MissileKind, Missile = new MissileParams(), Pellets = 3 };
        Assert.Contains("pellets", launcher.Validate());
    }

    /// <summary>Новинки — только вспомогательные модули: у двигателя и щита свои роли.</summary>
    [Fact]
    public void TheNewFields_BelongToUtilityModules()
    {
        var engine = new ModuleParams("Двигатель", Fitting.EngineSlot, HpMul: 1.2);
        Assert.Contains("belong to a utility module", engine.Validate());
    }
}
