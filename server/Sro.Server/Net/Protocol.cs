using System.Text.Json;
using System.Text.Json.Serialization;
using Sro.Sim;

namespace Sro.Server.Net;

// Протокол — JSON-объекты с полем "t" (тип сообщения). Зеркало на клиенте: client/src/net/protocol.ts.

// Клиент → сервер
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMsg), "hello")]
[JsonDerivedType(typeof(CheckMsg), "check")]
[JsonDerivedType(typeof(PingMsg), "ping")]
[JsonDerivedType(typeof(InputMsg), "input")]
[JsonDerivedType(typeof(HullMsg), "hull")]
[JsonDerivedType(typeof(WeaponMsg), "weapon")]
[JsonDerivedType(typeof(FitMsg), "fit")]
[JsonDerivedType(typeof(SellItemMsg), "sellItem")]
[JsonDerivedType(typeof(NameMsg), "name")]
[JsonDerivedType(typeof(TargetMsg), "target")]
[JsonDerivedType(typeof(FireMsg), "fire")]
[JsonDerivedType(typeof(LootTargetMsg), "loot")]
[JsonDerivedType(typeof(GrabMsg), "grab")]
[JsonDerivedType(typeof(SellMsg), "sell")]
[JsonDerivedType(typeof(DropMsg), "drop")]
[JsonDerivedType(typeof(DockMsg), "dock")]
[JsonDerivedType(typeof(BuyMsg), "buy")]
[JsonDerivedType(typeof(BuyGoodsMsg), "buyGoods")]
[JsonDerivedType(typeof(RepairMsg), "repair")]
[JsonDerivedType(typeof(JumpMsg), "jump")]
[JsonDerivedType(typeof(RefuelMsg), "refuel")]
[JsonDerivedType(typeof(MissionMsg), "mission")]
[JsonDerivedType(typeof(PartyMsg), "party")]
[JsonDerivedType(typeof(PvpMsg), "pvp")]
public abstract record ClientMessage;

/// <summary>
/// Вход. С паролем или ключом — пилот с аккаунтом (GDD §61): корпус, пушку, кредиты и трюм сервер берёт из аккаунта,
/// а Hull, Weapon и Token не смотрит. Без них — гость без сохранения: так входят тесты и смоук-скрипты.
/// </summary>
/// <param name="Hull">Гость: класс корпуса, сохранённый на устройстве.</param>
/// <param name="Token">Гость: сессия вкладки — с ней после обрыва связи он возвращается к своему кораблю.</param>
/// <param name="Weapon">Гость: пушка, сохранённая на устройстве.</param>
/// <param name="Password">Пароль; свободный ник с ним заводит новый аккаунт.</param>
/// <param name="Key">Ключ устройства из <see cref="AccountMsg"/>: вход без пароля.</param>
/// <param name="Career">
/// Путь пилота (M15.5): применяется, только когда этим входом заводится аккаунт. Вошедшему в старый
/// аккаунт путь менять нечем, и поле у него просто игнорируется; запертый путь отклоняется как denied.
/// </param>
public sealed record HelloMsg(
    string? Name,
    string? Hull,
    string? Token,
    string? Weapon = null,
    string? Password = null,
    string? Key = null,
    string? Career = null) : ClientMessage;

/// <summary>Свободен ли ник (M15.5): по ответу клиент решает, показывать ли карточки пути.</summary>
public sealed record CheckMsg(string? Name) : ClientMessage;

/// <summary>Карточка пути на экране входа (M15.5).</summary>
/// <param name="Enabled">false — карточка серая и не выбирается: путь ещё закрыт.</param>
public sealed record CareerDto(string Id, string Name, string Hint, bool Enabled);

/// <summary>
/// Ответ на <see cref="CheckMsg"/>: этим ником заведётся новый аккаунт или это вход в старый.
/// Пути едут здесь же — экран входа нужен раньше, чем welcome, и спросить их больше негде.
/// </summary>
/// <param name="Careers">Пути; пусто — их нет, и выбирать нечего (сервер без careers.json).</param>
/// <param name="Career">Какой выбран заранее.</param>
public sealed record NameFreeMsg(string Name, bool Free, IReadOnlyList<CareerDto> Careers, string Career) : ServerMessage;

/// <param name="C">Время клиента, возвращается в pong как есть для замера RTT.</param>
public sealed record PingMsg(double C) : ClientMessage;

/// <summary>Только управление (§49): направление на экране и тяга. Координаты клиент не присылает.</summary>
public sealed record InputMsg(int Seq, double Dx, double Dy, double Th) : ClientMessage;

/// <summary>Поставить корпус из ангара: пилоту с аккаунтом — только свой и только в доке, гостю — любой.</summary>
public sealed record HullMsg(string? Id) : ClientMessage;

/// <summary>Поставить пушку в первый слот: пилоту с аккаунтом — только со склада и только в доке, гостю — любую.</summary>
public sealed record WeaponMsg(string? Id) : ClientMessage;

/// <summary>
/// Оснащение (GDD §20): поставить в слот пушку или модуль со склада, Id = null — снять на склад.
/// Пилоту с аккаунтом — только в доке; гостю — где угодно и что угодно.
/// </summary>
/// <param name="Slot">w0…w5 — оружейные слоты, engine, shield, radar, tank, generator — модули.</param>
public sealed record FitMsg(string? Slot, string? Id = null) : ClientMessage;

/// <summary>Продать со склада пушку или модуль (в доке) — за долю цены.</summary>
public sealed record SellItemMsg(string? Id) : ClientMessage;

/// <summary>Смена ника на лету — только у гостя: у пилота с аккаунтом ник и есть вход.</summary>
public sealed record NameMsg(string? Name) : ClientMessage;

/// <summary>Выбранная цель (GDD §9); 0 — цели нет.</summary>
public sealed record TargetMsg(int Id) : ClientMessage;

/// <summary>Атака нажата или отпущена: пока нажата, пушка стреляет сама по готовности (GDD §47).</summary>
public sealed record FireMsg(bool On) : ClientMessage;

