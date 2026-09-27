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

    /// <summary>Взятое задание с доски; null — нет.</summary>
    public ActiveMission? Active;

    /// <summary>
    /// Взятая сюжетная миссия; null — нет. У сюжета свой слот (плейтест 2026-09-26): историю ведут
    /// параллельно с работой с доски, а не вместо неё.
    /// </summary>
    public ActiveMission? Story;

    /// <summary>Оба слота по порядку: доска, потом сюжет.</summary>
    public static readonly MissionSlot[] Slots = [MissionSlot.Board, MissionSlot.Story];

    public ActiveMission? Of(MissionSlot slot) => slot == MissionSlot.Story ? Story : Active;

    public void Set(MissionSlot slot, ActiveMission? mission)
    {
        if (slot == MissionSlot.Story) Story = mission;
        else Active = mission;
    }

    /// <summary>В какой слот ложится это предложение: сюжетное — в свой, остальное — в слот доски.</summary>
    public static MissionSlot SlotOf(MissionOffer offer) => offer.Story is null ? MissionSlot.Board : MissionSlot.Story;

    /// <summary>
    /// Слот взятого задания с таким id; null — такого нет. Клиент до протокола 36 id не шлёт: тогда
    /// доска, если там что-то взято, иначе сюжет.
    /// </summary>
    public MissionSlot? SlotOf(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Active is not null ? MissionSlot.Board : Story is not null ? MissionSlot.Story : null;
        if (Active?.Offer.Id == id) return MissionSlot.Board;
        if (Story?.Offer.Id == id) return MissionSlot.Story;
        return null;
    }

    /// <summary>
    /// Слот, где взято живое задание — конвой, патруль или оборона; null — такого нет. Живое одно на оба
    /// слота: его актёры летают в системе, и два конвоя разом проваливали бы друг друга.
    /// </summary>
    public MissionSlot? LiveSlot =>
        Active is { } board && MissionRules.IsLive(board.Offer.Kind) ? MissionSlot.Board
        : Story is { } story && MissionRules.IsLive(story.Offer.Kind) ? MissionSlot.Story
        : null;

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

/// <summary>Слот задания: работа с доски и сюжетная миссия берутся параллельно, по одной.</summary>
public enum MissionSlot
{
    Board,
    Story,
}
