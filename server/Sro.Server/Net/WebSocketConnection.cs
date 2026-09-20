using System.Net.WebSockets;
using System.Threading.Channels;
using Sro.Server.Accounts;
using Sro.Server.Game;

namespace Sro.Server.Net;

/// <summary>Соединение с клиентом так, как его видит игровая комната. Транспорт можно заменить.</summary>
public interface IClientConnection
{
    int Id { get; }

    /// <summary>Неблокирующая отправка: сообщение ставится в очередь соединения.</summary>
    void Send(ServerMessage message);

    /// <summary>Отправка уже закодированного сообщения — ростер и баланс кодируются один раз на всех.</summary>
    void SendRaw(byte[] utf8Json);

    /// <summary>Бинарный кадр снапшота (<see cref="SnapshotCodec"/>).</summary>
    /// <returns>false — очередь соединения полна и кадр выброшен: следующий должен быть ключевым.</returns>
    bool SendFrame(byte[] frame);

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

    private readonly record struct Outgoing(byte[] Bytes, bool Binary);

    // WebSocket не допускает параллельных SendAsync, поэтому исходящие идут через очередь с одним читателем.
    // Полная очередь (клиент не успевает принимать) выбрасывает новое, а не старое: дельта-снапшот, выброшенный
    // из середины очереди, сломал бы клиенту всё после него, а о новом отправитель узнаёт и шлёт ключевой кадр.
    private readonly Channel<Outgoing> _outbox = Channel.CreateBounded<Outgoing>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public int Id { get; } = Interlocked.Increment(ref _nextId);

    public void Send(ServerMessage message) => SendRaw(Protocol.Encode(message));

    public void SendRaw(byte[] utf8Json) => _outbox.Writer.TryWrite(new Outgoing(utf8Json, Binary: false));

    public bool SendFrame(byte[] frame)
    {
        if (!_outbox.Writer.TryWrite(new Outgoing(frame, Binary: true))) return false;
        Interlocked.Add(ref _framesBytes, frame.Length);
        return true;
    }

    private static long _framesBytes;

    /// <summary>Байт снапшотов, поставленных в очередь всеми соединениями с запуска, — для лога трафика.</summary>
    public static long FrameBytes => Interlocked.Read(ref _framesBytes);

    public void Close(int code, string reason)
    {
        _closeCode = code;
        _closeReason = reason;
        _outbox.Writer.TryComplete(); // send-loop отправит остаток очереди и close-фрейм
    }

    public async Task RunAsync(GalaxyHost room, AccountStore accounts, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sending = SendLoopAsync(cts);
        try
        {
            await ReceiveLoopAsync(room, accounts, cts.Token);
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

    private async Task ReceiveLoopAsync(GalaxyHost room, AccountStore accounts, CancellationToken ct)
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
                case HelloMsg { Password: null, Key: null } hello when !joined:
                    joined = true;
                    room.Join(this, hello);
                    break;
                case HelloMsg hello when !joined:
                    // Хеш пароля — десятки миллисекунд: считаем здесь, в сетевом потоке, а не в тике комнаты.
                    var login = hello.Password is null ? accounts.Resume(hello.Key) : accounts.Login(hello.Name, hello.Password);
                    if (!login.Ok)
                    {
                        Send(new DeniedMsg(DeniedCode(login.Error)));
                        Close(Protocol.DeniedCloseCode, "denied");
                        break;
                    }
                    joined = true;
                    Send(new AccountMsg(login.Name, login.Key));
                    room.JoinAccount(this, login.Id, login.Name);
                    break;
                case DockMsg dock when joined:
                    room.Dock(this, dock.On, dock.Place);
                    break;
                case BuyMsg buy when joined:
                    room.Buy(this, buy.Kind, buy.Id, buy.Slot);
                    break;
                case RepairMsg when joined:
                    room.Repair(this);
                    break;
                case JumpMsg jump when joined:
                    room.Jump(this, jump.To);
                    break;
                case RefuelMsg when joined:
                    room.Refuel(this);
                    break;
                case MissionMsg mission when joined:
                    room.Mission(this, mission.Action, mission.Id);
                    break;
                case PartyMsg party when joined:
                    room.Party(this, party.Action, party.Id);
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
                case PvpMsg pvp when joined:
                    room.SetPvp(this, pvp.On);
                    break;
                case LootTargetMsg loot when joined:
                    room.SetLootTarget(this, loot.Id);
                    break;
                case GrabMsg when joined:
                    room.Grab(this);
                    break;
                case DropMsg drop when joined:
                    room.Jettison(this, drop.Item);
                    break;
                case SellMsg sell when joined:
                    room.Sell(this, sell.Item, sell.Count);
                    break;
                case BuyGoodsMsg goods when joined:
                    room.BuyGoods(this, goods.Item, goods.Count);
                    break;
                case HullMsg hull when joined:
                    room.SetHull(this, hull.Id);
                    break;
                case WeaponMsg weapon when joined:
                    room.SetWeapon(this, weapon.Id);
                    break;
                case FitMsg fit when joined:
                    room.Fit(this, fit.Slot, fit.Id);
                    break;
                case SellItemMsg sell when joined:
                    room.SellItem(this, sell.Id);
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

    private static string DeniedCode(LoginError error) => error switch
    {
        LoginError.BadName => Protocol.BadNameDenied,
        LoginError.BadPassword => Protocol.BadPasswordDenied,
        LoginError.WrongPassword => Protocol.WrongPasswordDenied,
        _ => Protocol.BadKeyDenied,
    };

    private async Task SendLoopAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(ct))
            {
                var type = message.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
                await socket.SendAsync(new ReadOnlyMemory<byte>(message.Bytes), type, endOfMessage: true, ct);
            }

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
