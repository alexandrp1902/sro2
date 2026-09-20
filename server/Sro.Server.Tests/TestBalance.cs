using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>Баланс для тестов комнаты. Не читается из shared/, чтобы тюнинг не ломал тесты.</summary>
internal static class TestBalance
{
    /// <summary>Радар тестовых корпусов видит всю систему: отсечение радаром проверяют отдельно, на своих корпусах.</summary>
    public const double Radar = 100_000;

    public static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams>
    {
        // Трюмы нарочно разные: на них проверяется перегруз при смене корпуса на меньший.
        ["light"] = new("Лёгкий", 165, 180, 220, 150, 0.65, 0, 16, Hp: 400, Shield: 150, ShieldRegen: 20, Evasion: 25, MoveEvasion: 8, Cargo: 20, Fuel: 100, Radar: Radar),
        ["heavy"] = new("Тяжёлый", 85, 70, 80, 55, 1.5, 0, 30, Hp: 1800, Shield: 500, ShieldRegen: 50, Evasion: 5, MoveEvasion: 2, Cargo: 60, Fuel: 300, Radar: Radar),
    };

    public static readonly IReadOnlyDictionary<string, WeaponParams> Weapons = new Dictionary<string, WeaponParams>
    {
        ["pulse"] = new("Импульсная пушка Mk1", 100, 75, 1.0, 500, 700, 10),
        ["laser"] = new("Лазер Mk1", 40, 90, 0.5, 400, 600, 20, Kind: "beam"),
        ["plasma"] = new("Плазма Mk1", 280, 60, 2.0, 450, 650, 15, Kind: "orb"),
        // Бьёт во все стороны — как пушки в shared/weapons.json с arc 180.
        ["turret"] = new("Турель", 100, 75, 1.0, 500, 700, 10, Arc: 180),
        // Площадная (M15.5): взрыв в точке попадания задевает соседей в радиусе 200.
        ["blast"] = new("Площадная", 100, 75, 1.0, 500, 700, 10, Arc: 180, BlastRadius: 200, BlastShare: 0.5),
        // Убивает с одного попадания — для тестов уничтожения и респауна.
        ["doom"] = new("Тестовая пушка", 100_000, 100, 1.0, 500, 700, 0),
    };

    /// <summary>Без дронов, пиратов, лута, метеоритов и разброса спауна — корабли появляются ровно в SpawnX, SpawnY.</summary>
    /// <param name="market">Рынок товаров (M12); null — цены плоские, как до M12.</param>
    public static Balance Create(
        CombatRules? rules = null,
        NpcRules? npcs = null,
        LootRules? loot = null,
        MeteorRules? meteors = null,
        ShopRules? shop = null,
        MarketRules? market = null,
        ReputationRules? reputation = null,
        MissionRules? missions = null) =>
        new(Hulls, Weapons, rules ?? new CombatRules(SpawnJitter: 0), npcs, loot, meteors, shop,
            MissionSet: missions, MarketSet: market, ReputationSet: reputation);

    /// <summary>
    /// Доска из одних «собрать»: ей нужен только предмет из loot.json, без пиратов и соседних станций.
    /// Этого хватает, чтобы проверить размер доски и особый контракт, не поднимая всю галактику.
    /// </summary>
    public static MissionRules Missions(string item = "metal") => new(
        Offers: 4,
        Collect: [new CollectTemplate(item, Min: 2, Max: 4)]);

    /// <summary>
    /// Шкала репутации для тестов: пять ступеней, ±10 % на цены, Mk3 и «cruiser» только друзьям,
    /// доска ужимается недоверенным. Числа свои, чтобы тюнинг reputation.json не ронял тесты.
    /// </summary>
    public static ReputationRules Reputation(double decayPerDay = 3) => new(
        Limit: 100,
        Levels:
        [
            new RepLevel("enemy", "Враг", -100, 1.10),
            new RepLevel("distrust", "Недоверие", -50, 1.05),
            new RepLevel("neutral", "Нейтрал", -10),
            new RepLevel("friend", "Друг", 30, 0.95),
            new RepLevel("hero", "Герой", 70, 0.90),
        ],
        DecayPerDay: decayPerDay,
        Events: new RepEvents(
            MissionPlace: 8, MissionSystem: 2, MissionAbandon: -5, MissionFail: -8,
            PirateKill: 0.5, PirateHourly: 6, SosHelp: 2, InvasionMax: 10,
            TraderAttack: -6, TraderKill: -12, TraderPlace: -4,
            RangerAttack: -10, RangerKill: -25, PlayerKill: -15),
        Gate: new RepGate("friend", [3], ["heavy"]),
        Missions: new RepMissions(new Dictionary<string, int> { ["enemy"] = 0, ["distrust"] = 2 }, "friend", 1.5));
}
