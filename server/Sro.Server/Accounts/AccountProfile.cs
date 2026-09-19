namespace Sro.Server.Accounts;

/// <summary>
/// Игровое состояние пилота, которое переживает выход из игры и перезапуск сервера (GDD §62, в объёме M8).
/// Модули корабля — M9. Положение корабля не хранится: после входа пилот появляется у станции
/// той системы, где пристыковался последний раз.
/// </summary>
/// <param name="Credits">Кредиты.</param>
/// <param name="Hull">Активный корпус.</param>
/// <param name="Weapon">Активная пушка.</param>
/// <param name="Hulls">Купленные корпуса — ангар (GDD §51).</param>
/// <param name="Weapons">Купленные пушки.</param>
/// <param name="Cargo">Трюм: предмет — количество. Груз живёт у пилота, а не у корабля (GDD §24).</param>
/// <param name="Fuel">Топливо (GDD §6); null — профиль старше M7, бак полный.</param>
/// <param name="System">Система последней стыковки — «домашняя база»; null — стартовая.</param>
/// <param name="Tutorial">Шаг обучения (GDD §54); null — профиль старше M8: обучение считается пройденным.</param>
/// <param name="Mission">Взятое задание; null — нет.</param>
/// <param name="MissionSeed">Сид доски заданий; null — любой.</param>
public sealed record AccountProfile(
    int Credits,
    string Hull,
    string Weapon,
    IReadOnlyList<string> Hulls,
    IReadOnlyList<string> Weapons,
    IReadOnlyDictionary<string, int> Cargo,
    int? Fuel = null,
    string? System = null,
    int? Tutorial = null,
    Sro.Sim.ActiveMission? Mission = null,
    int? MissionSeed = null);
