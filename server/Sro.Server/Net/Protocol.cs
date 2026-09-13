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
[JsonDerivedType(typeof(NameMsg), "name")]
public abstract record ClientMessage;

/// <param name="Hull">Класс корпуса, сохранённый на устройстве.</param>
/// <param name="Token">Сессия вкладки: с ней после обрыва связи игрок возвращается к своему кораблю.</param>
public sealed record HelloMsg(string? Name, string? Hull, string? Token) : ClientMessage;

/// <param name="C">Время клиента, возвращается в pong как есть для замера RTT.</param>
public sealed record PingMsg(double C) : ClientMessage;

/// <summary>Только управление (§49): направление на экране и тяга. Координаты клиент не присылает.</summary>
public sealed record InputMsg(int Seq, double Dx, double Dy, double Th) : ClientMessage;

/// <summary>Смена класса корпуса из dev-панели.</summary>
public sealed record HullMsg(string? Id) : ClientMessage;

/// <summary>Смена ника на лету.</summary>
public sealed record NameMsg(string? Name) : ClientMessage;

// Сервер → клиент
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(WelcomeMsg), "welcome")]
[JsonDerivedType(typeof(PongMsg), "pong")]
[JsonDerivedType(typeof(PlayersMsg), "players")]
[JsonDerivedType(typeof(ConfigMsg), "config")]
[JsonDerivedType(typeof(SnapshotMsg), "snapshot")]
public abstract record ServerMessage;

/// <param name="Id">Id своего корабля в снапшотах.</param>
/// <param name="Hulls">Параметры корпусов: клиент предсказывает движение с теми же числами, что и сервер.</param>
/// <param name="Resumed">Игрок вернулся к кораблю, который ждал его после обрыва связи.</param>
public sealed record WelcomeMsg(int Id, int TickRate, IReadOnlyDictionary<string, HullParams> Hulls, bool Resumed) : ServerMessage;

public sealed record PongMsg(double C, long Tick) : ServerMessage;

/// <summary>Весь список игроков системы; присылается при любом изменении (вход, выход, обрыв, смена ника).</summary>
public sealed record PlayersMsg(IReadOnlyList<PlayerDto> Players) : ServerMessage;

/// <param name="Online">false — связи нет, корабль висит в космосе и ждёт игрока.</param>
public sealed record PlayerDto(int Id, string Name, bool Online);

/// <summary>hulls.json изменился на диске.</summary>
public sealed record ConfigMsg(IReadOnlyDictionary<string, HullParams> Hulls) : ServerMessage;

public sealed record SnapshotMsg(long Tick, IReadOnlyList<ShipDto> Ships) : ServerMessage;

/// <param name="Th">Тяга последнего входа — для пламени двигателя у чужих кораблей.</param>
/// <param name="Ack">Последний применённый seq владельца: состояние — ровно после этого входа.</param>
public sealed record ShipDto(int Id, double X, double Y, double R, double Vx, double Vy, string Hull, double Th, int Ack);

public static class Protocol
{
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
