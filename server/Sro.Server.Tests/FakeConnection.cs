using System.Text.Json;
using Sro.Server.Net;

namespace Sro.Server.Tests;

/// <summary>Соединение без сети: всё, что комната отправила, раскодируется и складывается в список.</summary>
internal sealed class FakeConnection(int id) : IClientConnection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
    };

    public int Id { get; } = id;
    public List<ServerMessage> Messages { get; } = [];
    public int? ClosedWith { get; private set; }

    public void Send(ServerMessage message) => SendRaw(Protocol.Encode(message));

    // Через JSON, как по сети: заодно проверяется, что сообщения сериализуются.
    public void SendRaw(byte[] utf8Json) => Messages.Add(JsonSerializer.Deserialize<ServerMessage>(utf8Json, Json)!);

    /// <summary>Бинарные снапшоты собираются в полный <see cref="SnapshotMsg"/>, как на клиенте.</summary>
    public SnapshotCodec.Decoder Snapshots { get; } = new();

    /// <summary>Сколько байт снапшотов пришло — для проверок трафика.</summary>
    public long FrameBytes { get; private set; }

    /// <summary>Очередь «полна»: кадры выбрасываются, как у медленного клиента.</summary>
    public bool Congested { get; set; }

    public bool SendFrame(byte[] frame)
    {
        if (Congested) return false;
        FrameBytes += frame.Length;
        Messages.Add(Snapshots.Decode(frame));
        return true;
    }

    public void Close(int code, string reason) => ClosedWith = code;

    public T Last<T>() where T : ServerMessage => Messages.OfType<T>().Last();

    public int Count<T>() where T : ServerMessage => Messages.OfType<T>().Count();
}
