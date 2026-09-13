using System.Net.WebSockets;
using System.Threading.Channels;
using Sro.Server.Game;

namespace Sro.Server.Net;

/// <summary>Соединение с клиентом так, как его видит игровая комната. Транспорт можно заменить.</summary>
public interface IClientConnection
{
    int Id { get; }

    /// <summary>Неблокирующая отправка: сообщение ставится в очередь соединения.</summary>
    void Send(ServerMessage message);

    /// <summary>Отправка уже закодированного сообщения — снапшот кодируется один раз на всех.</summary>
    void SendRaw(byte[] utf8Json);
}

public sealed class WebSocketConnection(WebSocket socket, ILogger log) : IClientConnection
{
    private const int MaxMessageBytes = 16 * 1024;
    private static int _nextId;

    // WebSocket не допускает параллельных SendAsync, поэтому исходящие идут через очередь с одним читателем.
    private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    public int Id { get; } = Interlocked.Increment(ref _nextId);

    public void Send(ServerMessage message) => SendRaw(Protocol.Encode(message));

    public void SendRaw(byte[] utf8Json) => _outbox.Writer.TryWrite(utf8Json);

    public async Task RunAsync(SystemRoom room, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sending = SendLoopAsync(cts.Token);
        try
        {
            await ReceiveLoopAsync(room, cts.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // Обрыв связи (например, iOS усыпил вкладку) — штатная ситуация.
        }
        finally
        {
            room.Leave(this);
            _outbox.Writer.TryComplete();
            cts.Cancel();
            try { await sending; } catch { /* сокет уже закрыт */ }
        }
    }

    private async Task ReceiveLoopAsync(SystemRoom room, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes];
        var joined = false;

        while (socket.State == WebSocketState.Open)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == buffer.Length)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, ct);
                    return;
                }
                result = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
                length += result.Count;
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return;
            }

            switch (Protocol.TryDecode(buffer.AsSpan(0, length)))
            {
                case HelloMsg hello when !joined:
                    joined = true;
                    room.Join(this, hello.Name, hello.Hull);
                    break;
                case InputMsg input when joined:
                    room.Input(this, input);
                    break;
                case HullMsg hull when joined:
                    room.SetHull(this, hull.Id);
                    break;
                case PingMsg ping:
                    // Отвечаем сразу из сетевого потока, чтобы пинг мерил сеть, а не ожидание тика.
                    Send(new PongMsg(ping.C, room.Tick));
                    break;
                case null:
                    log.LogDebug("Connection #{Id}: undecodable message", Id);
                    break;
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var bytes in _outbox.Reader.ReadAllAsync(ct))
            await socket.SendAsync(new ReadOnlyMemory<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
