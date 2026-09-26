using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Идущее «живое» задание (M14): сопровождение конвоя и патруль со звеном рейнджеров. Живёт только в своей
/// комнате и только пока пилот в космосе: его актёры — это корабли этой системы. Прыжок, док, гибель, обрыв
/// связи и перезапуск комнаты его кончают, поэтому в аккаунт он не пишется, а <see cref="MissionLog.Active"/>
/// с таким видом при входе не восстанавливается.
/// </summary>
/// <param name="Kind">
/// <see cref="MissionRules.EscortKind"/>, <see cref="MissionRules.PatrolKind"/>
/// или <see cref="MissionRules.DefendKind"/>.
/// </param>
public sealed class MissionRun(int id, int playerId, string kind)
{
    /// <summary>Номер прогона: им помечены его конвой и его звено.</summary>
    public int Id { get; } = id;
    public int PlayerId { get; } = playerId;
    public string Kind { get; } = kind;

    /// <summary>Сопровождение: чей это конвой; 0 — конвоя уже нет.</summary>
    public int TraderId;

    /// <summary>Сопровождение: длина маршрута конвоя при выходе — по ней видно, какую часть пути он прошёл.</summary>
    public double Route;

    /// <summary>Сопровождение: сколько засад уже выпущено.</summary>
    public int Wave;

    /// <summary>Сопровождение: тик последней засады; 0 — засад ещё не было.</summary>
    public long WaveTick;

    /// <summary>Сопровождение: с какого тика пилот вне радиуса; 0 — он рядом.</summary>
    public long AwaySince;

    /// <summary>Патруль: точки маршрута по порядку.</summary>
    public readonly List<(double X, double Y)> Points = [];

    /// <summary>Патруль: к какой точке идёт звено.</summary>
    public int Point;

    /// <summary>
    /// Патруль: на какой точке маршрута ждут пираты. Бой один на весь патруль, но обязательный: без него
    /// это был бы облёт точек за деньги (M14).
    /// </summary>
    public int FightAt;

    /// <summary>Патруль: пираты уже вызваны. Точка не засчитывается, пока их не перебьют.</summary>
    public bool Engaged;

    /// <summary>Оборона: ключ места, которое защищают. Оно на орбите, и точку боя считают каждый тик.</summary>
    public string? Place;

    /// <summary>Оборона: сколько налётчиков уже дошло до поселения.</summary>
    public int Strikes;

    /// <summary>Оборона: с какого тика можно выпускать следующую волну; 0 — прямо сейчас.</summary>
    public long NextWaveTick;
}
