using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// «Вторжение пиратов» (GDD §38): раз в intervalMinutes галактика выбирает систему со станцией, объявляет вторжение
/// всем пилотам, а через announceSeconds у станции начинаются волны. Все сроки считаются от текущего invasion.json —
/// правка файла во время плейтеста действует сразу. Вклад — урон по пиратам вторжения; фонд делится по нему.
/// Живёт в потоке тика галактики, как и <see cref="Galaxy"/>.
/// </summary>
public sealed class InvasionDirector
{
    public enum Phase
    {
        /// <summary>Ждёт срока следующего вторжения.</summary>
        Idle,
        /// <summary>Объявлено: идёт отсчёт до начала.</summary>
        Announced,
        /// <summary>Волны у станции.</summary>
        Running,
    }

    /// <summary>В итогах — не больше стольких строк.</summary>
    public const int MaxResults = 10;

    private readonly ILogger _log;
    private readonly Random _rng;
    private readonly Dictionary<int, Score> _scores = [];
    /// <summary>С этого тика идёт ожидание; первое вторжение ждёт firstMinutes, остальные — intervalMinutes.</summary>
    private long _idleSince;
    private bool _first = true;
    private long _announcedAt;
    private long _startedAt;
    private long _nextWaveTick;
    private (double X, double Y) _point;
    private string? _lastSystem;

    private sealed class Score
    {
        public required string Name;
        public double Damage;
    }

    public InvasionDirector(ILogger log, Random? rng = null)
    {
        _log = log;
        _rng = rng ?? Random.Shared;
    }

    public Phase State { get; private set; }

    /// <summary>Номер текущего (или последнего) вторжения.</summary>
    public int Id { get; private set; }

    /// <summary>Система вторжения; null — нет.</summary>
    public Room? Room { get; private set; }

    /// <summary>Номер идущей волны, с нуля.</summary>
    public int Wave { get; private set; }

    /// <summary>Урон пилота по пиратам вторжения id.</summary>
    public void Contributed(Player player, int id, double damage)
    {
        if (id != Id || State != Phase.Running) return;
        if (!_scores.TryGetValue(player.Id, out var score)) _scores[player.Id] = score = new Score { Name = player.Name };
        score.Name = player.Name;
        score.Damage += damage;
    }

    /// <summary>Урон пилота во вторжении — для тестов и лога.</summary>
    public double DamageOf(int playerId) => _scores.GetValueOrDefault(playerId)?.Damage ?? 0;

    /// <summary>Шаг после шага всех комнат.</summary>
    public void Step(Galaxy galaxy, long tick)
    {
        var rules = galaxy.Balance.Invasion;
        switch (State)
        {
            case Phase.Idle:
                if (!rules.Enabled)
                {
                    _idleSince = tick; // включат — отсчёт пойдёт с этого момента
                    return;
                }
                if (tick - _idleSince >= (_first ? rules.FirstTicks : rules.IntervalTicks)) Announce(galaxy, tick);
                return;
            case Phase.Announced:
                if (tick - _announcedAt >= rules.AnnounceTicks) Start(galaxy, tick, rules);
                else if ((tick - _announcedAt) % SimConfig.TickRate == 0) Broadcast(galaxy, Status(tick, rules));
                return;
            case Phase.Running:
                Run(galaxy, tick, rules);
                return;
        }
    }

    /// <summary>Вошедшему в игру — что сейчас с вторжением, чтобы не ждать секунды до следующей рассылки.</summary>
    public void SendTo(Player player, Galaxy galaxy)
    {
        if (State == Phase.Idle || player.Connection is not { } connection) return;
        connection.Send(Status(galaxy.Tick, galaxy.Balance.Invasion));
    }

    private void Announce(Galaxy galaxy, long tick)
    {
        var candidates = galaxy.Rooms.Where(r => r.Balance.HasStation).ToList();
        if (candidates.Count == 0)
        {
            _idleSince = tick;
            return;
        }
        // Не два раза подряд в одной системе, если есть из чего выбрать.
        if (candidates.Count > 1) candidates.RemoveAll(r => r.SystemId == _lastSystem);
        Room = candidates[_rng.Next(candidates.Count)];
        Id++;
        Wave = 0;
        _scores.Clear();
        _announcedAt = tick;
        _nextWaveTick = 0;
        _point = default;
        State = Phase.Announced;
        _log.LogInformation("Invasion {Id} announced in {System}", Id, Room.SystemId);
        Broadcast(galaxy, Status(tick, galaxy.Balance.Invasion));
    }

    private void Start(Galaxy galaxy, long tick, InvasionRules rules)
    {
        if (!rules.Enabled || Room is null)
        {
            Finish(galaxy, tick, rules, won: false);
            return;
        }
        State = Phase.Running;
        _startedAt = tick;
        _point = Room.InvasionPoint();
        Wave = 0;
        Room.SpawnInvasion(Id, rules.WaveList[0], _point);
        _log.LogInformation("Invasion {Id} started in {System}: wave 1/{Waves}", Id, Room.SystemId, rules.WaveList.Count);
        Broadcast(galaxy, Status(tick, rules));
    }