/// <summary>Выбранный предмет (боевой документ §45 — SelectedLoot); 0 — нет. Его и забирает <see cref="GrabMsg"/>.</summary>
public sealed record LootTargetMsg(int Id) : ClientMessage;

/// <summary>Взять выбранный предмет: подбор ручной, тракторный луч сам ничего не хватает.</summary>
public sealed record GrabMsg : ClientMessage;

/// <summary>Продать груз в доке; Item — что именно, null — весь трюм (всё, чем здесь торгуют).</summary>
/// <param name="Count">Сколько штук; 0 — вся стопка. Без Item не смотрится.</param>
public sealed record SellMsg(string? Item = null, int Count = 0) : ClientMessage;

/// <summary>
/// Выбросить груз за борт (M15.1): стопка целиком уходит в космос рядом с кораблём, и подобрать её может
/// кто угодно, включая самого пилота. Только в полёте: в доке для этого есть рынок.
/// </summary>
/// <param name="Item">Что выбросить; null — ничего не делаем. Количество не спрашиваем: стопка целиком.</param>
public sealed record DropMsg(string? Item) : ClientMessage;

/// <summary>Пристыковаться или сесть (On), либо вылететь из дока.</summary>
/// <param name="Place">
/// Ключ места (M15): «st:vega» — станция, «pl:terra» — поселение. null — ближайшее подходящее,
/// как было до планет; клиент называет место, когда игрок выбрал прицелом именно его.
/// </param>
public sealed record DockMsg(bool On, string? Place = null) : ClientMessage;

/// <summary>Купить в доке корпус, пушку или модуль (GDD §26). Корпус сразу ставится.</summary>
/// <param name="Kind"><see cref="Protocol.HullItem"/> или <see cref="Protocol.ItemKind"/>.</param>
/// <param name="Slot">
/// Пушку или модуль — сразу в этот слот (старое уходит на склад); null — в свободный подходящий слот или на склад.
/// </param>
public sealed record BuyMsg(string? Kind, string? Id, string? Slot = null) : ClientMessage;

/// <summary>
/// Купить товар на рынке станции (M12). Отдельно от <see cref="BuyMsg"/>: тот берёт ровно одну вещь и уходит
/// в слот, на склад или в смену корпуса, а здесь — N единиц в трюм. Сервер сам урежет Count до того,
/// что есть на складе, что по карману и что влезет.
/// </summary>
public sealed record BuyGoodsMsg(string? Item, int Count = 1) : ClientMessage;

/// <summary>Починить корпус в доке и зарядить щит — по цене repairPrice из shop.json.</summary>
public sealed record RepairMsg : ClientMessage;

/// <summary>Начать гиперпрыжок через врата в систему To (GDD §5); To = null — отменить подготовку.</summary>
public sealed record JumpMsg(string? To) : ClientMessage;

/// <summary>Заправить бак в доке до полного — по fuelPrice из shop.json (GDD §6, §26).</summary>
public sealed record RefuelMsg : ClientMessage;

/// <summary>Задания (GDD §36, §54).</summary>
/// <param name="Action">
/// <see cref="Protocol.AcceptMission"/> — взять задание Id с доски (в доке); <see cref="Protocol.AbandonMission"/> —
/// бросить своё (где угодно); <see cref="Protocol.CompleteMission"/> — сдать «собрать» (в доке);
/// <see cref="Protocol.SkipTutorial"/> — пропустить обучение.
/// </param>
public sealed record MissionMsg(string? Action, string? Id = null) : ClientMessage;

/// <summary>Группа (GDD §37).</summary>
/// <param name="Action">
/// <see cref="PartyCodes.InviteAction"/> — позвать пилота Id; <see cref="PartyCodes.AcceptAction"/> и
/// <see cref="PartyCodes.DeclineAction"/> — ответить на приглашение пилота Id; <see cref="PartyCodes.LeaveAction"/> — выйти.
/// </param>
public sealed record PartyMsg(string? Action, int Id = 0) : ClientMessage;

/// <summary>
/// Переключатель PvP пилота: выключен — его пушки и ракеты не бьют игроков, торговцев и рейнджеров, где бы он ни был.
/// Правила системы (GDD §34) он не расширяет: в системе без PvP по пилотам не стреляют и с включённым.
/// </summary>
public sealed record PvpMsg(bool On) : ClientMessage;

// Сервер → клиент
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(WelcomeMsg), "welcome")]
[JsonDerivedType(typeof(PongMsg), "pong")]
[JsonDerivedType(typeof(PlayersMsg), "players")]
[JsonDerivedType(typeof(ConfigMsg), "config")]
[JsonDerivedType(typeof(SnapshotMsg), "snapshot")]
[JsonDerivedType(typeof(CargoMsg), "cargo")]
[JsonDerivedType(typeof(NoticeMsg), "notice")]
[JsonDerivedType(typeof(AccountMsg), "account")]
[JsonDerivedType(typeof(NameFreeMsg), "nameFree")]
[JsonDerivedType(typeof(DemandMsg), "demand")]
[JsonDerivedType(typeof(DeniedMsg), "denied")]
[JsonDerivedType(typeof(HangarMsg), "hangar")]
[JsonDerivedType(typeof(MissionsMsg), "missions")]
[JsonDerivedType(typeof(SosMsg), "sos")]
[JsonDerivedType(typeof(PartyInviteMsg), "partyInvite")]
[JsonDerivedType(typeof(PartyStateMsg), "partyState")]
[JsonDerivedType(typeof(PartyEventMsg), "partyEvent")]
[JsonDerivedType(typeof(BountyMsg), "bounty")]
[JsonDerivedType(typeof(InvasionMsg), "invasion")]
[JsonDerivedType(typeof(MarketMsg), "market")]
[JsonDerivedType(typeof(RepMsg), "rep")]
public abstract record ServerMessage;

