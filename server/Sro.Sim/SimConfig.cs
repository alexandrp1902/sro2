namespace Sro.Sim;

/// <summary>Общие константы симуляции. Сервер и клиент обязаны использовать одинаковые значения.</summary>
public static class SimConfig
{
    /// <summary>Серверных тиков в секунду (GDD §44).</summary>
    public const int TickRate = 20;

    /// <summary>Длительность одного тика, секунды.</summary>
    public const double Dt = 1.0 / TickRate;

    /// <summary>Класс корпуса нового игрока. Зеркало DEFAULT_HULL в client/src/sim/hulls.ts.</summary>
    public const string DefaultHull = "light";

    /// <summary>Пушка нового игрока. Зеркало DEFAULT_WEAPON в client/src/sim/weapons.ts.</summary>
    public const string DefaultWeapon = "pulse";

    /// <summary>Точка спауна у станции. Зеркало SPAWN в client/src/game/layout.ts.</summary>
    public const double SpawnX = 0;
    public const double SpawnY = 420;
}
