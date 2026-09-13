namespace Sro.Sim.Tests;

public class InputBufferTests
{
    private static readonly MoveInput Idle = new(0, -1, 0);

    private static MoveInput Thrust(double th) => new(0, -1, th);

    private static (int Count, MoveInput[] Steps) Tick(InputBuffer buffer)
    {
        var steps = new MoveInput[InputBuffer.MaxBudget];
        var count = buffer.Tick(steps);
        return (count, steps[..count]);
    }

    /// <summary>Клиент уже прислал два входа и сервер начал шагать.</summary>
    private static (InputBuffer Buffer, int NextSeq) Started()
    {
        var buffer = new InputBuffer(Idle);
        buffer.Enqueue(1, Thrust(0.1));
        buffer.Enqueue(2, Thrust(0.2));
        Assert.Equal(1, Tick(buffer).Count);
        return (buffer, 3);
    }

    [Fact]
    public void WaitsForAJitterBufferBeforeStepping()
    {
        var buffer = new InputBuffer(Idle);
        buffer.Enqueue(1, Thrust(1));
        Assert.Equal(0, Tick(buffer).Count);

        buffer.Enqueue(2, Thrust(1));
        var (count, _) = Tick(buffer);
        Assert.Equal(1, count);
        Assert.Equal(1, buffer.AckSeq);
    }

    [Fact]
    public void SteadyStream_IsOneStepPerInputWithAMatchingAck()
    {
        var (buffer, seq) = Started();
        for (var i = 0; i < 50; i++, seq++)
        {
            buffer.Enqueue(seq, Thrust(0.5));
            var (count, steps) = Tick(buffer);
            Assert.Equal(1, count);
            Assert.Equal(seq - 1, buffer.AckSeq); // один вход всегда в запасе
            Assert.Equal(i == 0 ? 0.2 : 0.5, steps[0].Throttle);
        }
    }

    [Fact]
    public void RejectsOldOrRepeatedSeq()
    {
        var buffer = new InputBuffer(Idle);
        Assert.True(buffer.Enqueue(5, Idle));
        Assert.False(buffer.Enqueue(5, Idle));
        Assert.False(buffer.Enqueue(4, Idle));
    }

    [Fact]
    public void BurstAfterAStall_CatchesUpSeveralStepsPerTick()
    {
        var (buffer, seq) = Started();
        Tick(buffer);
        for (var i = 0; i < InputBuffer.StarveTicks; i++) Tick(buffer); // бюджет копится, пока входов нет
        for (var i = 0; i < 4; i++) buffer.Enqueue(seq++, Thrust(1));
        Assert.Equal(3, Tick(buffer).Count); // один вход остаётся про запас
    }

    [Fact]
    public void BurstWithoutAStall_DoesNotSpeedTheShipUpOrPileUpLatency()
    {
        var (buffer, seq) = Started();
        for (var i = 0; i < 6; i++) buffer.Enqueue(seq++, Thrust(1)); // клиент «спешит»
        var total = 0;
        for (var tick = 0; tick < 4; tick++) total += Tick(buffer).Count;
        Assert.Equal(4, total); // не быстрее шага за тик
        Assert.True(seq - 1 - buffer.AckSeq <= InputBuffer.MaxDepth, $"queue lag {seq - 1 - buffer.AckSeq}");
    }

    [Fact]
    public void Flood_CannotSimulateFasterThanTheTickRate()
    {
        var (buffer, seq) = Started();
        var steps = 0;
        for (var tick = 0; tick < 100; tick++)
        {
            for (var i = 0; i < 5; i++) buffer.Enqueue(seq++, Thrust(1)); // спидхак: 100 входов/с
            steps += Tick(buffer).Count;
        }
        Assert.InRange(steps, 95, 100 + InputBuffer.MaxBudget);
    }

    [Fact]
    public void ShortGap_WaitsWithoutPhantomSteps()
    {
        var (buffer, _) = Started();
        Tick(buffer); // доедаем запасной вход
        for (var i = 0; i < InputBuffer.StarveTicks; i++) Assert.Equal(0, Tick(buffer).Count);
        Assert.Equal(0, buffer.Phantom);
    }

    [Fact]
    public void LongGap_KeepsCruisingAndLateInputsPayTheDebt()
    {
        var (buffer, seq) = Started();
        Tick(buffer);
        var lastAck = buffer.AckSeq;
        for (var i = 0; i < InputBuffer.StarveTicks; i++) Tick(buffer);

        var (count, steps) = Tick(buffer);
        Assert.Equal(1, count);
        Assert.Equal(0.2, steps[0].Throttle); // летим с последним входом
        Assert.Equal(1, buffer.Phantom);
        Assert.Equal(lastAck, buffer.AckSeq);

        // Всплеск опоздавших входов: лишний засчитывается за фантомный шаг, остальные шагаются.
        for (var i = 0; i < 4; i++) buffer.Enqueue(seq++, Thrust(0.9));
        var (afterBurst, _) = Tick(buffer);
        Assert.Equal(0, buffer.Phantom);
        Assert.Equal(2, afterBurst); // 4 − 1 (долг) − 1 (запас)
        Assert.Equal(seq - 2, buffer.AckSeq);
    }

    [Fact]
    public void VeryLongSilence_ForgivesTheDebt()
    {
        var (buffer, _) = Started();
        Tick(buffer);
        for (var i = 0; i < InputBuffer.ResyncTicks + 5; i++) Tick(buffer);
        Assert.Equal(0, buffer.Phantom);
    }

    [Fact]
    public void NeverStartedPlayer_DoesNotMove()
    {
        var buffer = new InputBuffer(Idle);
        for (var i = 0; i < 30; i++) Assert.Equal(0, Tick(buffer).Count);
    }
}
