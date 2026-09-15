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
public abstract record ClientMessage;

/// <param name="Hull">Класс корпуса, сохранённый на устройстве.</param>
/// <param name="Token">Сессия вкладки: с ней после обрыва связи игрок возвращается к своему кораблю.</param>
/// <param name="Weapon">Пушка, сохранённая на устройстве.</param>
public sealed record HelloMsg(string? Name, string? Hull, string? Token, string? Weapon = null) : ClientMessage;

/// <param name="C">Время клиента, возвращается в pong как есть для замера RTT.</param>
public sealed record PingMsg(double C) : ClientMessage;

/// <summary>Только управление (§49): направление на экране и тяга. Координаты клиент не присылает.</summary>
public sealed record InputMsg(int Seq, double Dx, double Dy, double Th) : ClientMessage;

/// <summary>Смена класса корпуса из dev-панели.</summary>
public sealed record HullMsg(string? Id) : ClientMessage;

/// <summary>Смена пушки из dev-панели.</summary>
public sealed record WeaponMsg(string? Id) : ClientMessage;

/// <summary>Смена ника на лету.</summary>
public sealed record NameMsg(string? Name) : ClientMessage;

/// <summary>Выбранная цель (GDD §9); 0 — цели нет.</summary>
public sealed record TargetMsg(int Id) : ClientMessage;

/// <summary>Атака нажата или отпущена: пока нажата, пушка стреляет сама по готовности (GDD §47).</summary>
public sealed record FireMsg(bool On) : ClientMessage;

// Сервер → клиент
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(WelcomeMsg), "welcome")]
[JsonDerivedType(typeof(PongMsg), "pong")]
[JsonDerivedType(typeof(PlayersMsg), "players")]
[JsonDerivedType(typeof(ConfigMsg), "config")]
[JsonDerivedType(typeof(SnapshotMsg), "snapshot")]
public abstract record ServerMessage;

/// <param name="Id">Id своего корабля в снапшотах.</param>
/// <param name="Version">Версия протокола (<see cref="Protocol.Version"/>): клиент другой версии играть не будет.</param>
/// <param name="Hulls">Параметры корпусов: клиент предсказывает движение с теми же числами, что и сервер.</param>
/// <param name="Weapons">Параметры пушек — для карточки цели и трассеров.</param>
/// <param name="Combat">Правила боя: время респауна, защита.</param>
/// <param name="Resumed">Игрок вернулся к кораблю, который ждал его после обрыва связи.</param>
/// <param name="Npcs">Пираты: логова и укрытие у станции — клиент рисует их на карте.</param>
public sealed record WelcomeMsg(
    int Id,
    int TickRate,
    int Version,
    IReadOnlyDictionary<string, HullParams> Hulls,
    IReadOnlyDictionary<string, WeaponParams> Weapons,
    CombatRules Combat,
    bool Resumed,
    NpcRules? Npcs = null) : ServerMessage;

public sealed record PongMsg(double C, long Tick) : ServerMessage;

/// <summary>Весь список кораблей с именами — игроки и NPC; присылается при любом изменении (вход, выход, обрыв, смена ника).</summary>
public sealed record PlayersMsg(IReadOnlyList<PlayerDto> Players) : ServerMessage;

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
    NpcRules? Npcs = null) : ServerMessage;

/// <param name="Shots">Выстрелы этого тика; нет — поле не пишется.</param>
/// <param name="Kills">Уничтоженные в этом тике.</param>
public sealed record SnapshotMsg(
    long Tick,
    IReadOnlyList<ShipDto> Ships,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ShotDto>? Shots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<KillDto>? Kills = null) : ServerMessage;

/// <param name="Th">Тяга последнего входа — для пламени двигателя у чужих кораблей.</param>
/// <param name="Ack">Последний применённый seq владельца: состояние — ровно после этого входа.</param>
/// <param name="Hp">Корпус, округлён вверх.</param>
/// <param name="Sh">Щит, округлён вверх.</param>
/// <param name="W">Пушка.</param>
/// <param name="Rt">Корабль уничтожен и появится в этот тик; 0 — цел.</param>
/// <param name="Pu">Под защитой до этого тика; 0 — без защиты.</param>
/// <param name="Tg">Цель пирата в бою; 0 — нет (и у игроков).</param>
/// <param name="Ai">Состояние ИИ пирата: patrol, attack, return; у игроков нет.</param>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ai = null);

/// <param name="Dmg">Урон всего (0 при промахе).</param>
/// <param name="Sh">Из него пришлось на щит.</param>
/// <param name="Ch">Шанс попадания, %, по которому бросал сервер.</param>
public sealed record ShotDto(int From, int To, string W, bool Hit, int Dmg, int Sh, double Ch);

/// <param name="By">Кто нанёс смертельный удар.</param>
public sealed record KillDto(int Id, int By);

public static class Protocol
{
    /// <summary>
    /// Меняется, когда клиент и сервер разных версий уже не поймут друг друга (3 — бой, M3; 4 — пираты, M4).
    /// Зеркало PROTOCOL_VERSION в client/src/net/protocol.ts.
    /// </summary>
    public const int Version = 4;

    public const string DroneKind = "drone";
    public const string PirateKind = "pirate";

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
