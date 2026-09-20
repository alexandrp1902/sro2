using Sro.Sim;

namespace Sro.Sim.Tests;

/// <summary>Новое снаряжение M11: ион (щит ×2, корпус ×0.3, замедление), охлаждение, ремонт и utility-слоты.</summary>
public class M11WeaponsTests
{
    private static readonly WeaponParams Ion = new(
        "Ионный разрядник Mk1", 120, 78, 1.4, 550, 750, 15, Arc: 180, Kind: "ion",
        ShieldFactor: 2, HullFactor: 0.3, Slow: 0.4, SlowSeconds: 2);

    [Fact]
    public void Ion_EatsShieldTwiceAsFastAndBarelyScratchesTheHull()
    {
        double hp = 1000, shield = 100;
        var damage = Combat.ApplyDamage(ref hp, ref shield, Ion);
        Assert.Equal(0, shield); // 100 щита съедены половиной урона
        Assert.Equal(100, damage.Shield);
        Assert.Equal(21, damage.Hull, 6); // оставшиеся 70 урона × 0.3
        Assert.Equal(979, hp, 6);
    }

    [Fact]
    public void Ion_AgainstAFullShieldTakesNothingFromTheHull()
    {
        double hp = 1000, shield = 500;
        var damage = Combat.ApplyDamage(ref hp, ref shield, Ion);
        Assert.Equal(240, damage.Shield);
        Assert.Equal(0, damage.Hull);
        Assert.Equal(260, shield);
    }

    [Fact]
    public void OrdinaryWeaponsAreUnchanged()
    {
        double hp = 1000, shield = 100;
        var damage = Combat.ApplyDamage(ref hp, ref shield, new WeaponParams("Пушка", 300, 75, 1, 500, 700, 10));
        Assert.Equal(100, damage.Shield);
        Assert.Equal(200, damage.Hull);
    }

    [Fact]
    public void Slowed_CutsSpeedAndAcceleration()
    {
        var hull = TestHulls.Light;
        var slowed = Movement.Slowed(hull, 0.4);
        Assert.Equal(hull.MaxSpeed * 0.6, slowed.MaxSpeed, 6);
        Assert.Equal(hull.Acceleration * 0.6, slowed.Acceleration, 6);
        Assert.Equal(hull.BrakeAcceleration, slowed.BrakeAcceleration); // тормозить замедление не мешает
    }

    [Fact]
    public void Cooldown_ShrinksWithCooling()
    {
        var weapon = new WeaponParams("Пушка", 100, 75, 1.0, 500, 700, 10);
        Assert.Equal(20, Combat.CooldownTicks(weapon));
        Assert.Equal(18, Combat.CooldownTicks(weapon, 0.9));
    }

    private static readonly IReadOnlyDictionary<string, ModuleParams> Modules = new Dictionary<string, ModuleParams>
    {
        ["engineS"] = new("Двигатель", Fitting.EngineSlot, EquipClass.S, Power: 5),
        ["shieldS"] = new("Щит", Fitting.ShieldSlot, EquipClass.S, Power: 10, Shield: 150, ShieldRegen: 20),
        ["radarS"] = new("Радар", Fitting.RadarSlot, EquipClass.S, Power: 5, Radar: 2000),
        ["generatorS"] = new("Генератор", Fitting.GeneratorSlot, EquipClass.S, Output: 200),
        ["repair"] = new("Ремонтный блок", Fitting.UtilityKind, EquipClass.S, Power: 10, Repair: 8),
        ["cooling"] = new("Охлаждение", Fitting.UtilityKind, EquipClass.S, Power: 12, Cooling: 0.1),
        ["cargoPod"] = new("Грузовой расширитель", Fitting.UtilityKind, EquipClass.S, Power: 4, Cargo: 10),
        ["bigPod"] = new("Тяжёлый расширитель", Fitting.UtilityKind, EquipClass.L, Power: 4, Cargo: 40),
    };

    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["pulse"] = new("Импульсная пушка", 100, 75, 1.0, 500, 700, 10, Power: 15),
    };

    private static HullParams Hull(int utility) => TestHulls.Light with { UtilitySlots = utility, Cargo = 20, Class = EquipClass.S };

    private static ShipFit Starter(params string?[] utility) =>
        new(["pulse"], "engineS", "shieldS", "radarS", "generatorS", utility);

    [Fact]
    public void UtilityModules_AddCargoRepairAndCooling()
    {
        var fit = Starter("cargoPod", "repair");
        var hull = Fitting.Effective(Hull(2), fit, Modules);
        Assert.Equal(30, hull.Cargo);
        Assert.Equal(8, Fitting.Repair(fit, Modules));
        Assert.Equal(1, Fitting.CooldownScale(fit, Modules));

        var cooled = Starter("cooling", "cooling");
        Assert.Equal(0.8, Fitting.CooldownScale(cooled, Modules), 6);
    }

    [Fact]
    public void CanInstall_ChecksUtilitySlotsAndClass()
    {
        var hull = Hull(1);
        var fit = Starter();
        Assert.Null(Fitting.CanInstall(hull, fit, "u0", "repair", Weapons, Modules));
        Assert.Equal(FitProblem.Slot, Fitting.CanInstall(hull, fit, "u1", "repair", Weapons, Modules)); // слот всего один
        Assert.Equal(FitProblem.Class, Fitting.CanInstall(hull, fit, "u0", "bigPod", Weapons, Modules)); // класс L в корпус S
        Assert.Equal(FitProblem.Slot, Fitting.CanInstall(hull, fit, "u0", "shieldS", Weapons, Modules)); // щит не вспомогательный
        Assert.Equal(FitProblem.Slot, Fitting.CanInstall(Hull(0), fit, "u0", "repair", Weapons, Modules));
    }

    [Fact]
    public void Refit_DropsUtilityThatNoLongerFits()
    {
        var removed = new List<string>();
        var fit = Fitting.Refit(Hull(1), Starter("cargoPod", "repair"), Weapons, Modules, removed);
        Assert.Equal("cargoPod", fit.Get("u0"));
        Assert.Null(fit.Get("u1"));
        Assert.Equal(["repair"], removed); // второй слот отняли — модуль на склад
    }

    [Fact]
    public void Fit_KnowsUtilitySlotsAndComparesThem()
    {
        Assert.Equal(0, Fitting.UtilityIndex("u0"));
        Assert.Equal(2, Fitting.UtilityIndex("u2"));
        Assert.Null(Fitting.UtilityIndex("u3"));
        Assert.True(Fitting.IsSlot("u1"));

        // Старый профиль без utility и новый с пустым слотом — одно и то же оснащение.
        Assert.Equal(Starter(), Starter(null, null));
        Assert.NotEqual(Starter(), Starter("repair"));
        Assert.Contains(("u0", "repair"), Starter("repair").Items());
    }

    [Fact]
    public void Power_CountsUtilityModules()
    {
        Assert.Equal(15 + 5 + 10 + 5 + 0 + 0 + 10, Fitting.Power(Starter("repair"), Weapons, Modules));
    }
}
