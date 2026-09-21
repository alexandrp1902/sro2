namespace Sro.Sim.Tests;

/// <summary>
/// Начало игры на настоящем балансе из shared/ (M16a). Здесь проверяется не формула, а обещание:
/// новичок на «Пчеле» со стартовым комплектом должен справляться с рейнджером первого уровня и иметь,
/// на что потратить первые кредиты. До M16a не выходило ни то, ни другое: рейнджер любого уровня летал
/// на «Страннике» и убивал новичка раньше, чем тот его, а в Ядре ему не продавали ни одного модуля,
/// который встал бы на стартовый корпус.
/// </summary>
public class StarterBalanceTests
{
    private static Balance Shared()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        return balance!;
    }

    /// <summary>Корабль в бою: чем его бьют и сколько он держит.</summary>
    private sealed record Fighter(string Name, double Ehp, double Evasion, IReadOnlyList<WeaponParams> Weapons);

    /// <summary>Новый пилот: стартовый корпус и стартовый комплект, один ствол из двух занят.</summary>
    private static Fighter Starter(Balance balance)
    {
        var hull = balance.Hulls[SimConfig.DefaultHull];
        var fit = Fitting.Starter;
        var effective = Fitting.Effective(hull, fit, balance.Modules);
        var weapons = fit.Weapons
            .Where(id => id is not null)
            .Select(id => balance.Weapons[id!])
            .ToList();
        return new Fighter(hull.Name, effective.Hp + effective.Shield, Combat.Evasion(effective, 0), weapons);
    }

    /// <summary>NPC этого типа и уровня — ровно так, как его создаст комната.</summary>
    private static Fighter Npc(Balance balance, string typeId, int level)
    {
        var npcs = balance.Npc;
        var type = npcs.TypeMap[typeId];
        var hull = balance.Hulls[NpcRules.HullOf(type, level)];
        var weapons = type.WeaponList.Select(id => npcs.ScaledWeapon(type, level, balance.Weapons[id])).ToList();
        return new Fighter(
            NpcRules.Name(type, level),
            npcs.MaxHp(type, level, hull) + npcs.MaxShield(type, level, hull),
            Combat.Evasion(hull, 0),
            weapons);
    }

    /// <summary>
    /// Урон в секунду по такой цели с такой дистанции. Восстановление щита не учитывается: под непрерывным
    /// огнём оно всё равно не работает, а счёт без него одинаково занижен у обеих сторон.
    /// </summary>
    private static double Dps(Fighter shooter, Fighter target, double distance)
    {
        var dps = 0.0;
        foreach (var weapon in shooter.Weapons)
        {
            if (!Combat.InRange(weapon, distance)) continue;
            dps += weapon.Damage * Combat.HitChance(weapon, distance, target.Evasion) / 100 / weapon.Cooldown;
        }
        return dps;
    }

    /// <summary>Сколько секунд шкала цели держится под этим огнём; бесконечность — не пробивается вовсе.</summary>
    private static double Ttk(Fighter shooter, Fighter target, double distance)
    {
        var dps = Dps(shooter, target, distance);
        return dps > 0 ? target.Ehp / dps : double.PositiveInfinity;
    }

    /// <summary>Дистанция боя рейнджера: он сам её держит (npcs.json holdRange).</summary>
    private static double HoldRange(Balance balance, string typeId) => balance.Npc.TypeMap[typeId].HoldRange;

    /// <summary>
    /// Один на один новичок выигрывает, и выигрывает с запасом: по заданию рейнджер первого уровня
    /// должен быть «потенциально побеждаемым начинающим игроком сопоставимого уровня».
    /// </summary>
    [Fact]
    public void AFreshPilot_BeatsALevelOneRanger_WithRoomToSpare()
    {
        var balance = Shared();
        var pilot = Starter(balance);
        var ranger = Npc(balance, "ranger", 1);
        var range = HoldRange(balance, "ranger");

        var killsRanger = Ttk(pilot, ranger, range);
        var killsPilot = Ttk(ranger, pilot, range);

        Assert.True(
            killsRanger < killsPilot,
            $"новичок должен успевать первым: {pilot.Name} тратит {killsRanger:0.0} с, {ranger.Name} — {killsPilot:0.0} с");
        // Треть шкалы в запасе: победа впритык — это не «побеждаемый», а «повезло».
        Assert.True(
            killsRanger < killsPilot * 0.66,
            $"и с запасом: {killsRanger:0.0} с против {killsPilot:0.0} с");
    }

    /// <summary>
    /// Но патруль остаётся угрозой: вдвоём рейнджеры новичка бьют. Иначе ослабление превратило бы
    /// первый уровень в мишень, а лететь на пост рейнджеров стало бы выгодно.
    /// </summary>
    [Fact]
    public void TwoLevelOneRangers_StillBeatAFreshPilot()
    {
        var balance = Shared();
        var pilot = Starter(balance);
        var ranger = Npc(balance, "ranger", 1);
        var range = HoldRange(balance, "ranger");

        // Пилот бьёт их по очереди, они его — вдвоём.
        var killsBoth = 2 * Ttk(pilot, ranger, range);
        var killsPilot = Ttk(ranger, pilot, range) / 2;

        Assert.True(killsPilot < killsBoth, $"двое должны брать верх: {killsPilot:0.0} с против {killsBoth:0.0} с");
    }

    /// <summary>Старший рейнджер по-прежнему не для новичка: срезан первый уровень, а не весь ряд.</summary>
    [Fact]
    public void ARangerGrows_WithItsLevel()
    {
        var balance = Shared();
        var pilot = Starter(balance);
        var range = HoldRange(balance, "ranger");

        var previous = 0.0;
        for (var level = 1; level <= 4; level++)
        {
            var ranger = Npc(balance, "ranger", level);
            var toughness = Ttk(pilot, ranger, range);
            Assert.True(toughness > previous, $"Ур.{level} должен быть крепче предыдущего: {toughness:0.0} с");
            previous = toughness;
        }
        var veteran = Npc(balance, "ranger", 4);
        Assert.True(
            Ttk(pilot, veteran, range) > Ttk(veteran, pilot, range),
            "рейнджер четвёртого уровня новичку не по зубам — так и должно быть");
    }

    /// <summary>Торговец — грузовик, а не боец: новичок обязан справляться с ним ещё легче, чем с рейнджером.</summary>
    [Fact]
    public void ALevelOneTrader_IsWeakerThanARanger()
    {
        var balance = Shared();
        var pilot = Starter(balance);
        var trader = Npc(balance, "trader", 1);
        var ranger = Npc(balance, "ranger", 1);

        Assert.True(Dps(trader, pilot, 400) < Dps(ranger, pilot, 400), "торговец не должен бить больнее рейнджера");
        var hull = balance.Hulls[NpcRules.HullOf(balance.Npc.TypeMap["trader"], 1)];
        Assert.True(hull.Cargo >= balance.Hulls[SimConfig.DefaultHull].Cargo, "у торговца должен быть грузовой корпус");
    }

    /// <summary>
    /// Стартовому корпусу есть куда расти (задание, п. 12). Раньше на «Пчелу» класса S не вставал
    /// ни один модуль из ассортимента Ядра, кроме тех же, что на ней уже стоят: все старшие были класса M.
    /// </summary>
    [Fact]
    public void TheStarterHull_HasSomethingToUpgrade_InEveryRegion()
    {
        var balance = Shared();
        var hull = balance.Hulls[SimConfig.DefaultHull];
        var starter = Fitting.Starter;

        foreach (var place in balance.Places)
        {
            var shop = balance.ShopAt(place.Key);
            // Stock null — здесь продают всё, что в прайсе (магазин без регионов).
            var stock = shop.Stock ?? [.. shop.ItemPrices.Keys];
            Assert.Contains(stock, id => Upgrades(balance, hull, starter, id));
        }
    }

    /// <summary>
    /// Встаёт ли это на стартовый корабль и делает ли его сильнее: модуль того же слота с лучшими
    /// характеристиками либо пушка мощнее стартовой. Тот же модуль, что уже стоит, за улучшение не идёт.
    /// </summary>
    private static bool Upgrades(Balance balance, HullParams hull, ShipFit fit, string id)
    {
        if (balance.Weapons.TryGetValue(id, out var weapon))
        {
            if (!EquipClass.Fits(weapon.Class, hull.Class)) return false;
            var start = balance.Weapons[SimConfig.DefaultWeapon];
            return weapon.Damage / weapon.Cooldown > start.Damage / start.Cooldown;
        }
        if (balance.Modules?.TryGetValue(id, out var module) != true || module is null) return false;
        if (!EquipClass.Fits(module.Class, hull.Class)) return false;
        var current = module.Slot switch
        {
            Fitting.EngineSlot => fit.Engine,
            Fitting.ShieldSlot => fit.Shield,
            Fitting.RadarSlot => fit.Radar,
            Fitting.GeneratorSlot => fit.Generator,
            _ => null, // вспомогательный слот у новичка пуст — туда годится что угодно
        };
        if (current is null) return true;
        if (current == id) return false;
        var now = balance.Modules[current];
        return module.Shield > now.Shield
            || module.Radar > now.Radar
            || module.Output > now.Output
            || module.Speed > now.Speed
            || module.Accel > now.Accel;
    }
}
