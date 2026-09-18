namespace Sro.Server.Accounts;

/// <summary>
/// Игровое состояние пилота, которое переживает выход из игры и перезапуск сервера (GDD §62, в объёме M6).
/// Топливо, домашняя база, текущая система — M7; задания — M8; модули корабля — M9.
/// Положение корабля не хранится: после входа пилот появляется у станции, как при первом входе.
/// </summary>
/// <param name="Credits">Кредиты.</param>
/// <param name="Hull">Активный корпус.</param>
/// <param name="Weapon">Активная пушка.</param>
/// <param name="Hulls">Купленные корпуса — ангар (GDD §51).</param>
/// <param name="Weapons">Купленные пушки.</param>
/// <param name="Cargo">Трюм: предмет — количество. Груз живёт у пилота, а не у корабля (GDD §24).</param>
public sealed record AccountProfile(
    int Credits,
    string Hull,
    string Weapon,
    IReadOnlyList<string> Hulls,
    IReadOnlyList<string> Weapons,
    IReadOnlyDictionary<string, int> Cargo);
