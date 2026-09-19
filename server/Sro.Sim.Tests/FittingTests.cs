using System.Text.Json;

namespace Sro.Sim.Tests;

/// <summary>Оснащение (GDD §11–13, §18–20): слоты корпуса, классы, энергия генератора, корпус с модулями.</summary>
public class FittingTests
{
    private static readonly HullParams Light = TestHulls.Light with { Class = EquipClass.S, WeaponSlots = [EquipClass.S, EquipClass.S] };
    private static readonly HullParams Heavy = TestHulls.Heavy with { Class = EquipClass.L, WeaponSlots = [EquipClass.L, EquipClass.M] };

    private static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["pulse"] = TestWeapons.Pulse with { Class = EquipClass.S, Power = 15 },
        ["plasma"] = TestWeapons.Plasma with { Class = EquipClass.M, Power = 30 },
        ["big"] = TestWeapons.Pulse with { Class = EquipClass.L, Power = 50 },
    };

    private static readonly IReadOnlyDictionary<string, ModuleParams> Modules = new Dictionary<string, ModuleParams>
    {
        ["engineS"] = new("Двигатель S", Fitting.EngineSlot, Power: 5),
        ["engineL"] = new("Двигатель L", Fitting.EngineSlot, EquipClass.L, Power: 15, Speed: 1.2, Accel: 1.5),
        ["shieldS"] = new("Щит S", Fitting.ShieldSlot, Power: 10, Shield: 150, ShieldRegen: 20),
        ["radarS"] = new("Радар S", Fitting.RadarSlot, Power: 5, Radar: 2000),
        ["tankS"] = new("Бак S", Fitting.TankSlot, Fuel: 100),
        ["generatorS"] = new("Генератор S", Fitting.GeneratorSlot, Output: 60),
        ["generatorL"] = new("Генератор L", Fitting.GeneratorSlot, EquipClass.L, Output: 200),
    };

    [Fact]
    public void Effective_TakesShieldRadarAndTankFromModules_AndScalesTheEngine()
    {
        var fit = Fitting.Starter.With(Fitting.EngineSlot, "engineL");

        var hull = Fitting.Effective(Heavy, fit, Modules);

        Assert.Equal(Heavy.MaxSpeed * 1.2, hull.MaxSpeed, 9);
        Assert.Equal(Heavy.Acceleration * 1.5, hull.Acceleration, 9);
        Assert.Equal(Heavy.BrakeAcceleration * 1.5, hull.BrakeAcceleration, 9);
        Assert.Equal(Heavy.TurnRate, hull.TurnRate); // поворот двигатель не меняет
        Assert.Equal((150, 20, 2000, 100), (hull.Shield, hull.ShieldRegen, hull.Radar, hull.Fuel));
        Assert.Equal(Heavy.Hp, hull.Hp);
    }

    [Fact]
    public void Effective_WithoutModules_IsTheHull()
    {
        Assert.Same(Light, Fitting.Effective(Light, Fitting.Starter, null));
        // Щит сняли — щита нет, бак сняли — топлива нет.
        var bare = Fitting.Effective(Light, Fitting.Starter.With(Fitting.ShieldSlot, null).With(Fitting.TankSlot, null), Modules);
        Assert.Equal((0, 0), (bare.Shield, bare.Fuel));
    }

    [Fact]
    public void Power_SumsWeaponsAndModules()
    {
        var fit = Fitting.Starter.With("w1", "pulse");

        Assert.Equal(15 + 15 + 5 + 10 + 5, Fitting.Power(fit, Weapons, Modules));
        Assert.Equal(60, Fitting.Output(fit, Modules));
        Assert.Equal(double.PositiveInfinity, Fitting.Output(fit, null));
    }

    [Theory]
    [InlineData("w1", "pulse", null)]
    [InlineData("w2", "pulse", FitProblem.Slot)] // у лёгкого два слота
    [InlineData("w0", "plasma", FitProblem.Class)] // M в слот S
    [InlineData("w1", "ghost", FitProblem.Slot)]
    [InlineData(Fitting.EngineSlot, "engineL", FitProblem.Class)] // L на корпус S
    [InlineData(Fitting.EngineSlot, "shieldS", FitProblem.Slot)] // щит — не двигатель
    [InlineData(Fitting.EngineSlot, null, FitProblem.Required)]
    [InlineData(Fitting.ShieldSlot, null, null)]
    [InlineData("cockpit", "pulse", FitProblem.Slot)]
    public void CanInstall_ChecksSlotAndClass(string slot, string? id, string? problem)
    {
        Assert.Equal(problem, Fitting.CanInstall(Light, Fitting.Starter, slot, id, Weapons, Modules));
    }

    [Fact]
    public void CanInstall_ChecksThePower_CountingWhatLeavesTheSlot()
    {
        var weapons = new Dictionary<string, WeaponParams>(Weapons) { ["hungry"] = TestWeapons.Pulse with { Power = 45 } };
        // Стартовое берёт 35 из 60: ещё 45 не влезает (80), и вместо пушки тоже (35 − 15 + 45 = 65).
        Assert.Equal(FitProblem.Power, Fitting.CanInstall(Light, Fitting.Starter, "w1", "hungry", weapons, Modules));
        Assert.Equal(FitProblem.Power, Fitting.CanInstall(Light, Fitting.Starter, "w0", "hungry", weapons, Modules));
        // Без щита освобождается 10: 25 − 15 + 45 = 55 — влезает.
        var noShield = Fitting.Starter.With(Fitting.ShieldSlot, null);
        Assert.Null(Fitting.CanInstall(Light, noShield, "w0", "hungry", weapons, Modules));
        // Больший генератор решает всё — если корпус его держит.
        Assert.Null(Fitting.CanInstall(Heavy, Fitting.Starter.With(Fitting.GeneratorSlot, "generatorL"), "w1", "hungry", weapons, Modules));
    }

    [Fact]
    public void Refit_ToASmallerHull_StoresWhatDoesNotFit()
    {
        var removed = new List<string>();
        var fit = Fitting.Starter with { Weapons = ["big", "plasma"] };
        fit = fit.With(Fitting.EngineSlot, "engineL").With(Fitting.GeneratorSlot, "generatorL");

        var light = Fitting.Refit(Light, fit, Weapons, Modules, removed);

        Assert.Equal([null, null], light.Weapons); // обе пушки старше слотов лёгкого
        Assert.Equal(("engineS", "generatorS"), (light.Engine, light.Generator)); // обязательные — стартовые взамен
        Assert.Equal(["big", "plasma", "engineL", "generatorL"], removed);
    }

    [Fact]
    public void Refit_DropsGunsFromTheLastSlot_WhenPowerRunsOut()
    {
        var weapons = new Dictionary<string, WeaponParams>(Weapons) { ["hungry"] = TestWeapons.Pulse with { Power = 30 } };
        var removed = new List<string>();

        var fit = Fitting.Refit(Light, Fitting.Starter with { Weapons = ["hungry", "hungry"] }, weapons, Modules, removed);

        Assert.Equal(["hungry", null], fit.Weapons);
        Assert.Equal(["hungry"], removed);
    }

    [Fact]
    public void Refit_ForgetsUnknownItems_WithoutStoringThem()
    {
        var removed = new List<string>();
        var fit = Fitting.Refit(Light, Fitting.Starter with { Weapons = ["ghost"], Shield = "ghostShield" }, Weapons, Modules, removed);

        Assert.Equal([null, null], fit.Weapons);
        Assert.Null(fit.Shield);
        Assert.Empty(removed);
    }

    [Fact]
    public void Fit_WithAndGet_AddressSlotsByName()
    {
        var fit = new ShipFit([]).With("w2", "pulse").With(Fitting.TankSlot, "tankS");

        Assert.Equal([null, null, "pulse"], fit.Weapons);
        Assert.Equal("pulse", fit.Get("w2"));
        Assert.Null(fit.Get("w5"));
        Assert.Equal("tankS", fit.Get(Fitting.TankSlot));
        Assert.Equal([("w2", "pulse"), (Fitting.TankSlot, "tankS")], fit.Items());
        Assert.Equal(fit, new ShipFit([null, null, "pulse"], Tank: "tankS")); // сравнение по значению, а не по ссылке на список
    }

    [Theory]
    [InlineData("w0", 0)]
    [InlineData("w5", 5)]
    [InlineData("w6", null)]
    [InlineData("w", null)]
    [InlineData("engine", null)]
    public void WeaponIndex_ParsesSlotNames(string slot, int? index) => Assert.Equal(index, Fitting.WeaponIndex(slot));

    [Fact]
    public void SharedModules_AreValid_AndTheStarterKitFlies()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        var modules = balance!.Modules!;
        foreach (var slot in Fitting.ModuleSlots)
        {
            // По три модуля каждого вида — S, M и L (GDD §59).
            var classes = modules.Values.Where(m => m.Slot == slot).Select(m => m.Class).OrderBy(EquipClass.Rank).ToList();
            Assert.Equal([EquipClass.S, EquipClass.M, EquipClass.L], classes);
        }
        Assert.Equal(6, balance.Weapons.Count); // шесть пушек (GDD §59)
        var light = balance.Hulls[SimConfig.DefaultHull];
        var starter = Fitting.Effective(light, Fitting.Starter, modules);
        Assert.True(starter.Shield > 0 && starter.Fuel > 0 && starter.Radar > 0);
        // Второй стартовой пушке место и энергия есть: первая покупка — вторая пушка.
        Assert.Null(Fitting.CanInstall(light, Fitting.Starter, "w1", SimConfig.DefaultWeapon, balance.Weapons, modules));
    }

    [Fact]
    public void Balance_RejectsAStarterKitThatDoesNotFit()
    {
        var sources = TestHulls.SharedSources();
        var modules = sources.Modules!.Replace("\"output\": 60", "\"output\": 10");

        Assert.False(Balance.TryParse(sources with { Modules = modules }, out _, out var error));
        Assert.StartsWith(Balance.ModulesFile, error);
    }

    [Theory]
    [InlineData("""{ "engineS": { "name": "x", "slot": "wing" } }""")]
    [InlineData("""{ "engineS": { "name": "x", "slot": "engine", "class": "XL" } }""")]
    [InlineData("""{ "engineS": { "name": "x", "slot": "engine", "speed": 0 } }""")]
    [InlineData("""{ "shieldS": { "name": "x", "slot": "shield" } }""")] // нет стартового двигателя
    public void ModuleCatalog_RejectsBrokenFiles(string json)
    {
        Assert.False(ModuleCatalog.TryParse(json, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Missile_TurnsTowardsTheTarget_AndHitsIt()
    {
        var p = new MissileParams(Speed: 300, TurnRate: 180, Lifetime: 5, HitRadius: 10);
        var m = new MissileState { X = 0, Y = 0, Rot = 0 }; // нос вверх, цель справа
        var ticks = 0;
        while (!Missiles.Hits(m, p, 400, 0, 20) && ticks < p.LifetimeTicks)
        {
            Missiles.Step(ref m, p, 400, 0, SimConfig.Dt);
            ticks++;
        }
        Assert.True(ticks < p.LifetimeTicks, "missile never reached the target");
        Assert.InRange(m.Rot, Math.PI / 4, Math.PI * 0.75); // довернула вправо
    }

    [Fact]
    public void Missile_TurnsNoFasterThanItsTurnRate()
    {
        var p = new MissileParams(Speed: 300, TurnRate: 90);
        var m = new MissileState();

        Missiles.Step(ref m, p, 0, 1000, 1); // цель строго позади

        Assert.Equal(Math.PI / 2, Math.Abs(m.Rot), 9);
        Assert.Equal(300, Math.Sqrt(m.X * m.X + m.Y * m.Y), 9);
    }

    [Fact]
    public void WeaponCatalog_RequiresTheMissileBlockForKindMissile()
    {
        Assert.NotNull((TestWeapons.Pulse with { Kind = WeaponParams.MissileKind }).Validate());
        Assert.NotNull((TestWeapons.Pulse with { Missile = new MissileParams() }).Validate());
        Assert.Null((TestWeapons.Pulse with { Kind = WeaponParams.MissileKind, Missile = new MissileParams() }).Validate());
        Assert.NotNull((TestWeapons.Pulse with { Class = "XL" }).Validate());
    }

    [Fact]
    public void HullSlots_MustNotBeAboveTheHullClass()
    {
        Assert.NotNull((Light with { WeaponSlots = [EquipClass.M] }).Validate());
        Assert.NotNull((Light with { WeaponSlots = [] }).Validate());
        Assert.Null(Light.Validate());
        Assert.Equal([EquipClass.L], TestHulls.Light.Slots); // старый корпус без слотов — один слот своего класса
    }

    // ── Общий эталон с клиентом ──────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions VectorJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record VectorCase(string Hull, ShipFit Fit, HullParams Effective, double Power, double Output);

    private sealed record VectorFile(
        IReadOnlyDictionary<string, HullParams> Hulls,
        IReadOnlyDictionary<string, WeaponParams> Weapons,
        IReadOnlyDictionary<string, ModuleParams> Modules,
        IReadOnlyList<VectorCase> Cases);

    /// <summary>Корпус с модулями и энергия — те же на клиенте (client/src/sim/fitting.test.ts).</summary>
    [Fact]
    public void EffectiveHullMatchesSharedVectors()
    {
        var hulls = new Dictionary<string, HullParams> { ["light"] = Light, ["heavy"] = Heavy };
        var fits = new (string Hull, ShipFit Fit)[]
        {
            ("light", Fitting.Starter),
            ("light", Fitting.Starter.With("w1", "pulse").With(Fitting.ShieldSlot, null)),
            ("heavy", (Fitting.Starter with { Weapons = ["big", "plasma"] }).With(Fitting.EngineSlot, "engineL").With(Fitting.GeneratorSlot, "generatorL")),
            ("heavy", new ShipFit([null], Engine: "engineS", Radar: "radarS", Generator: "generatorS")),
        };
        var generated = new VectorFile(
            hulls, Weapons, Modules,
            [.. fits.Select(f => new VectorCase(
                f.Hull, f.Fit, Fitting.Effective(hulls[f.Hull], f.Fit, Modules),
                Fitting.Power(f.Fit, Weapons, Modules), Fitting.Output(f.Fit, Modules)))]);
        var text = JsonSerializer.Serialize(generated, VectorJson).ReplaceLineEndings("\n") + "\n";

        var path = Path.Combine(TestHulls.RepoRoot(), "shared", "test-vectors", "fitting.json");
        if (Environment.GetEnvironmentVariable("SRO_UPDATE_VECTORS") == "1")
        {
            File.WriteAllText(path, text);
            return;
        }
        Assert.True(File.Exists(path), $"{path} is missing: run 'SRO_UPDATE_VECTORS=1 dotnet test'");
        Assert.Equal(text, File.ReadAllText(path).ReplaceLineEndings("\n"));
    }
}
