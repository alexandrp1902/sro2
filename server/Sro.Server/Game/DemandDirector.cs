using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// События спроса (M15.5): раз в intervalMinutes галактика выбирает место в красной зоне, объявляет
/// нужду всем пилотам, а через announceSeconds открывает приёмку. Цена там подскакивает в несколько раз
/// и оседает по мере наполнения квоты.
///
/// Смысл не в доставке. Гружёные корабли сходятся в одну точку, где PvP свободен и убийство не стоит
/// репутации, — и товар можно не везти, а отнять у того, кто уже довёз его до половины пути.
///
/// Устроено как <see cref="InvasionDirector"/> и живёт в том же потоке тика галактики. Состояние — только
/// в памяти: перезапуск сервера гасит событие, и цены возвращаются к норме.
/// </summary>
public sealed class DemandDirector(ILogger log, Random? rng = null)
{
    public enum Phase
    {
        /// <summary>Ждёт срока следующего события.</summary>
        Idle,
        /// <summary>Объявлено: приёмка вот-вот откроется, есть время долететь.</summary>
        Announced,
        /// <summary>Приёмка идёт.</summary>
        Open,
    }

    private readonly Random _rng = rng ?? Random.Shared;
    private long _idleSince;
    private bool _first = true;
    private long _announcedAt;
    private long _openedAt;
    private string? _lastPlace;

    public Phase State { get; private set; }

    /// <summary>Номер текущего (или последнего) события.</summary>
    public int Id { get; private set; }

    /// <summary>Комната события; null — его нет.</summary>
    public Room? Room { get; private set; }

    /// <summary>Место события; null — его нет.</summary>
    public PlaceDef? Place { get; private set; }

    /// <summary>Повод; null — события нет.</summary>
    public DemandCase? Cause { get; private set; }

    /// <summary>Шаг после шага всех комнат.</summary>
    public void Step(Galaxy galaxy, long tick)
    {
        var rules = galaxy.Balance.Demand;
        switch (State)
        {
            case Phase.Idle:
                if (!rules.Any)
                {
                    // Событий нет — ждать нечего; выключили и включили файл, значит отсчёт с нуля.
                    _idleSince = tick;
                    return;
                }
                if (tick - _idleSince >= (_first ? rules.FirstTicks : rules.IntervalTicks)) Announce(galaxy, tick, rules);
                return;
            case Phase.Announced:
                if (tick - _announcedAt >= rules.AnnounceTicks) Open(galaxy, tick, rules);
                else if ((tick - _announcedAt) % SimConfig.TickRate == 0) Broadcast(galaxy, Status(tick, rules));
                return;
            case Phase.Open:
                Run(galaxy, tick, rules);
                return;
        }
    }

    /// <summary>Вошедшему в игру — что сейчас со спросом, чтобы не ждать секунды до следующей рассылки.</summary>
    public void SendTo(Player player, Galaxy galaxy)
    {
        if (State == Phase.Idle || player.Connection is not { } connection) return;
        if (Status(galaxy.Tick, galaxy.Balance.Demand) is { } status) connection.Send(status);
    }

    /// <summary>Квоту выбрали — закрываем досрочно, не дожидаясь следующего тика фазы.</summary>
    public void Filled(Galaxy galaxy, Room room)
    {
        if (State != Phase.Open || Room != room) return;
        Finish(galaxy, galaxy.Tick, galaxy.Balance.Demand, Protocol.DemandFilled);
    }

    private void Announce(Galaxy galaxy, long tick, DemandRules rules)
    {
        // Красная зона обязательна: в Ядре это был бы безопасный извоз, а событие держится на том,
        // что гружёный корабль там могут отнять.
        var candidates = new List<(Room Room, PlaceDef Place)>();
        foreach (var room in galaxy.Rooms)
        {
            if (room.Balance.SystemDef.Pvp != GalaxyRules.PvpFree) continue;
            foreach (var trading in room.TradingPlaces) candidates.Add((room, trading));
        }
        if (candidates.Count == 0)
        {
            _idleSince = tick;
            return;
        }
        // Не два раза подряд в одно место, если есть из чего выбрать.
        if (candidates.Count > 1) candidates.RemoveAll(c => c.Place.Key == _lastPlace);

        var (chosen, place) = candidates[_rng.Next(candidates.Count)];
        Room = chosen;
        Place = place;
        Cause = rules.CaseList[_rng.Next(rules.CaseList.Count)];
        Id++;
        _announcedAt = tick;
        _lastPlace = place.Key;
        State = Phase.Announced;
        log.LogInformation("Demand {Id} announced at {Place}: {Case}", Id, place.Key, Cause.Id);
        Broadcast(galaxy, Status(tick, rules));
    }

    private void Open(Galaxy galaxy, long tick, DemandRules rules)
    {
        if (Room is null || Place is null || Cause is null)
        {
            Finish(galaxy, tick, rules, Protocol.DemandOver);
            return;
        }
        State = Phase.Open;
        _openedAt = tick;
        Room.StartDemand(Id, Place, Cause, rules);
        Broadcast(galaxy, Status(tick, rules));
    }

    private void Run(Galaxy galaxy, long tick, DemandRules rules)
    {
        if (Room is null || Room.DemandLeft(Id) <= 0)
        {
            Finish(galaxy, tick, rules, Protocol.DemandFilled);
            return;
        }
        // Срок вышел с неполной квотой — событие просто гаснет. Ни штрафов, ни виноватых:
        // не довезли — значит не довезли.
        if (tick - _openedAt >= rules.DurationTicks)
        {
            Finish(galaxy, tick, rules, Protocol.DemandOver);
            return;
        }
        if ((tick - _openedAt) % SimConfig.TickRate == 0) Broadcast(galaxy, Status(tick, rules));
    }

    private void Finish(Galaxy galaxy, long tick, DemandRules rules, string state)
    {
        var status = Status(tick, rules, state);
        Room?.EndDemand(Id);
        log.LogInformation("Demand {Id} finished as {State}", Id, state);
        State = Phase.Idle;
        _idleSince = tick;
        _first = false;
        Room = null;
        Place = null;
        Cause = null;
        if (status is not null) Broadcast(galaxy, status);
    }

    /// <summary>Что сейчас со спросом; null — события нет.</summary>
    private DemandMsg? Status(long tick, DemandRules rules, string? state = null)
    {
        if (Room is null || Place is null || Cause is null) return null;
        var now = state ?? (State == Phase.Announced ? Protocol.DemandAnnounce : Protocol.DemandOpen);
        var ticksLeft = State == Phase.Announced
            ? rules.AnnounceTicks - (tick - _announcedAt)
            : rules.DurationTicks - (tick - _openedAt);
        return new DemandMsg(
            now,
            Room.SystemId,
            Room.Balance.SystemDef.Name,
            Place.Key,
            Place.Name,
            Cause.Id,
            Cause.Title,
            [.. Cause.GoodList],
            Math.Max(0, (int)Math.Round(ticksLeft / (double)SimConfig.TickRate)),
            Room.DemandLeft(Id),
            rules.Quota,
            Math.Round(Room.DemandMul(Id), 3));
    }

    /// <summary>Одно и то же всем пилотам галактики: кодируем раз.</summary>
    private static void Broadcast(Galaxy galaxy, DemandMsg? status)
    {
        if (status is null) return;
        var bytes = Protocol.Encode(status);
        foreach (var room in galaxy.Rooms)
            foreach (var player in room.Pilots)
                player.Connection?.SendRaw(bytes);
    }
}
