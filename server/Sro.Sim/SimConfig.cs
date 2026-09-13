namespace Sro.Sim;

/// <summary>Общие константы симуляции. Сервер и клиент обязаны использовать одинаковые значения.</summary>
public static class SimConfig
{
    /// <summary>Серверных тиков в секунду (GDD §44).</summary>
    public const int TickRate = 20;

    /// <summary>Длительность одного тика, секунды.</summary>
    public const double Dt = 1.0 / TickRate;
}