/// <param name="Id">Id своего корабля в снапшотах.</param>
/// <param name="Version">Версия протокола (<see cref="Protocol.Version"/>): клиент другой версии играть не будет.</param>
/// <param name="Hulls">Параметры корпусов: клиент предсказывает движение с теми же числами, что и сервер.</param>
/// <param name="Weapons">Параметры пушек — для карточки цели и трассеров.</param>
/// <param name="Combat">Правила боя: время респауна, защита.</param>
/// <param name="Resumed">Игрок вернулся к кораблю, который ждал его после обрыва связи.</param>
/// <param name="Npcs">Пираты: логова и укрытие у станции — клиент рисует их на карте.</param>
/// <param name="Loot">Лут: радиус захвата, вид и редкость предметов — для подписей и кольца захвата.</param>
/// <param name="Meteors">Метеориты: радиусы, прочность и пороги предупреждения о таране.</param>
/// <param name="Shop">Магазин станции: цены корпусов, пушек и ремонта.</param>
/// <param name="System">Система, где сейчас корабль: небо, станция, врата. После прыжка приходит новый welcome.</param>
/// <param name="Galaxy">Карта галактики: системы и маршруты с ценой прыжка.</param>
/// <param name="Modules">Модули кораблей: клиент считает по ним скорость, щит, радар и энергию, как сервер.</param>
/// <param name="Market">
/// Правила рынка этой станции (M12): по ним клиент считает цену пачки той же формулой, что и сервер.
/// Живые цены сюда не кладутся — они приходят отдельным <see cref="MarketMsg"/>. null — рынка здесь нет.
/// </param>
public sealed record WelcomeMsg(
    int Id,
    int TickRate,
    int Version,
    IReadOnlyDictionary<string, HullParams> Hulls,
    IReadOnlyDictionary<string, WeaponParams> Weapons,
    CombatRules Combat,
    bool Resumed,
    NpcRules? Npcs = null,
    LootRules? Loot = null,
    MeteorRules? Meteors = null,
    ShopRules? Shop = null,
    SystemDto? System = null,
    GalaxyDto? Galaxy = null,
    IReadOnlyDictionary<string, ModuleParams>? Modules = null,
    MarketRules? Market = null,
    ReputationRules? Reputation = null) : ServerMessage;

/// <summary>Врата в системе: куда ведут, как называется та система и сколько топлива стоит прыжок.</summary>
public sealed record GateDto(string To, string Name, double X, double Y, int Cost);

/// <param name="Pvp">off — PvP нет; border — нет у станции; free — везде (GDD §34).</param>
/// <param name="Station">В системе есть станция; иначе дока и укрытия нет.</param>
/// <param name="Seed">Небо системы.</param>
/// <param name="Core">Радиус укрытия у станции (PvP в border-системе и пираты сюда не заходят); 0 — укрытия нет.</param>
/// <param name="GateRange">Ближе этого к вратам можно начать прыжок.</param>
/// <param name="JumpSeconds">Подготовка прыжка.</param>
/// <param name="Sun">Звезда в центре; null — её нет.</param>
/// <param name="StationOrbit">Орбита станции (радиус 0 — станция в центре).</param>
/// <param name="Planets">Планеты на орбитах.</param>
/// <param name="PirateBase">Пиратская база: отсюда вылетают налётчики пиратской системы; null — её нет.</param>
/// <param name="OrbitEpoch">
/// Орбитальное время в тик 0 этой системы, секунды: клиент считает орбиты от тика снапшота той же формулой,
/// что и сервер (<see cref="OrbitDef"/>).
/// </param>
public sealed record SystemDto(
    string Id,
    string Name,
    int Danger,
    string Pvp,
    bool Station,
    int Seed,
    double Core,
    double GateRange,
    double JumpSeconds,
    IReadOnlyList<GateDto> Gates,
    SunDef? Sun,
    OrbitDef StationOrbit,
    IReadOnlyList<PlanetDef> Planets,
    double OrbitEpoch,
    PirateBase? PirateBase = null,
    /// <summary>Картинка станции (M11); null — по опасности системы, как до M11.</summary>
    string? StationSprite = null,
    /// <summary>Свой набор фонов дока (M12); null — общие сцены станции.</summary>
    string? DockScene = null,
    /// <summary>Регион галактики (M11); null — регионов нет.</summary>
    string? Region = null);

/// <summary>Система на карте галактики (GDD §55).</summary>
/// <param name="Region">Регион (M11): ядро, пограничье, рубеж; null — регионов нет.</param>
/// <param name="Station">В системе есть станция. Не то же, что «сюда можно сесть»: см. <paramref name="Places"/>.</param>
/// <param name="Places">
/// Места системы (M15): станция и поселения. Нужны, чтобы назвать адрес доставки словами и чтобы карта
/// не врала «дока нет» там, где есть поселение. Пусто — сесть в системе негде.
/// </param>
public sealed record GalaxySystemDto(
    string Id,
    string Name,
    int Danger,
    string Pvp,
    bool Station,
    double X,
    double Y,
    string? Region = null,
    IReadOnlyList<PlaceNameDto>? Places = null);

/// <summary>Имя места для карты и текста заданий (M15).</summary>
public sealed record PlaceNameDto(string Key, string Name);

/// <summary>Регион галактики на карте (M11).</summary>
public sealed record RegionDto(string Id, string Name, string Color);

/// <param name="Cost">Топлива на прыжок в любую сторону.</param>
public sealed record LinkDto(string A, string B, int Cost);

public sealed record GalaxyDto(
    IReadOnlyList<GalaxySystemDto> Systems,
    IReadOnlyList<LinkDto> Links,
    IReadOnlyList<RegionDto>? Regions = null);

public sealed record PongMsg(double C, long Tick) : ServerMessage;

/// <summary>Весь список кораблей с именами — игроки и NPC; присылается при любом изменении (вход, выход, обрыв, смена ника).</summary>
/// <param name="Players">Корабли этой системы.</param>
/// <param name="Total">Пилотов на связи во всей галактике.</param>
public sealed record PlayersMsg(IReadOnlyList<PlayerDto> Players, int Total = 0) : ServerMessage;

