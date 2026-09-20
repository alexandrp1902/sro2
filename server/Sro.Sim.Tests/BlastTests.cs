using Sro.Sim;

namespace Sro.Sim.Tests;

/// <summary>Урон по площади (M15.5): спад от эпицентра к краю, проверки параметров и молчание тиров.</summary>
public class BlastTests
{
    private static readonly WeaponParams Torpedo = new(
        "Торпедный аппарат Mk1", 650, 100, 8, 900, 900, 0, Arc: 60, Kind: "missile",
        Missile: new MissileParams(170, 55, 9, 18, 150),
        BlastRadius: 300, BlastShare: 0.5);

    private static readonly WeaponParams Railgun = new(
        "Рельсотрон Mk1", 480, 85, 4, 1200, 1500, 20, Arc: 40);

    [Fact]
    public void InTheEpicentreTheNeighbourTakesTheShareOfTheDamage()
    {
        Assert.Equal(325, Combat.Splash(Torpedo, 0), 6); // 650 × 0.5
    }

    [Fact]
    public void AtTheRimAndBeyondThereIsNothing()
    {
        Assert.Equal(0, Combat.Splash(Torpedo, 300));
        Assert.Equal(0, Combat.Splash(Torpedo, 400));
    }

    [Fact]
    public void HalfwayOutItIsLessThanHalf()
    {
        // Спад 1.5, а не линейный: на полпути остаётся около трети эпицентра, а не половина.
        var half = Combat.Splash(Torpedo, 150);
        Assert.True(half < 325 * 0.5, $"на полрадиуса {half} должно быть меньше половины эпицентра");
        Assert.Equal(325 * Math.Pow(0.5, 1.5), half, 6);
    }

    [Fact]
    public void ItFallsOffAllTheWayOut()
    {
        var previous = double.MaxValue;
        for (var d = 0; d <= 300; d += 25)
        {
            var value = Combat.Splash(Torpedo, d);
            Assert.True(value < previous, $"на {d} урон {value} не меньше прежнего {previous}");
            previous = value;
        }
    }

    [Fact]
    public void AWeaponWithoutARadiusHasNoSplashAtAll()
    {
        Assert.Equal(0, Combat.Splash(Railgun, 0));
        Assert.Equal(0, Combat.Splash(Railgun, 50));
    }

    [Fact]
    public void ANeighbourInsideTheHullCountsAsTheEpicentre()
    {
        // Расстояние до брони бывает отрицательным: сосед вплотную получает ровно долю, а не больше.
        Assert.Equal(325, Combat.Splash(Torpedo, -20), 6);
    }

    [Theory]
    [InlineData(-1, 0.5, 1.5, "blastRadius must not be negative")]
    [InlineData(2000, 0.5, 1.5, "blastRadius must not exceed maxRange")]
    [InlineData(300, 1.5, 1.5, "blastShare must be within 0..1")]
    [InlineData(300, 0.5, 9, "blastFalloff must be within 0.5..4")]
    [InlineData(300, 0, 1.5, "blastRadius without blastShare does nothing")]
    public void BadBlastParametersAreRejected(double radius, double share, double falloff, string expected)
    {
        var weapon = Torpedo with { BlastRadius = radius, BlastShare = share, BlastFalloff = falloff };
        Assert.Equal(expected, weapon.Validate());
    }

    [Fact]
    public void TiersDoNotScaleTheBlastRadius()
    {
        // Mk3 бьёт сильнее, а не шире: осколки растут вместе с уроном, радиус остаётся прежним.
        var weapons = new Dictionary<string, WeaponParams> { ["torpedoes"] = Torpedo };
        var tiers = new List<TierDef> { new(1.25, 1.15, 3), new(1.5, 1.3, 8) };
        var expanded = Tiers.Expand(weapons, tiers);

        var mk3 = expanded[Tiers.Id("torpedoes", 3)];
        Assert.Equal(Torpedo.BlastRadius, mk3.BlastRadius);
        Assert.Equal(Torpedo.BlastShare, mk3.BlastShare);
        Assert.True(mk3.Damage > Torpedo.Damage);
    }
}