    private void Run(Galaxy galaxy, long tick, InvasionRules rules)
    {
        var waves = rules.WaveList;
        if (Room is null || waves.Count == 0)
        {
            Finish(galaxy, tick, rules, won: false);
            return;
        }
        if (_nextWaveTick > 0)
        {
            if (tick >= _nextWaveTick)
            {
                _nextWaveTick = 0;
                Wave = Math.Min(Wave + 1, waves.Count - 1);
                Room.SpawnInvasion(Id, waves[Wave], _point);
                _log.LogInformation("Invasion {Id}: wave {Wave}/{Waves}", Id, Wave + 1, waves.Count);
                Broadcast(galaxy, Status(tick, rules));
            }
        }
        else if (Room.InvadersLeft(Id) == 0)
        {
            if (Wave + 1 >= waves.Count)
            {
                Finish(galaxy, tick, rules, won: true);
                return;
            }
            _nextWaveTick = tick + rules.WaveGapTicks;
            Broadcast(galaxy, Status(tick, rules));
        }
        if (tick - _startedAt >= rules.DurationTicks)
        {
            Finish(galaxy, tick, rules, won: false);
            return;
        }
        if ((tick - _startedAt) % SimConfig.TickRate == 0) Broadcast(galaxy, Status(tick, rules));
    }

    /// <summary>Итог: уцелевшие пираты уходят, фонд — по урону, каждому пилоту — итоги и своя доля.</summary>
    private void Finish(Galaxy galaxy, long tick, InvasionRules rules, bool won)
    {
        var room = Room;
        room?.EndInvasion(Id);
        var ranked = _scores.OrderByDescending(kv => kv.Value.Damage).ThenBy(kv => kv.Key).ToList();
        var shares = rules.Shares([.. ranked.Select(kv => kv.Value.Damage)], won);
        var rewards = new Dictionary<int, int>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var (id, _) = ranked[i];
            rewards[id] = shares[i];
            // Кто ушёл из игры до конца — доли не получает: платить некуда.
            if (galaxy.FindPilot(id) is { } found) found.Room.Pay(found.Player, shares[i]);
        }
        var results = ranked.Take(MaxResults)
            .Select((kv, i) => new InvasionScoreDto(kv.Value.Name, (int)Math.Round(kv.Value.Damage), shares[i]))
            .ToList();
        var state = won ? Protocol.InvasionWon : Protocol.InvasionLost;
        foreach (var r in galaxy.Rooms)
        {
            foreach (var player in r.Pilots)
            {
                player.Connection?.Send(new InvasionMsg(
                    state, room?.SystemId ?? "", SystemName(room), 0, Wave + 1, rules.WaveList.Count, 0, 0, _point.X, _point.Y,
                    results, rewards.GetValueOrDefault(player.Id), (int)Math.Round(DamageOf(player.Id))));
            }
        }
        _log.LogInformation(
            "Invasion {Id} in {System} {Outcome} at wave {Wave}: {Players} pilots shared {Paid} credits",
            Id, room?.SystemId, won ? "repelled" : "failed", Wave + 1, ranked.Count, shares.Sum());
        _lastSystem = room?.SystemId;
        Room = null;
        State = Phase.Idle;
        _idleSince = tick;
        _first = false;
    }

    private InvasionMsg Status(long tick, InvasionRules rules)
    {
        var system = Room?.SystemId ?? "";
        var waves = rules.WaveList.Count;
        if (State == Phase.Announced)
            return new InvasionMsg(Protocol.InvasionAnnounce, system, SystemName(Room), Seconds(_announcedAt + rules.AnnounceTicks - tick), 0, waves);
        var nextIn = _nextWaveTick > 0 ? Seconds(_nextWaveTick - tick) : 0;
        return new InvasionMsg(
            Protocol.InvasionWave, system, SystemName(Room), Seconds(_startedAt + rules.DurationTicks - tick),
            Wave + 1, waves, Room?.InvadersLeft(Id) ?? 0, nextIn, _point.X, _point.Y);
    }

    private static string SystemName(Room? room) => room?.Balance.SystemDef.Name ?? "";

    private static int Seconds(long ticks) => (int)Math.Ceiling(Math.Max(0, ticks) / (double)SimConfig.TickRate);

    /// <summary>Одно и то же всем пилотам галактики: кодируется один раз.</summary>
    private static void Broadcast(Galaxy galaxy, InvasionMsg message)
    {
        var bytes = Protocol.Encode(message);
        foreach (var room in galaxy.Rooms)
        {
            foreach (var player in room.Pilots) player.Connection?.SendRaw(bytes);
        }
    }
}