/// <param name="Online">false — связи нет, корабль висит в космосе и ждёт игрока.</param>
/// <param name="Npc">Дрон или другой NPC: о нём не пишут в ленту и не считают в «онлайн».</param>
/// <param name="MaxHp">Своя прочность NPC вместо корпусной; нет — как у корпуса.</param>
/// <param name="MaxSh">Свой щит NPC вместо корпусного; нет — как у корпуса.</param>
/// <param name="Kind">
/// Вид NPC: <see cref="Protocol.DroneKind"/>, <see cref="Protocol.PirateKind"/>, <see cref="Protocol.RangerKind"/>,
/// <see cref="Protocol.TraderKind"/>, <see cref="Protocol.ConvoyKind"/> или <see cref="Protocol.WingKind"/>;
/// у игроков нет.
/// </param>
public sealed record PlayerDto(
    int Id,
    string Name,
    bool Online,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Npc = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxHp = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxSh = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null);

/// <summary>Файлы баланса изменились на диске.</summary>
public sealed record ConfigMsg(
    IReadOnlyDictionary<string, HullParams> Hulls,
    IReadOnlyDictionary<string, WeaponParams> Weapons,
    CombatRules Combat,
    NpcRules? Npcs = null,
    LootRules? Loot = null,
    MeteorRules? Meteors = null,
    ShopRules? Shop = null,
    SystemDto? System = null,
    GalaxyDto? Galaxy = null,
    IReadOnlyDictionary<string, ModuleParams>? Modules = null,
    MarketRules? Market = null,
    ReputationRules? Reputation = null) : ServerMessage;

/// <summary>Вход принят. Приходит раньше <see cref="WelcomeMsg"/>.</summary>
/// <param name="Name">Ник аккаунта так, как он записан на сервере.</param>
/// <param name="Key">Новый ключ устройства — только после входа по паролю; клиент хранит его вместо пароля.</param>
public sealed record AccountMsg(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null) : ServerMessage;

/// <summary>Вход отклонён; следом сервер закрывает соединение с кодом <see cref="Protocol.DeniedCloseCode"/>.</summary>
/// <param name="Code">Причина: <see cref="Protocol.BadNameDenied"/> и соседние.</param>
public sealed record DeniedMsg(string Code) : ServerMessage;

/// <summary>
/// Ангар пилота (GDD §51) — только своему соединению. Шлётся при входе, стыковке, покупке, смене оснащения и ремонте.
/// </summary>
/// <param name="Hull">Активный корпус.</param>
/// <param name="Fit">Что стоит на корабле: пушки по слотам и модули.</param>
/// <param name="Hulls">Свои корпуса; у гостя — все.</param>
/// <param name="Storage">Склад: пушки и модули, которые куплены или сняты и сейчас не стоят, — id и сколько.</param>
/// <param name="Docked">Корабль в доке: в космосе его нет, экран станции открыт.</param>
/// <param name="Hp">Прочность корпуса, округлена вверх: в доке снапшот о своём корабле молчит.</param>
/// <param name="MaxHp">Полная прочность активного корпуса.</param>
/// <param name="Fuel">Топливо в баке (GDD §6). Меняется только прыжком и заправкой — тогда hangar приходит снова.</param>
/// <param name="MaxFuel">Бак активного корпуса.</param>
/// <param name="Home">Система последней стыковки: здесь корабль появится после гибели и после входа.</param>
/// <param name="Power">Сколько энергии забирает оснащение (GDD §18).</param>
/// <param name="PowerMax">Сколько даёт генератор; 0 — энергию не считают (баланс без modules.json).</param>
/// <param name="Guest">Гость: склада нет, ставить можно что угодно где угодно.</param>
/// <param name="Place">Где корабль стоит (M15): ключ места; null — в космосе.</param>
public sealed record HangarMsg(
    string Hull,
    ShipFit Fit,
    IReadOnlyList<string> Hulls,
    IReadOnlyDictionary<string, int> Storage,
    bool Docked,
    int Hp,
    int MaxHp,
    int Fuel = 0,
    int MaxFuel = 0,
    string? Home = null,
    int Power = 0,
    int PowerMax = 0,
    bool Guest = false,
    PlaceDto? Place = null) : ServerMessage;

/// <summary>
/// Место, где стоит корабль (M15): станция или поселение на планете. Клиент по нему выбирает фон дока,
/// заголовок и то, какие вкладки показывать. Всё остальное о месте он уже знает из <see cref="SystemDto"/>.
/// </summary>
/// <param name="Key">Ключ места: «st:vega», «pl:terra».</param>
/// <param name="Kind">«st» — станция, «pl» — поселение.</param>
/// <param name="Name">Как его звать на экране.</param>
/// <param name="Scene">Набор фонов дока; null — общий по виду места.</param>
/// <param name="Shipyard">Здесь продают и меняют корпуса; false — вкладки верфи нет.</param>
public sealed record PlaceDto(string Key, string Kind, string Name, string? Scene, bool Shipyard);

/// <summary>
/// Трюм игрока (GDD §21) — только своему соединению: снапшот один на всех, личному месту в нём нет.
/// Шлётся по событию (подбор, вход, смена корпуса, правка баланса, сдача груза), а не каждый тик.
/// </summary>
/// <param name="Used">Занято объёма.</param>
/// <param name="Max">Ёмкость трюма текущего корпуса.</param>
/// <param name="Items">Что лежит: идентификатор предмета — количество.</param>
/// <param name="Credits">Кредиты пилота.</param>
/// <param name="Reserved">Из занятого — груз доставки: его не продать и не выбросить.</param>
public sealed record CargoMsg(
    double Used,
    double Max,
    IReadOnlyDictionary<string, int> Items,
    int Credits = 0,
    int Reserved = 0) : ServerMessage;

/// <summary>Строка рынка станции (M12).</summary>
/// <param name="Id">Товар — из loot.json: название, объём и цвет редкости клиент берёт оттуда.</param>
/// <param name="Buy">Сколько пилот платит за штуку прямо сейчас.</param>
/// <param name="Sell">Сколько пилот получает за штуку.</param>
/// <param name="Stock">Запас станции, штук: на него и смотрит формула цены.</param>
/// <param name="Norm">Равновесный запас — по нему видно, здесь «мало» или «много».</param>
public sealed record MarketItemDto(string Id, int Buy, int Sell, int Stock, int Norm);

