namespace Sro.Sim;

/// <summary>
/// Очередь входов одного игрока. Сервер делает ровно один шаг на каждый вход, поэтому AckSeq значит
/// «состояние после входа AckSeq», и клиент точно переигрывает неподтверждённые входы поверх снапшота.
/// </summary>
public sealed class InputBuffer(MoveInput initial)
{
    /// <summary>Шаги начинаются, когда накопилось столько входов: запас на неровную доставку по сети.</summary>
    public const int StartDepth = 2;

    /// <summary>
    /// Бюджет шагов пополняется на 1 за тик и копится не больше чем до MaxBudget: после всплеска TCP можно
    /// догнать несколько шагов за тик, но в среднем не быстрее 20 шагов/с — ускорить себя клиент не может.
    /// </summary>
    public const int MaxBudget = 5;

    /// <summary>Столько тиков без входов корабль ждёт, потом летит дальше с последним входом («фантомные» шаги).</summary>
    public const int StarveTicks = 3;

    /// <summary>После стольких тиков тишины долг фантомных шагов прощается: клиент, видимо, был в фоне.</summary>
    public const int ResyncTicks = 20;

    /// <summary>
    /// Если после шагов в очереди больше входов, клиент шлёт быстрее реального времени (спешат часы или спидхак):
    /// один лишний вход за тик засчитывается без шага, чтобы задержка не копилась.
    /// </summary>
    public const int MaxDepth = 4;

    public const int MaxQueue = 10;

    private readonly Queue<(int Seq, MoveInput Input)> _queue = new();
    private int _lastSeq;
    private int _budget;
    private int _starved;
    private bool _started;

    public int AckSeq { get; private set; }
    public MoveInput Last { get; private set; } = initial;

    /// <summary>Сколько шагов сделано без входа; столько же опоздавших входов засчитываются без шага.</summary>
    public int Phantom { get; private set; }

    /// <returns>false, если seq не новее уже полученного.</returns>
    public bool Enqueue(int seq, MoveInput input)
    {
        if (seq <= _lastSeq) return false;
        _lastSeq = seq;
        if (_queue.Count >= MaxQueue) _queue.Dequeue();
        _queue.Enqueue((seq, input));
        return true;
    }

    /// <summary>Входы, которые нужно просимулировать в этом тике, по шагу на каждый.</summary>
    /// <returns>Сколько входов записано в <paramref name="steps"/>.</returns>
    public int Tick(Span<MoveInput> steps)
    {
        _budget = Math.Min(_budget + 1, MaxBudget);

        // Лишние входы после всплеска засчитываются за уже сделанные фантомные шаги.
        // Один вход всегда остаётся на шаг этого тика, чтобы корабль не замирал, пока долг гасится.
        while (Phantom > 0 && _queue.Count > 1)
        {
            Consume(_queue.Dequeue());
            Phantom--;
        }

        if (_queue.Count == 0)
        {
            _starved++;
            if (!_started || _starved <= StarveTicks || _budget == 0 || steps.Length == 0) return 0;
            _budget--;
            if (_starved <= ResyncTicks) Phantom++;
            else Phantom = 0;
            steps[0] = Last;
            return 1;
        }

        _starved = 0;
        if (!_started)
        {
            if (_queue.Count < StartDepth) return 0;
            _started = true;
        }

        // Обычно один шаг; если очередь выросла, догоняем, оставляя один вход про запас.
        var count = Math.Min(Math.Max(1, _queue.Count - (StartDepth - 1)), Math.Min(_budget, steps.Length));
        for (var i = 0; i < count; i++)
        {
            var entry = _queue.Dequeue();
            Consume(entry);
            steps[i] = entry.Input;
        }
        _budget -= count;

        if (_queue.Count > MaxDepth) Consume(_queue.Dequeue());
        return count;
    }

    private void Consume((int Seq, MoveInput Input) entry)
    {
        AckSeq = entry.Seq;
        Last = entry.Input;
    }
}
