using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Обучение и задания пилота (GDD §36, §54). Как и трюм, живут у игрока, а не у корабля: переживают гибель,
/// прыжки и обрыв связи. Что засчитывать, решает <see cref="Room"/> — здесь только состояние.
/// </summary>
public sealed class MissionLog
{
    /// <summary>
    /// Так пройденное обучение записано в профиле (M18): там null значит «профиль старше M18»,
    /// и отличить его от «пройдено» надо.
    /// </summary>
    public const string Finished = "done";

    /// <summary>Id текущего шага обучения (M18; до него — номер); null — обучения нет: пройдено или пропущено.</summary>
    public string? Tutorial;

    /// <summary>
    /// Шаг «остановиться» (M18): разогнался ли пилот после вылета. Без этого шаг закрыл бы корабль,
    /// так и не тронувший газ у дока. Не хранится: перезашёл — разгонись снова.
    /// </summary>
    public bool Moved;

    /// <summary>С какой секунды комнаты пилот стоит у буя; null — не стоит.</summary>
    public double? StillSince;

    /// <summary>Взятое задание; null — нет. Одно на пилота.</summary>
    public ActiveMission? Active;

    /// <summary>Сид доски: меняется, когда задание взяли или сдали, — и доска обновляется.</summary>
    public int Seed;
}

/// <summary>
/// Что пилот прошёл в сюжетных кампаниях (M20a). Прогресс — список выполненных миссий, а не номер шага:
/// урок M18 про обучение. Миссию можно вставить в середину цепочки, и тот, кто уже в пути, не собьётся.
/// </summary>
public sealed class StoryLog
{
    /// <summary>Id выполненных миссий кампании; порядок неважен, важно только членство.</summary>
    public readonly HashSet<string> Done = new(StringComparer.Ordinal);

    /// <summary>Флаги выборов: по ним следующие миссии и реплики узнают, как пилот тогда поступил.</summary>
    public readonly HashSet<string> Flags = new(StringComparer.Ordinal);

    /// <summary>Последние реплики кампании — их показывает журнал, когда миссия уже взята или ещё не взята.</summary>
    public List<string> Lines = [];

    public bool Any => Done.Count > 0 || Flags.Count > 0 || Lines.Count > 0;
}