/// <summary>
/// Слух торговца (M12): куда везти товар или где его дёшево взять. Текст собирает клиент — здесь только
/// факты, как у <see cref="MissionOffer"/>. Берутся из настоящих цен соседних станций.
/// </summary>
/// <param name="Kind"><see cref="Rumours.RouteKind"/> — брать здесь и везти туда; <see cref="Rumours.GlutKind"/> — там этого навалом.</param>
/// <param name="Name">Название той системы: на экране дока клиенту его больше неоткуда взять.</param>
/// <param name="Price">Цена штуки там; <paramref name="Profit"/> — сколько выходит с штуки.</param>
/// <param name="Scarce">Там этого сейчас мало — отсюда разговоры про эпидемию и голод.</param>
public sealed record RumourDto(
    string Kind,
    string Good,
    string System,
    string Name,
    int Hops,
    int Price,
    int Profit = 0,
    bool Scarce = false);

/// <summary>
/// Живые цены станции (M12) — только тому, кто в доке: у каждой станции рынок свой, а в космосе он не нужен.
/// Шлётся по событию: стыковка, сделка, приход торговца, возврат запасов к норме, правка баланса.
/// </summary>
/// <param name="Rumours">О чём судачит здешний торговец; считается на стыковке и дальше не меняется.</param>
/// <param name="Station">
/// Профиль этого места — что оно производит и что скупает (M15.5). В welcome едет профиль главного места
/// системы, поэтому без этого поля клиент на поселении считал бы цену по чужой витрине.
/// </param>
/// <param name="Demand">Спрос события (M15.5); null — здесь его нет.</param>
public sealed record MarketMsg(
    string System,
    IReadOnlyList<MarketItemDto> Items,
    IReadOnlyList<RumourDto>? Rumours = null,
    MarketStation? Station = null,
    DemandQuoteDto? Demand = null) : ServerMessage;

/// <summary>
/// Спрос события на этом месте (M15.5). Множитель — уже посчитанный скаляр: клиент считает цену той же
/// формулой, что и сервер, и внутри сделки он не меняется. Квота убывает — следующая рассылка привезёт новый.
/// </summary>
/// <param name="Left">Сколько единиц ещё примут.</param>
public sealed record DemandQuoteDto(
    string Case,
    string Title,
    IReadOnlyList<string> Goods,
    double Mul,
    int Left,
    int Quota);

/// <summary>
/// Событие спроса (M15.5) — на всю галактику, как вторжение: объявлено, открыто, закрыто или погасло.
/// </summary>
/// <param name="State">announce | open | filled | over.</param>
/// <param name="SecondsLeft">Сколько осталось до открытия приёмки или до конца срока.</param>
public sealed record DemandMsg(
    string State,
    string System,
    string SystemName,
    string Place,
    string PlaceName,
    string Case,
    string Title,
    IReadOnlyList<string> Goods,
    int SecondsLeft,
    int Left,
    int Quota,
    double Mul) : ServerMessage;

/// <summary>Отношение к пилоту здесь и сейчас (M13); null — в этой системе станции нет.</summary>
/// <param name="Place">Ключ станции, например «st:vega».</param>
/// <param name="Value">Очки станции.</param>
/// <param name="Level">Ступень станции: по ней идёт скидка или наценка на снаряжение и ремонт.</param>
/// <param name="Gate">
/// Ступень, по которой решают, что выложить на витрину: лучшее из станции и среднего по региону.
/// Считает сервер — клиенту незачем повторять региональную арифметику, разойтись в ней проще, чем сойтись.
/// </param>
/// <param name="System">Очки системы.</param>
/// <param name="SystemLevel">Ступень системы: по ней закрывается док и звереют рейнджеры.</param>
/// <param name="Region">Среднее по региону, округлённое.</param>
public sealed record RepHereDto(
    string Place,
    int Value,
    string Level,
    string Gate,
    int System,
    string SystemLevel,
    int Region);

/// <summary>Одна строка журнала: «−15 Vega: атака торговца». Текст собирает клиент по Code (M13).</summary>
/// <param name="Code">Повод: <see cref="Protocol.RepTraderKill"/> и соседние.</param>
/// <param name="Delta">На сколько изменилось; 0 сюда не попадает.</param>
/// <param name="Key">Чьё отношение: «sys:vega» или «st:vega».</param>
/// <param name="Value">Сколько стало.</param>
public sealed record RepChangeDto(string Code, int Delta, string Key, int Value);

/// <summary>
/// Репутация пилота (M13) — только своему соединению: она у игрока, а не у системы. Отдельным сообщением,
/// а не полем ангара: цвет систем на карте и предупреждение «Враг» нужны и в полёте, а ангар шлётся
/// только по событиям дока.
/// </summary>
/// <param name="Systems">Очки по системам; только ненулевые — из них красится карта галактики.</param>
/// <param name="Places">Очки по станциям; только ненулевые.</param>
/// <param name="Change">Что только что изменилось; null — это полное состояние без повода.</param>
public sealed record RepMsg(
    IReadOnlyDictionary<string, int> Systems,
    IReadOnlyDictionary<string, int> Places,
    RepHereDto? Here = null,
    RepChangeDto? Change = null) : ServerMessage;

/// <summary>Текущий шаг обучения (GDD §54).</summary>
/// <param name="Step">Номер шага с нуля.</param>
/// <param name="Total">Шагов всего.</param>
/// <param name="Id">Что засчитывает шаг: <see cref="MissionRules.TutorialIds"/>.</param>
public sealed record TutorialDto(int Step, int Total, string Id, string Title, string Hint);

