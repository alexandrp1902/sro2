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
/// <param name="System">Система последней стыковки — «домашняя база»; null — стартовая.</param>
/// <param name="Tutorial">
/// Номер шага обучения (GDD §54) — так он хранился до M18; null — профиль старше M8 или уже с
/// <paramref name="TutorialStep"/>. Новые профили его не пишут: номер переводится в id при входе.
/// </param>
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
/// <param name="Ships">
/// Где стоят корпуса ангара (M15.6): id корпуса → ключ места. Активного корпуса здесь нет.
/// null — профиль старше M15.6: тогда считается, что все корабли ждут дома.
/// </param>
/// <param name="Career">
/// Путь, выбранный при заведении аккаунта (M15.5): «ranger», «trader». null — профиль старше M15.5 или гость,
/// читается как рейнджер. Дальше старта путь ни на что не влияет: он остаётся ради карточки пилота,
/// ветки обучения и статистики.
/// </param>
/// <param name="Hp">
/// Прочность корпуса (M15.7). До неё положение и состояние корабля не хранились вовсе, и гибель ничего
/// не стоила: достаточно было перезайти. Теперь разбитый корпус переживает и выход, и перезапуск сервера.
/// null — профиль старше M15.7 или корабль целый: читается как полный корпус.
/// </param>
/// <param name="TutorialStep">
/// Id шага обучения (M18): по id, а не по номеру, чтобы шаги можно было вставлять в середину.
/// «done» — пройдено или пропущено; null — профиль старше M18, тогда смотрится <paramref name="Tutorial"/>.
/// </param>
public sealed record AccountProfile(
    int Credits,
    string Hull,
    string Weapon,
    IReadOnlyList<string> Hulls,
    IReadOnlyList<string> Weapons,
    IReadOnlyDictionary<string, int> Cargo,
    string? System = null,
    int? Tutorial = null,
    Sro.Sim.ActiveMission? Mission = null,
    int? MissionSeed = null,
    Sro.Sim.ShipFit? Fit = null,
    IReadOnlyDictionary<string, int>? Storage = null,
    IReadOnlyDictionary<string, double>? Reputation = null,
    long? RepAt = null,
    string? Place = null,
    string? Career = null,
    IReadOnlyDictionary<string, string>? Ships = null,
    double? Hp = null,
    string? TutorialStep = null);
