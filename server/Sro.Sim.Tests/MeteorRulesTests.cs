namespace Sro.Sim.Tests;

/// <summary>shared/meteors.json: разбор, проверки и инвариант туннелирования.</summary>
public class MeteorRulesTests
{
    /// <summary>Скорость лёгкого — как в shared/hulls.json (165): у TestHulls она ещё из GDD (330) и туннелирование ловила бы сразу.</summary>
    private static readonly IReadOnlyDictionary<string, HullParams> Hulls = new Dictionary<string, HullParams>
    {
        ["light"] = TestHulls.Light with { MaxSpeed = 165 },
        ["heavy"] = TestHulls.Heavy,
    };

    private static readonly string[] Tables = ["rockSmall", "rockLarge"];

    private const string Valid = """
        {
          "maxAlive": 10,
          "sizes": {
            "small": { "name": "Мелкий", "radius": 14, "hp": 60, "speedMin": 240, "speedMax": 300, "ramDamage": 110, "weight": 0.45, "table": "rockSmall" },
            "large": { "name": "Крупный", "radius": 34, "hp": 320, "speedMin": 180, "speedMax": 220, "ramDamage": 300, "weight": 0.15 }
          }
        }
        """;

    private static bool Parse(string json, out MeteorRules rules, out string? error) =>
        MeteorRules.TryParse(json, Hulls, 900, Tables, out rules, out error);

    [Fact]
    public void EmptyFile_MeansNoMeteors()
    {
        Assert.True(Parse("{}", out var rules, out var error), error);
        Assert.False(rules.Enabled);
        Assert.False(MeteorRules.None.Enabled);
    }

    [Fact]
    public void ValidFile_Parses()
    {
        Assert.True(Parse(Valid, out var rules, out var error), error);
        Assert.True(rules.Enabled);
        Assert.Equal(2, rules.SizeMap.Count);
        // Средний интервал 7 с — вероятность на тик такая, что за 140 тиков камень появляется примерно один раз.
        Assert.Equal(1 - Math.Exp(-SimConfig.Dt / 7), rules.SpawnChancePerTick, 12);
    }

    [Fact]
    public void SharedMeteorsJson_IsValid()
    {
        Assert.True(Balance.TryParse(TestHulls.SharedSources(), out var balance, out var error), error);
        Assert.True(balance!.Meteors.Enabled);
        // Каждая таблица размера существует в loot.json — иначе расстрелянный камень ничего бы не давал.
        Assert.All(balance.Meteors.SizeMap.Values, s => Assert.Contains(s.Table!, balance.Loot.TableMap.Keys));
    }

    [Fact]
    public void Validate_RejectsSpeedThatWouldTunnel()
    {
        // 600 + 165 = 765 ед/с → 38.3 за тик, больше наименьшей досягаемости 14 + 16 = 30.
        var json = Valid.Replace("\"speedMax\": 300", "\"speedMax\": 600");
        Assert.False(Parse(json, out _, out var error));
        Assert.StartsWith("speed is too high for the tick rate", error);
    }

    [Fact]
    public void Validate_CountsTheSpeedGravityAdds()
    {
        // Сама по себе скорость проходит, но разгон к центру системы выводит её за предел тика.
        Assert.True(Parse(Valid, out var gentle, out var error), error);
        Assert.True(gentle.TopSpeed(900) > 300, "gravity must speed a falling rock up");

        var json = Valid.Replace("\"maxAlive\": 10", "\"maxAlive\": 10, \"gravity\": 200000000");
        Assert.False(Parse(json, out _, out var heavy));
        Assert.StartsWith("speed is too high for the tick rate", heavy);
        Assert.Contains("Top meteor speed with gravity", heavy);
    }

    [Fact]
    public void Validate_RejectsBrokenTracks()
    {
        var withTracks = Valid.Replace("\"maxAlive\": 10", "\"maxAlive\": 10, \"tracks\": { \"arc\": { \"name\": \"Дуга\", \"aimFactor\": 2 } }");
        Assert.False(Parse(withTracks, out _, out var error));
        Assert.Equal("tracks.arc: aimFactor must be within 0..1", error);

        var zeroWeights = Valid.Replace("\"maxAlive\": 10", "\"maxAlive\": 10, \"tracks\": { \"arc\": { \"name\": \"Дуга\", \"weight\": 0 } }");
        Assert.False(Parse(zeroWeights, out _, out var weightError));
        Assert.Equal("at least one track must have a positive weight", weightError);
    }