/// <summary>Что только что сделано — для строки в ленте.</summary>
/// <param name="Kind">
/// <see cref="Protocol.TutorialDone"/> — шаг обучения; <see cref="Protocol.MissionDone"/> — задание сдано;
/// <see cref="Protocol.MissionFailed"/> — провалено (M14).
/// </param>
/// <param name="Title">Шаг обучения — его текст.</param>
/// <param name="Mission">Сданное или проваленное задание.</param>
/// <param name="Last">Это был последний шаг обучения.</param>
/// <param name="Reason">Почему провалено (<see cref="Protocol.TraderFail"/> и прочие); null — не провалено (M14).</param>
public sealed record MissionDoneDto(
    string Kind,
    int Reward,
    string? Title = null,
    MissionOffer? Mission = null,
    bool Last = false,
    string? Reason = null);

/// <summary>
/// Куда смотреть по «живому» заданию (M14): Ship — за этим кораблём идти (конвой), 0 — точка (X, Y),
/// текущая цель маршрута патруля. Живёт в сообщении, а не в <see cref="ActiveMission"/>: номера кораблей —
/// дело комнаты, в аккаунт им нельзя.
/// </summary>
public sealed record MissionMarkDto(int Ship, double X, double Y);

/// <summary>
/// Обучение и задания пилота — только ему. Шлётся по событию: вход, прыжок, стыковка, прогресс, правка баланса.
/// </summary>
/// <param name="Tutorial">Текущий шаг обучения; null — обучения нет.</param>
/// <param name="Active">Взятое задание; null — нет.</param>
/// <param name="Offers">Доска станции этой системы; в системе без станции пусто.</param>
/// <param name="Done">Что сделано этим событием; null — просто обновление.</param>
/// <param name="Mark">Куда смотреть по живому заданию (M14); null — метки нет.</param>
public sealed record MissionsMsg(
    TutorialDto? Tutorial,
    ActiveMission? Active,
    IReadOnlyList<MissionOffer> Offers,
    MissionDoneDto? Done = null,
    MissionMarkDto? Mark = null) : ServerMessage;

/// <summary>Короткое уведомление игроку по коду; текст подставляет клиент (см. ui/feed.ts).</summary>
public sealed record NoticeMsg(string Code) : ServerMessage;

/// <summary>
/// SOS торговца всем пилотам системы: на него напали (<see cref="Protocol.SosOn"/> — и потом раз в секунду, где он),
/// отбился (<see cref="Protocol.SosSaved"/>) или погиб (<see cref="Protocol.SosLost"/>).
/// </summary>
/// <param name="Reward">Кредиты этому пилоту за помощь — только в «спасён» и только тем, кто помогал.</param>
public sealed record SosMsg(int Id, string Name, double X, double Y, string State, int Reward = 0) : ServerMessage;

/// <summary>Пилот From зовёт в группу; ответ — <see cref="PartyMsg"/> accept или decline в течение Seconds.</summary>
public sealed record PartyInviteMsg(int From, string Name, double Seconds) : ServerMessage;

/// <summary>Участник группы — где он и цел ли; расстояние клиент считает сам, если тот в той же системе.</summary>
/// <param name="System">Id системы; SystemName — её имя для панели.</param>
public sealed record PartyMemberDto(
    int Id, string Name, string System, string SystemName, double X, double Y,
    int Hp, int MaxHp, int Sh, int MaxSh, bool Online, bool Dead, bool Docked);

/// <summary>Своя группа: при каждом изменении состава и раз в statusSeconds. Пустой список — не в группе.</summary>
public sealed record PartyStateMsg(int Leader, IReadOnlyList<PartyMemberDto> Members) : ServerMessage;

/// <summary>Событие группы для ленты (<see cref="PartyCodes"/>): текст подставляет клиент, Name — о ком.</summary>
public sealed record PartyEventMsg(string Code, string? Name = null) : ServerMessage;

/// <summary>Награда за голову пирата (GDD §31): Amount — своя доля, Shared — на скольких её поделили.</summary>
public sealed record BountyMsg(int Amount, int Shared, string Name) : ServerMessage;

/// <summary>Строка итогов вторжения: кто, сколько урона пиратам, сколько получил.</summary>
public sealed record InvasionScoreDto(string Name, int Damage, int Reward);

/// <summary>
/// «Вторжение пиратов» (GDD §38) — всем пилотам галактики: раз в секунду, пока оно объявлено или идёт, и итог в конце.
/// </summary>
/// <param name="State">
/// <see cref="Protocol.InvasionAnnounce"/> — скоро (SecondsLeft до начала); <see cref="Protocol.InvasionWave"/> — идёт
/// (SecondsLeft до конца, NextIn — до следующей волны, если текущая зачищена); <see cref="Protocol.InvasionWon"/> и
/// <see cref="Protocol.InvasionLost"/> — итог.
/// </param>
/// <param name="X">Точка сбора пиратов у станции (0, 0 — ещё не известна).</param>
/// <param name="Results">Итог: участники по убыванию урона (не больше десяти).</param>
/// <param name="Reward">Итог: своя доля, кредиты.</param>
public sealed record InvasionMsg(
    string State,
    string System,
    string SystemName,
    int SecondsLeft,
    int Wave = 0,
    int Waves = 0,
    int Remaining = 0,
    int NextIn = 0,
    double X = 0,
    double Y = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<InvasionScoreDto>? Results = null,
    int Reward = 0,
    int Damage = 0) : ServerMessage;

/// <param name="Shots">Выстрелы этого тика; нет — поле не пишется.</param>
/// <param name="Kills">Уничтоженные в этом тике.</param>
/// <param name="Loot">Предметы, лежащие в космосе.</param>
/// <param name="Picks">Подобранное в этом тике — видно всем: чужой луч объясняет, куда делся предмет.</param>
/// <param name="Meteors">Метеориты в системе. Отдельно от кораблей: у них нет корпуса, пушки и места в ростере.</param>
/// <param name="Missiles">Ракеты в полёте.</param>
public sealed record SnapshotMsg(
    long Tick,
    IReadOnlyList<ShipDto> Ships,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ShotDto>? Shots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<KillDto>? Kills = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LootDto>? Loot = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PickDto>? Picks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MeteorDto>? Meteors = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MissileDto>? Missiles = null) : ServerMessage;

