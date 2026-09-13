using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sro.Server.Net;

// Протокол — JSON-объекты с полем "t" (тип сообщения). Зеркало на клиенте: client/src/net/protocol.ts.

// Клиент → сервер
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMsg), "hello")]
[JsonDerivedType(typeof(PingMsg), "ping")]
public abstract record ClientMessage;

public sealed record HelloMsg(string? Name) : ClientMessage;

/// <param name="C">Время клиента, возвращается в pong как есть для замера RTT.</param>
public sealed record PingMsg(double C) : ClientMessage;

// Сервер → клиент
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(WelcomeMsg), "welcome")]
[JsonDerivedType(typeof(PongMsg), "pong")]
[JsonDerivedType(typeof(OnlineMsg), "online")]
public abstract record ServerMessage;

public sealed record WelcomeMsg(int Id, int TickRate) : ServerMessage;

public sealed record PongMsg(double C, long Tick) : ServerMessage;

public sealed record OnlineMsg(int Count) : ServerMessage;

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
