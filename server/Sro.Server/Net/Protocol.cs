using System.Text.Json;
using System.Text.Json.Serialization;
using Sro.Sim;

namespace Sro.Server.Net;

// Протокол — JSON-объекты с полем "t" (тип сообщения). Зеркало на клиенте: client/src/net/protocol.ts.

// Клиент → сервер
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMsg), "hello")]
[JsonDerivedType(typeof(PingMsg), "ping")]
[JsonDerivedType(typeof(InputMsg), "input")]
[JsonDerivedType(typeof(HullMsg), "hull")]
[JsonDerivedType(typeof(WeaponMsg), "weapon")]
[JsonDerivedType(typeof(NameMsg), "name")]
[JsonDerivedType(typeof(TargetMsg), "target")]
[JsonDerivedType(typeof(FireMsg), "fire")]
[JsonDerivedType(typeof(LootTargetMsg), "loot")]
[JsonDerivedType(typeof(GrabMsg), "grab")]
[JsonDerivedType(typeof(SellMsg), "sell")]
[JsonDerivedType(typeof(DockMsg), "dock")]
[JsonDerivedType(typeof(BuyMsg), "buy")]
[JsonDerivedType(typeof(RepairMsg), "repair")]
[JsonDerivedType(typeof(JumpMsg), "jump")]
[JsonDerivedType(typeof(RefuelMsg), "refuel")]
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
public sealed record HelloMsg(
    string? Name,
    string? Hull,
    string? Token,
    string? Weapon = null,
    string? Password = null,
    string? Key = null) : ClientMessage;

/// <param name="C">Время клиента, возвращается в pong как есть для замера RTT.</param>
public sealed record PingMsg(double C) : ClientMessage;

/// <summary>Только управление (§49): направление на экране и тяга. Координаты клиент не присылает.</summary>
public sealed record InputMsg(int Seq, double Dx, double Dy, double Th) : ClientMessage;

/// <summary>Поставить корпус из ангара: пилоту с аккаунтом — только свой и только в доке, гостю — любой.</summary>
public sealed record HullMsg(string? Id) : ClientMessage;

/// <summary>Поставить пушку: пилоту с аккаунтом — только свою и только в доке, гостю — любую.</summary>
public sealed record WeaponMsg(string? Id) : ClientMessage;

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

/// <summary>Продать груз в доке; Item — что именно, null — весь трюм.</summary>
public sealed record SellMsg(string? Item = null) : ClientMessage;

/// <summary>Пристыковаться к станции (On) или вылететь из дока.</summary>
public sealed record DockMsg(bool On) : ClientMessage;

/// <summary>Купить в доке корпус или пушку (GDD §26); купленное сразу ставится на корабль.</summary>
/// <param name="Kind"><see cref="Protocol.HullItem"/> или <see cref="Protocol.WeaponItem"/>.</param>
public sealed record BuyMsg(string? Kind, string? Id) : ClientMessage;

/// <summary>Починить корпус в доке и зарядить щит — по цене repairPrice из shop.json.</summary>
public sealed record RepairMsg : ClientMessage;

/// <summary>Начать гиперпрыжок через врата в систему To (GDD §5); To = null — отменить подготовку.</summary>
public sealed record JumpMsg(string? To) : ClientMessage;

/// <summary>Заправить бак в доке до полного — по fuelPrice из shop.json (GDD §6, §26).</summary>
public sealed record RefuelMsg : ClientMessage;

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
[JsonDerivedType(typeof(DeniedMsg), "denied")]
[JsonDerivedType(typeof(HangarMsg), "hangar")]
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
    GalaxyDto? Galaxy = null) : ServerMessage;

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
    PirateBase? PirateBase = null);

/// <summary>Система на карте галактики (GDD §55).</summary>
public sealed record GalaxySystemDto(string Id, string Name, int Danger, string Pvp, bool Station, double X, double Y);

/// <param name="Cost">Топлива на прыжок в любую сторону.</param>
public sealed record LinkDto(string A, string B, int Cost);

public sealed record GalaxyDto(IReadOnlyList<GalaxySystemDto> Systems, IReadOnlyList<LinkDto> Links);

public sealed record PongMsg(double C, long Tick) : ServerMessage;

/// <summary>Весь список кораблей с именами — игроки и NPC; присылается при любом изменении (вход, выход, обрыв, смена ника).</summary>
/// <param name="Players">Корабли этой системы.</param>
/// <param name="Total">Пилотов на связи во всей галактике.</param>
public sealed record PlayersMsg(IReadOnlyList<PlayerDto> Players, int Total = 0) : ServerMessage;

