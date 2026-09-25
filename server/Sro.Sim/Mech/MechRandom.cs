namespace Sro.Sim.Mech;

/// <summary>
/// Броски наземного боя — xorshift32 от сида сессии. Свой, а не System.Random: последовательность того
/// не обещана между версиями .NET, а бой обязан воспроизводиться в тестах и смоуке по одному сиду.
/// Клиенту он не нужен: броски делает только сервер.
/// </summary>
public sealed class MechRandom(uint seed)
{
    private uint _state = seed == 0 ? 0x9E3779B9u : seed;

    public uint NextUInt()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>Целое в [0, n).</summary>
    public int Next(int n) => (int)(NextUInt() % (uint)n);

    /// <summary>Дробное в [0, 1).</summary>
    public double NextDouble() => NextUInt() / 4294967296.0;
}
