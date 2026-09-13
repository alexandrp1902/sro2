using System.Globalization;
using System.Text;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Логика одной звёздной системы: игроки, сессии, шаг симуляции, снапшоты. Вызывается только из потока тика
/// (<see cref="SystemRoom"/>), своих потоков и таймеров не имеет — поэтому тестируется напрямую.
/// </summary>
public sealed class Room(IReadOnlyDictionary<string, HullParams> hulls, ILogger log)
{
    public const int MaxNameLength = 16;
    public const string DefaultName = "Рейнджер";

    /// <summary>Столько корабль без связи ждёт возвращения игрока, потом удаляется.</summary>
    public const int ReconnectGraceTicks = 60 * SimConfig.TickRate;

    /// <summary>Код закрытия WebSocket: к кораблю подключилось новое соединение с той же сессией.</summary>
    public const int ReplacedCloseCode = 4001;

    private const int MinTokenLength = 16;
    private const int MaxTokenLength = 64;

    private readonly Dictionary<int, Player> _players = [];
    private readonly Dictionary<int, Player> _byConnection = [];
    private readonly Dictionary<string, Player> _byToken = new(StringComparer.Ordinal);
    private readonly List<ShipDto> _ships = [];
    private readonly List<Player> _expired = [];
    private readonly MoveInput[] _steps = new MoveInput[InputBuffer.MaxBudget];
    private int _nextId;

    public long Tick { get; private set; }
    public IReadOnlyDictionary<string, HullParams> Hulls { get; private set; } = hulls;
    public int Count => _players.Count;

    public void Join(IClientConnection connection, string? token, string? name, string? hull)
    {
        if (_byConnection.ContainsKey(connection.Id)) return;
        if (!IsValidToken(token)) token = null;
        var hullId = hull is not null && Hulls.ContainsKey(hull) ? hull : null;

        if (token is not null && _byToken.TryGetValue(token, out var player))
        {
            // Старое соединение могло ещё не заметить обрыв (iOS усыпил вкладку) или это дубль вкладки.
            if (player.Connection is { } old)
            {
                _byConnection.Remove(old.Id);
                old.Close(ReplacedCloseCode, "replaced");
            }
            player.Attach(connection);
            player.Name = UniqueName(SanitizeName(name), player);
            if (hullId is not null) player.HullId = hullId;
            _byConnection[connection.Id] = player;
            connection.Send(new WelcomeMsg(player.Id, SimConfig.TickRate, Hulls, Resumed: true));
            BroadcastPlayers();
            log.LogInformation("Player {Id} '{Name}' resumed, online {Count}", player.Id, player.Name, OnlineCount());
            return;
        }

        player = new Player(++_nextId, token, UniqueName(SanitizeName(name), null), hullId ?? SimConfig.DefaultHull);
        player.Attach(connection);
        _players[player.Id] = player;
        _byConnection[connection.Id] = player;
        if (token is not null) _byToken[token] = player;
        connection.Send(new WelcomeMsg(player.Id, SimConfig.TickRate, Hulls, Resumed: false));
        BroadcastPlayers();
        log.LogInformation("Player {Id} '{Name}' joined as {Hull}, online {Count}", player.Id, player.Name, player.HullId, OnlineCount());
    }

    /// <summary>Соединение закрылось. Корабль остаётся ждать игрока, если у того есть сессия.</summary>
    public void Disconnect(IClientConnection connection)
    {
        // Соединение, которое уже вытеснили новым, здесь не найдётся и корабль не отцепит.
        if (!_byConnection.Remove(connection.Id, out var player)) return;
        if (player.Token is null)
        {
            Remove(player);
            log.LogInformation("Player {Id} left, online {Count}", player.Id, OnlineCount());
        }
        else
        {
            player.Detach(Tick);
            log.LogInformation("Player {Id} lost connection, online {Count}", player.Id, OnlineCount());
        }
        BroadcastPlayers();
    }

    public void Input(IClientConnection connection, int seq, MoveInput input)
    {
        if (_byConnection.TryGetValue(connection.Id, out var player)) player.Inputs.Enqueue(seq, input);
    }

    public void SetHull(IClientConnection connection, string? hullId)
    {
        if (hullId is null || !Hulls.ContainsKey(hullId) || !_byConnection.TryGetValue(connection.Id, out var player)) return;
        player.HullId = hullId;
        log.LogInformation("Player {Id} switched to {Hull}", player.Id, hullId);
    }