    [Fact]
    public void Tracks_ArePickedByWeightAndFallBackToThePlainOne()
    {
        var json = Valid.Replace(
            "\"maxAlive\": 10",
            "\"maxAlive\": 10, \"tracks\": { \"flyby\": { \"name\": \"Пролёт\", \"weight\": 0.75 }, \"arc\": { \"name\": \"Дуга\", \"aimFactor\": 0.4, \"speedFactor\": 0.8, \"weight\": 0.25 } }");
        Assert.True(Parse(json, out var rules, out var error), error);

        Assert.Equal("flyby", rules.PickTrack(0));
        Assert.Equal("arc", rules.PickTrack(0.8));
        Assert.Equal(0.4, rules.Track("arc").AimFactor);
        // Неизвестная и отсутствующая траектория — обычная: целимся во весь круг на своей скорости.
        Assert.Equal(1, rules.Track("nope").AimFactor);
        Assert.Equal(1, rules.Track(null).SpeedFactor);
        Assert.Null(MeteorRules.None.PickTrack(0.5));
    }

    [Theory]
    [InlineData("\"radius\": 14", "\"radius\": 0", "sizes.small: radius must be positive")]
    [InlineData("\"speedMin\": 240", "\"speedMin\": 400", "sizes.small: speedMax must not be less than speedMin")]
    [InlineData("\"hp\": 60", "\"hp\": -1", "sizes.small: hp must be positive")]
    [InlineData("\"rockSmall\"", "\"nowhere\"", "sizes.small: unknown loot table 'nowhere'")]
    [InlineData("\"weight\": 0.45", "\"weight\": 0", null)]
    [InlineData("\"maxAlive\": 10", "\"maxAlive\": -1", "maxAlive must not be negative")]
    [InlineData("\"maxAlive\": 10", "\"maxAlive\": 10, \"aimRadius\": 500", "aimRadius must be within")]
    [InlineData("\"maxAlive\": 10", "\"maxAlive\": 10, \"ramMinFactor\": 2", "ramMinFactor must be positive and not above ramMaxFactor")]
    [InlineData("\"maxAlive\": 10", "\"maxAlive\": 10, \"gravity\": -1", "gravity must not be negative")]
    [InlineData("\"maxAlive\": 10", "\"maxAlive\": 10, \"gravityMinRadius\": 0", "gravityMinRadius must be positive")]
    public void Validate_RejectsBrokenFiles(string from, string to, string? expected)
    {
        var ok = Parse(Valid.Replace(from, to), out _, out var error);
        if (expected is null)
        {
            Assert.True(ok, error); // нулевой вес у одного размера допустим: он просто не выпадает
            return;
        }
        Assert.False(ok);
        Assert.StartsWith(expected, error);
    }

    [Fact]
    public void Validate_RejectsAllZeroWeights()
    {
        var json = Valid.Replace("\"weight\": 0.45", "\"weight\": 0").Replace("\"weight\": 0.15", "\"weight\": 0");
        Assert.False(Parse(json, out _, out var error));
        Assert.Equal("at least one size must have a positive weight", error);
    }

    [Fact]
    public void Balance_RejectsTheWholeSetWhenMeteorsAreBroken()
    {
        Assert.False(Balance.TryParse(TestHulls.SharedSources() with { Meteors = "not json" }, out var balance, out var error));
        Assert.Null(balance);
        Assert.StartsWith(Balance.MeteorsFile, error);
    }

    [Fact]
    public void Balance_WithoutMeteors_FallsBackToNone()
    {
        var balance = new Balance(new Dictionary<string, HullParams>(), new Dictionary<string, WeaponParams>(), new CombatRules());
        Assert.Same(MeteorRules.None, balance.Meteors);
    }

    [Fact]
    public void PickSize_FollowsWeights()
    {
        Assert.True(Parse(Valid, out var rules, out _));
        // Веса 0.45 и 0.15: первые три четверти отрезка — мелкий, остальное — крупный.
        Assert.Equal("small", rules.PickSize(0));
        Assert.Equal("small", rules.PickSize(0.74));
        Assert.Equal("large", rules.PickSize(0.76));
        Assert.Equal("large", rules.PickSize(0.999999999));

        var counts = new Dictionary<string, int> { ["small"] = 0, ["large"] = 0 };
        var rng = new Random(3);
        for (var i = 0; i < 10_000; i++) counts[rules.PickSize(rng.NextDouble())!]++;
        Assert.InRange(counts["small"] / 10_000.0, 0.73, 0.77);
    }

    [Fact]
    public void PickSize_SkipsZeroWeight()
    {
        Assert.True(Parse(Valid.Replace("\"weight\": 0.45", "\"weight\": 0"), out var rules, out _));
        Assert.Equal("large", rules.PickSize(0));
    }

    [Theory]
    [InlineData(500, 250, 1.6)]
    [InlineData(250, 250, 1.0)]
    [InlineData(50, 250, 0.5)]
    [InlineData(-100, 250, 0.5)]
    [InlineData(10, 0, 1.6)]
    public void RamFactor_IsClamped(double closing, double speed, double expected)
    {
        Assert.Equal(expected, new MeteorRules().RamFactor(closing, speed), 9);
    }
}