/// <param name="Online">false — связи нет, корабль висит в космосе и ждёт игрока.</param>
/// <param name="Npc">Дрон или другой NPC: о нём не пишут в ленту и не считают в «онлайн».</param>
/// <param name="MaxHp">Своя прочность NPC вместо корпусной; нет — как у корпуса.</param>
/// <param name="MaxSh">Свой щит NPC вместо корпусного; нет — как у корпуса.</param>
/// <param name="Kind">Вид NPC: <see cref="Protocol.DroneKind"/> или <see cref="Protocol.PirateKind"/>; у игроков нет.</param>
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
    GalaxyDto? Galaxy = null) : ServerMessage;

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
/// Ангар пилота (GDD §51) — только своему соединению. Шлётся при входе, стыковке, покупке, смене корабля и ремонте.
/// </summary>
/// <param name="Hull">Активный корпус.</param>
/// <param name="Weapon">Активная пушка.</param>
/// <param name="Hulls">Свои корпуса; у гостя — все.</param>
/// <param name="Weapons">Свои пушки; у гостя — все.</param>
/// <param name="Docked">Корабль в доке: в космосе его нет, экран станции открыт.</param>
/// <param name="Hp">Прочность корпуса, округлена вверх: в доке снапшот о своём корабле молчит.</param>
/// <param name="MaxHp">Полная прочность активного корпуса.</param>
/// <param name="Fuel">Топливо в баке (GDD §6). Меняется только прыжком и заправкой — тогда hangar приходит снова.</param>
/// <param name="MaxFuel">Бак активного корпуса.</param>
/// <param name="Home">Система последней стыковки: здесь корабль появится после гибели и после входа.</param>
public sealed record HangarMsg(
    string Hull,
    string Weapon,
    IReadOnlyList<string> Hulls,
    IReadOnlyList<string> Weapons,
    bool Docked,
    int Hp,
    int MaxHp,
    int Fuel = 0,
    int MaxFuel = 0,
    string? Home = null) : ServerMessage;

/// <summary>
/// Трюм игрока (GDD §21) — только своему соединению: снапшот один на всех, личному месту в нём нет.
/// Шлётся по событию (подбор, вход, смена корпуса, правка баланса, сдача груза), а не каждый тик.
/// </summary>
/// <param name="Used">Занято объёма.</param>
/// <param name="Max">Ёмкость трюма текущего корпуса.</param>
/// <param name="Items">Что лежит: идентификатор предмета — количество.</param>
/// <param name="Credits">Кредиты пилота.</param>
public sealed record CargoMsg(
    double Used,
    double Max,
    IReadOnlyDictionary<string, int> Items,
    int Credits = 0) : ServerMessage;

/// <summary>Короткое уведомление игроку по коду; текст подставляет клиент (см. ui/feed.ts).</summary>
public sealed record NoticeMsg(string Code) : ServerMessage;

/// <param name="Shots">Выстрелы этого тика; нет — поле не пишется.</param>
/// <param name="Kills">Уничтоженные в этом тике.</param>
/// <param name="Loot">Предметы, лежащие в космосе.</param>
/// <param name="Picks">Подобранное в этом тике — видно всем: чужой луч объясняет, куда делся предмет.</param>
/// <param name="Meteors">Метеориты в системе. Отдельно от кораблей: у них нет корпуса, пушки и места в ростере.</param>
public sealed record SnapshotMsg(
    long Tick,
    IReadOnlyList<ShipDto> Ships,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ShotDto>? Shots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<KillDto>? Kills = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LootDto>? Loot = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PickDto>? Picks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MeteorDto>? Meteors = null) : ServerMessage;

/// <param name="Th">Тяга последнего входа — для пламени двигателя у чужих кораблей.</param>
/// <param name="Ack">Последний применённый seq владельца: состояние — ровно после этого входа.</param>
/// <param name="Hp">Корпус, округлён вверх.</param>
/// <param name="Sh">Щит, округлён вверх.</param>
/// <param name="W">Пушка.</param>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long J = 0);

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

public static class Protocol
{
    /// <summary>
    /// Меняется, когда клиент и сервер разных версий уже не поймут друг друга
    /// (3 — бой, M3; 4 — пираты, M4; 5 — лут и трюм, M5a; 6 — ручной подбор и продажа груза; 7 — метеориты, M5b;
    /// 8 — аккаунты, док и магазин станции, M6; 9 — системы, врата, топливо, радар и бинарные дельта-снапшоты, M7).
    /// Зеркало PROTOCOL_VERSION в client/src/net/protocol.ts.
    /// </summary>
    public const int Version = 10;

    public const string DroneKind = "drone";
    public const string PirateKind = "pirate";

    /// <summary>Что покупают в доке (<see cref="BuyMsg.Kind"/>).</summary>
    public const string HullItem = "hull";
    public const string WeaponItem = "weapon";

    /// <summary>Коды уведомлений (<see cref="NoticeMsg"/>); текст подставляет клиент.</summary>
    public const string CargoFullNotice = "cargoFull";
    public const string UnloadedNotice = "unloaded";
    public const string TooFarNotice = "tooFar";
    public const string NoCreditsNotice = "noCredits";
    public const string NoFuelNotice = "noFuel";
    public const string GateFarNotice = "gateFar";
    public const string JumpCancelledNotice = "jumpCancelled";

    /// <summary>Причины отказа во входе (<see cref="DeniedMsg"/>).</summary>
    public const string BadNameDenied = "badName";
    public const string BadPasswordDenied = "badPassword";
    public const string WrongPasswordDenied = "wrongPassword";
    public const string BadKeyDenied = "badKey";

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
