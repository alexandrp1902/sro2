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

    /// <summary>Закрыть соединение с кодом (4000–4999 — коды игры) после уже поставленных в очередь сообщений.</summary>
    void Close(int code, string reason);
}

public sealed class WebSocketConnection(WebSocket socket, ILogger log) : IClientConnection
{
    private const int MaxMessageBytes = 16 * 1024;

    /// <summary>Столько ждём ответного close от клиента; мёртвый сокет (усыплённая вкладка iOS) не ответит никогда.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private static int _nextId;
    private volatile string? _closeReason;
    private int _closeCode;

    // WebSocket не допускает параллельных SendAsync, поэтому исходящие идут через очередь с одним читателем.
    private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    public int Id { get; } = Interlocked.Increment(ref _nextId);

    public void Send(ServerMessage message) => SendRaw(Protocol.Encode(message));

    public void SendRaw(byte[] utf8Json) => _outbox.Writer.TryWrite(utf8Json);

    public void Close(int code, string reason)
    {
        _closeCode = code;
        _closeReason = reason;
        _outbox.Writer.TryComplete(); // send-loop отправит остаток очереди и close-фрейм
    }

    public async Task RunAsync(SystemRoom room, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sending = SendLoopAsync(cts);
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
                // Если close начали мы (Close), ответ клиента уже завершил рукопожатие.
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return;
            }

            switch (Protocol.TryDecode(buffer.AsSpan(0, length)))
            {
                case HelloMsg hello when !joined:
                    joined = true;
                    room.Join(this, hello);
                    break;
                case InputMsg input when joined:
                    room.Input(this, input);
                    break;
                case TargetMsg target when joined:
                    room.SetTarget(this, target.Id);
                    break;
                case FireMsg fire when joined:
                    room.SetFire(this, fire.On);
                    break;
                case LootTargetMsg loot when joined:
                    room.SetLootTarget(this, loot.Id);
                    break;
                case GrabMsg when joined:
                    room.Grab(this);
                    break;
                case SellMsg sell when joined:
                    room.Sell(this, sell.Item);
                    break;
                case HullMsg hull when joined:
                    room.SetHull(this, hull.Id);
                    break;
                case WeaponMsg weapon when joined:
                    room.SetWeapon(this, weapon.Id);
                    break;
                case NameMsg name when joined:
                    room.Rename(this, name.Name);
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

    private async Task SendLoopAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await foreach (var bytes in _outbox.Reader.ReadAllAsync(ct))
                await socket.SendAsync(new ReadOnlyMemory<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);

            if (_closeReason is { } reason && socket.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync((WebSocketCloseStatus)_closeCode, reason, ct);
                cts.CancelAfter(CloseTimeout);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            cts.Cancel(); // отправить не вышло — сокет мёртв, приём тоже прекращаем
        }
    }
}