    public void Rename(IClientConnection connection, string? name)
    {
        if (!_byConnection.TryGetValue(connection.Id, out var player)) return;
        var unique = UniqueName(SanitizeName(name), player);
        if (unique == player.Name) return;
        log.LogInformation("Player {Id} renamed '{Old}' → '{New}'", player.Id, player.Name, unique);
        player.Name = unique;
        BroadcastPlayers();
    }

    public void ApplyHulls(IReadOnlyDictionary<string, HullParams> hulls)
    {
        Hulls = hulls;
        var message = new ConfigMsg(hulls);
        foreach (var player in _players.Values) player.Connection?.Send(message);
    }

    /// <summary>Один тик: шаги всех кораблей и снапшот подключённым игрокам.</summary>
    public void Step()
    {
        _ships.Clear();
        foreach (var player in _players.Values)
        {
            var hull = HullOf(player);
            if (player.Connection is null)
            {
                if (Tick - player.LostAtTick >= ReconnectGraceTicks)
                {
                    _expired.Add(player);
                    continue;
                }
                Movement.Step(ref player.Ship, player.StopInput, hull, SimConfig.Dt);
            }
            else
            {
                var count = player.Inputs.Tick(_steps);
                for (var i = 0; i < count; i++) Movement.Step(ref player.Ship, _steps[i], hull, SimConfig.Dt);
            }

            var s = player.Ship;
            var throttle = player.Connection is null ? 0 : player.Inputs.Last.Throttle;
            _ships.Add(new ShipDto(player.Id, s.X, s.Y, s.Rot, s.Vx, s.Vy, player.HullId, throttle, player.Inputs.AckSeq));
        }
        Tick++;

        if (_expired.Count > 0)
        {
            foreach (var player in _expired)
            {
                Remove(player);
                log.LogInformation("Player {Id} did not come back, removed", player.Id);
            }
            _expired.Clear();
            BroadcastPlayers();
        }

        if (_ships.Count == 0) return;
        // Все получают одни и те же байты: ack каждого игрока лежит в записи его корабля.
        var snapshot = Protocol.Encode(new SnapshotMsg(Tick, _ships));
        foreach (var player in _players.Values) player.Connection?.SendRaw(snapshot);
    }

    /// <summary>Без управляющих и невидимых символов, пробелы схлопнуты, не длиннее MaxNameLength.</summary>
    public static string SanitizeName(string? name)
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsControl(c) || category == UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        var trimmed = sb.ToString().Trim();
        return trimmed.Length == 0 ? DefaultName : Truncate(trimmed, MaxNameLength);
    }

    public static bool IsValidToken(string? token) =>
        token is { Length: >= MinTokenLength and <= MaxTokenLength } &&
        token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Занятое другим игроком имя получает номер: «Имя 2», «Имя 3»…</summary>
    private string UniqueName(string name, Player? self)
    {
        var candidate = name;
        for (var n = 2; IsTaken(candidate, self); n++)
        {
            var suffix = $" {n}";
            candidate = Truncate(name, MaxNameLength - suffix.Length).TrimEnd() + suffix;
        }
        return candidate;
    }

    private bool IsTaken(string name, Player? self)
    {
        foreach (var player in _players.Values)
        {
            if (player != self && string.Equals(player.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Не разрезает суррогатную пару (эмодзи) пополам.</summary>
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var length = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..length];
    }

    private void Remove(Player player)
    {
        _players.Remove(player.Id);
        if (player.Token is not null) _byToken.Remove(player.Token);
    }

    private HullParams HullOf(Player player)
    {
        if (Hulls.TryGetValue(player.HullId, out var hull)) return hull;
        player.HullId = SimConfig.DefaultHull; // корпус убрали из hulls.json на лету
        return Hulls[SimConfig.DefaultHull];
    }

    private int OnlineCount() => _byConnection.Count;

    private void BroadcastPlayers()
    {
        var list = _players.Values
            .OrderBy(p => p.Id)
            .Select(p => new PlayerDto(p.Id, p.Name, p.Connection is not null))
            .ToList();
        var message = Protocol.Encode(new PlayersMsg(list));
        foreach (var player in _players.Values) player.Connection?.SendRaw(message);
    }
}
