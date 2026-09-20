namespace Sro.Server.Accounts;

/// <summary>
/// Игровое состояние пилота, которое переживает выход из игры и перезапуск сервера (GDD §62, в объёме M9).
/// Положение корабля не хранится: после входа пилот появляется у станции той системы, где пристыковался последний раз.
/// </summary>
/// <param name="Credits">Кредиты.</param>
/// <param name="Hull">Активный корпус.</param>
/// <param name="Weapon">Пушка первого слота; до M9 — единственная пушка корабля.</param>
/// <param name="Hulls">Купленные корпуса — ангар (GDD §51).</param>
/// <param name="Weapons">До M9 — купленные пушки; теперь пусто, всё купленное — в <paramref name="Storage"/> и <paramref name="Fit"/>.</param>
/// <param name="Cargo">Трюм: предмет — количество. Груз живёт у пилота, а не у корабля (GDD §24).</param>
/// <param name="Fuel">Топливо (GDD §6); null — профиль старше M7, бак полный.</param>
/// <param name="System">Система последней стыковки — «домашняя база»; null — стартовая.</param>
/// <param name="Tutorial">Шаг обучения (GDD §54); null — профиль старше M8: обучение считается пройденным.</param>
/// <param name="Mission">Взятое задание; null — нет.</param>
/// <param name="MissionSeed">Сид доски заданий; null — любой.</param>
/// <param name="Fit">Оснащение корабля: пушки по слотам и модули; null — профиль старше M9.</param>
/// <param name="Storage">Склад: пушки и модули, которые не стоят; null — пуст (или профиль старше M9).</param>
/// <param name="Reputation">Очки по системам и станциям (M13); null — профиль старше M13, репутация нулевая.</param>
/// <param name="RepAt">
/// Когда репутацию в последний раз приводили к текущему времени, unix-секунды. Она тает по часам, а не по
/// тикам, поэтому без этой отметки пропущенное время было бы не из чего посчитать.
/// </param>
/// <param name="Place">
/// Место последней стыковки (M15): «st:vega» — станция, «pl:terra» — поселение. null — профиль старше M15,
/// тогда это станция системы <paramref name="System"/>. Сама <paramref name="System"/> остаётся заполненной:
/// по ней выбирается комната, и откат сервера на старую версию не обнулит пилоту дом.
/// </param>
/// <param name="Career">
/// Путь, выбранный при заведении аккаунта (M15.5): «ranger», «trader». null — профиль старше M15.5 или гость,
/// читается как рейнджер. Дальше старта путь ни на что не влияет: он остаётся ради карточки пилота,
/// ветки обучения и статистики.
/// </param>
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
    int? MissionSeed = null,
    Sro.Sim.ShipFit? Fit = null,
    IReadOnlyDictionary<string, int>? Storage = null,
    IReadOnlyDictionary<string, double>? Reputation = null,
    long? RepAt = null,
    string? Place = null,
    string? Career = null);