/// <param name="Th">Тяга последнего входа — для пламени двигателя у чужих кораблей.</param>
/// <param name="Ack">Последний применённый seq владельца: состояние — ровно после этого входа.</param>
/// <param name="Hp">Корпус, округлён вверх.</param>
/// <param name="Sh">Щит, округлён вверх.</param>
/// <param name="W">Первая стоящая пушка; "" — пушек нет.</param>
/// <param name="Rt">Корабль уничтожен и появится в этот тик; 0 — цел.</param>
/// <param name="Pu">Под защитой до этого тика; 0 — без защиты.</param>
/// <param name="Tg">Цель пирата в бою; 0 — нет (и у игроков).</param>
/// <param name="Ai">Состояние ИИ пирата: patrol, attack, return; у игроков нет.</param>
/// <param name="J">Готовится гиперпрыжок: корабль уйдёт из системы в этот тик; 0 — нет.</param>
public sealed record ShipDto(
    int Id,
    double X,
    double Y,
    double R,
    double Vx,
    double Vy,
    string Hull,
    double Th,
    int Ack,
    int Hp,
    int Sh,
    string W,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Rt = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Pu = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Tg = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ai = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long J = 0,
    /// <summary>Тик, до которого корабль замедлен ионкой (M11); 0 — не замедлен.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Sl = 0);

/// <param name="Dmg">Урон всего (0 при промахе).</param>
/// <param name="Sh">Из него пришлось на щит.</param>
/// <param name="Ch">Шанс попадания, %, по которому бросал сервер.</param>
public sealed record ShotDto(int From, int To, string W, bool Hit, int Dmg, int Sh, double Ch);

/// <param name="By">Кто нанёс смертельный удар.</param>
public sealed record KillDto(int Id, int By);

/// <summary>Предмет в космосе. Поля короткие: снапшот один на всех и уходит 20 раз в секунду.</summary>
/// <param name="I">Идентификатор предмета из loot.json.</param>
/// <param name="N">Количество в стопке.</param>
/// <param name="E">Тик, когда предмет исчезнет: клиент сам считает, когда мигать.</param>
/// <param name="C">Предмет из контейнера, а не обломки: рисуется ящиком и не протухает.</param>
public sealed record LootDto(
    int Id,
    double X,
    double Y,
    string I,
    int N,
    long E,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool C = false);

/// <param name="By">Чей тракторный луч забрал предмет.</param>
public sealed record PickDto(int By, int Id, string I, int N);

/// <summary>
/// Метеорит. Летит строго по прямой с постоянной скоростью, поэтому клиент считает положение как x + vx·Δt —
/// точно, без буфера кадров. Радиус и максимум прочности клиент берёт из meteors.json по размеру.
/// </summary>
/// <param name="S">Размер — ключ sizes в meteors.json.</param>
/// <param name="Hp">Прочность, округлена вверх.</param>
public sealed record MeteorDto(int Id, double X, double Y, double Vx, double Vy, string S, int Hp);

/// <summary>
/// Ракета в полёте (боевой документ §37). Скорость — из пушки W, курс — R: клиент ведёт её сам между кадрами.
/// </summary>
/// <param name="R">Курс, как у корабля: 0 — нос вверх.</param>
/// <param name="O">Кто запустил.</param>
/// <param name="T">В кого летит.</param>
/// <param name="W">Ракетница — ключ weapons.json.</param>
public sealed record MissileDto(int Id, double X, double Y, double R, int O, int T, string W);

public static class Protocol
{
    /// <summary>
    /// Меняется, когда клиент и сервер разных версий уже не поймут друг друга
    /// (3 — бой, M3; 4 — пираты, M4; 5 — лут и трюм, M5a; 6 — ручной подбор и продажа груза; 7 — метеориты, M5b;
    /// 8 — аккаунты, док и магазин станции, M6; 9 — системы, врата, топливо, радар и бинарные дельта-снапшоты, M7;
    /// 10 — звезда, орбиты и налёты; 11 — задания и обучение, M8; 12 — слоты, модули, ракеты, торговцы, M9; 13 — SOS торговцев;
    /// 14 — группы, награда за голову и вторжения, M10;
    /// 15 — переключатель PvP; 16 — регионы, тиры Mk1–Mk3, utility-слоты, новое оружие и замедление, M11;
    /// 17 — рынок товаров, покупка груза, живые цены, M12; 18 — репутация систем и станций, M13;
    /// 19 — сопровождение, патруль, важное письмо, охота на метеориты и провал задания, M14;
    /// 20 — посадка на планеты: место как общее понятие дока, поселения, их рынок и репутация, M15;
    /// 21 — выброс груза за борт; 22 — урон по площади, M15.5).
    /// Зеркало PROTOCOL_VERSION в client/src/net/protocol.ts.
    /// </summary>
    public const int Version = 22;

    public const string DroneKind = "drone";
    public const string PirateKind = "pirate";
    public const string TraderKind = "trader";
    public const string RangerKind = "ranger";
    /// <summary>Конвой задания «сопровождение» и звено задания «патруль» (M14): у них свои корабли.</summary>
    public const string ConvoyKind = "convoy";
    public const string WingKind = "wing";

    /// <summary>Что покупают в доке (<see cref="BuyMsg.Kind"/>).</summary>
    public const string HullItem = "hull";
    public const string ItemKind = "item";

    /// <summary>Коды уведомлений (<see cref="NoticeMsg"/>); текст подставляет клиент.</summary>
    public const string CargoFullNotice = "cargoFull";
    /// <summary>Корабль уничтожен — трюм высыпался в космос.</summary>
    public const string CargoLostNotice = "cargoLost";
    public const string UnloadedNotice = "unloaded";
    public const string TooFarNotice = "tooFar";
    public const string NoCreditsNotice = "noCredits";
    public const string NotSoldNotice = "notSold";
    public const string NoFuelNotice = "noFuel";
    public const string GateFarNotice = "gateFar";
    public const string JumpCancelledNotice = "jumpCancelled";
    /// <summary>Подготовку прыжка сбило попадание.</summary>
    public const string JumpHitNotice = "jumpHit";
    public const string NoPowerNotice = "noPower";
    public const string BadClassNotice = "badClass";
    public const string BadSlotNotice = "badSlot";
    /// <summary>Рейнджеры пошли на пилота: он напал на торговца.</summary>
    public const string RangersNotice = "rangers";
    /// <summary>Этим товаром здесь не торгуют: чужой регион или контрабанда (M12).</summary>
    public const string NoGoodsNotice = "noGoods";
    /// <summary>На складе станции столько нет (M12).</summary>
    public const string NoStockNotice = "noStock";
    /// <summary>Док закрыт: в этой системе пилота считают врагом (M13).</summary>
    public const string DockClosedNotice = "dockClosed";
    /// <summary>Груз выброшен за борт (M15.1).</summary>
    public const string JettisonedNotice = "jettisoned";
    /// <summary>Здесь нет верфи: корабль меняют не в каждом поселении (M15).</summary>
    public const string NoShipyardNotice = "noShipyard";
    /// <summary>Это продают только своим — не хватает репутации места (M13).</summary>
    public const string NeedRepNotice = "needRep";
    /// <summary>Пилот отстал от конвоя: вернуться, пока задание не провалено (M14).</summary>
    public const string MissionAwayNotice = "missionAway";
    /// <summary>Впереди засада на конвой (M14).</summary>
    public const string AmbushNotice = "ambush";
    /// <summary>Звено рейнджеров вышло вместе с пилотом (M14).</summary>
    public const string WingNotice = "wing";

    /// <summary>За что начислена или снята репутация (<see cref="RepChangeDto.Code"/>; M13).</summary>
    public const string RepMissionDone = "missionDone";
    public const string RepMissionAbandon = "missionAbandon";
    public const string RepMissionFail = "missionFail";
    public const string RepPirate = "pirate";
    public const string RepSos = "sos";
    public const string RepInvasion = "invasion";
    public const string RepTraderAttack = "traderAttack";
    public const string RepTraderKill = "traderKill";
    public const string RepRangerAttack = "rangerAttack";
    public const string RepRangerKill = "rangerKill";
    public const string RepPlayerKill = "playerKill";

    /// <summary>Состояния SOS торговца (<see cref="SosMsg.State"/>).</summary>
    public const string SosOn = "on";
    public const string SosSaved = "saved";
    public const string SosLost = "lost";

    /// <summary>Состояния вторжения (<see cref="InvasionMsg.State"/>).</summary>
    public const string InvasionAnnounce = "announce";
    public const string InvasionWave = "wave";
    public const string InvasionWon = "won";
    public const string InvasionLost = "lost";

    /// <summary>Действия с заданиями (<see cref="MissionMsg.Action"/>).</summary>
    public const string AcceptMission = "accept";
    public const string AbandonMission = "abandon";
    public const string CompleteMission = "complete";
    public const string SkipTutorial = "skip";

    /// <summary>Что сделано (<see cref="MissionDoneDto.Kind"/>).</summary>
    public const string TutorialDone = "tutorial";
    public const string MissionDone = "mission";
    public const string MissionFailed = "failed";

    /// <summary>Почему задание провалено (<see cref="MissionDoneDto.Reason"/>; M14).</summary>
    public const string TraderFail = "trader";
    public const string AwayFail = "away";
    public const string WingFail = "wing";
    public const string DeadFail = "dead";
    public const string LeftFail = "left";
    public const string TimeFail = "time";
    /// <summary>Налётчики добрались до поселения: оборона сорвана (M15).</summary>
    public const string RaidFail = "raid";

    /// <summary>Причины отказа во входе (<see cref="DeniedMsg"/>).</summary>
    public const string BadNameDenied = "badName";
    public const string BadPasswordDenied = "badPassword";
    public const string WrongPasswordDenied = "wrongPassword";
    public const string BadKeyDenied = "badKey";
    /// <summary>Такого пути нет или он ещё закрыт — например, пират (M15.5).</summary>
    public const string BadCareerDenied = "badCareer";

    /// <summary>Состояния события спроса (<see cref="DemandMsg.State"/>, M15.5).</summary>
    public const string DemandAnnounce = "announce";
    public const string DemandOpen = "open";
    public const string DemandFilled = "filled";
    public const string DemandOver = "over";

    /// <summary>Код закрытия WebSocket после <see cref="DeniedMsg"/>: клиент не переподключается сам, а ждёт пилота.</summary>
    public const int DeniedCloseCode = 4003;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
    };

    public static byte[] Encode(ServerMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    /// <returns>Сообщение или null, если пришёл мусор или неизвестный тип.</returns>
    public static ClientMessage? TryDecode(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<ClientMessage>(utf8, Json);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Действия <see cref="PartyMsg"/> и коды <see cref="PartyEventMsg"/>.</summary>
public static class PartyCodes
{
    public const string InviteAction = "invite";
    public const string AcceptAction = "accept";
    public const string DeclineAction = "decline";
    public const string LeaveAction = "leave";

    /// <summary>Приглашение отправлено пилоту Name.</summary>
    public const string Invited = "invited";
    /// <summary>Name вступил в группу; себе — «вы в группе».</summary>
    public const string Joined = "joined";
    /// <summary>Name вышел из группы (или из игры).</summary>
    public const string Left = "left";
    /// <summary>Name отклонил приглашение.</summary>
    public const string Declined = "declined";
    /// <summary>Name не ответил вовремя, или приглашения уже нет.</summary>
    public const string Expired = "expired";
    /// <summary>В группе нет мест.</summary>
    public const string Full = "full";
    /// <summary>Name уже в группе.</summary>
    public const string Busy = "busy";
    /// <summary>Такого пилота нет в игре.</summary>
    public const string Gone = "gone";
    /// <summary>Группа распалась: в ней остались вы один.</summary>
    public const string Disbanded = "disbanded";
}
