using Sro.Sim;

namespace Sro.Sim.Tests;

/// <summary>
/// Правила обмена (M16b) из shared/trade.json. Числа здесь простые, и проверять в них нечего, кроме
/// одного: дурное значение из файла должно быть отвергнуто, а не уехать в игру.
/// </summary>
public class TradeRulesTests
{
    [Fact]
    public void DefaultsAreSane()
    {
        Assert.Null(TradeRules.Default.Validate());
        Assert.True(TradeRules.Default.Range > 0);
        Assert.True(TradeRules.Default.InviteTicks >= 1);
    }

    [Theory]
    [InlineData(0, 20, "range must be within 0..5000")]
    [InlineData(-1, 20, "range must be within 0..5000")]
    [InlineData(50_000, 20, "range must be within 0..5000")]
    [InlineData(1200, 0, "inviteSeconds must be positive")]
    public void BadNumbersAreRefused(double range, double inviteSeconds, string error) =>
        Assert.Equal(error, new TradeRules(range, inviteSeconds).Validate());

    [Fact]
    public void ReadsTheFileAndKeepsTheDefaultsForWhatIsMissing()
    {
        Assert.True(TradeRules.TryParse("""{ "range": 900 }""", out var rules, out var error));
        Assert.Null(error);
        Assert.Equal(900, rules.Range);
        Assert.Equal(TradeRules.Default.InviteSeconds, rules.InviteSeconds);

        // Невалидный файл отклоняется целиком: в игре остаётся прежний баланс.
        Assert.False(TradeRules.TryParse("""{ "range": -5 }""", out _, out error));
        Assert.Equal("range must be within 0..5000", error);
    }
}
