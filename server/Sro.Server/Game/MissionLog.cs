using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Обучение и задания пилота (GDD §36, §54). Как и трюм, живут у игрока, а не у корабля: переживают гибель,
/// прыжки и обрыв связи. Что засчитывать, решает <see cref="Room"/> — здесь только состояние.
/// </summary>
public sealed class MissionLog
{
    /// <summary>Обучение пройдено или пропущено — какой бы длины ни стал список шагов в missions.json.</summary>
    public const int Finished = int.MaxValue;

    /// <summary>Номер текущего шага обучения; <see cref="Finished"/> — обучения нет.</summary>
    public int Tutorial = Finished;

    /// <summary>Взятое задание; null — нет. Одно на пилота.</summary>
    public ActiveMission? Active;

    /// <summary>Сид доски: меняется, когда задание взяли или сдали, — и доска обновляется.</summary>
    public int Seed;
}
