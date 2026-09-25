using Sro.Server.Net;
using Sro.Sim;
using Sro.Sim.Mech;

namespace Sro.Server.Game;

/// <summary>
/// Наземный бой одного пилота (M21): бой, кампания, чей ретранслятор его открыл, и с какого тика пилот
/// без связи (null — на связи).
/// </summary>
public sealed class MechSession(string account, string campaign, MechBattle battle)
{
    public string Account { get; } = account;
    public string Campaign { get; } = campaign;
    public MechBattle Battle { get; } = battle;
    public long? LostAt { get; set; }
}

/// <summary>
/// Галактика и наземные бои (M21). Сессия живёт здесь, а не в комнате: комната забывает пилота без связи
/// через минуту (<see cref="Room.ReconnectGraceTicks"/>), а бой обязан ждать его пять. Ключ — аккаунт:
/// вернувшийся с другого устройства попадает в тот же бой. Бой пошаговый, тика у него нет — только
/// «запрос — проверка — результат»; тик нужен лишь чтобы закрыть брошенный.
/// </summary>
public sealed partial class Galaxy
{
    /// <summary>Сколько ждём пилота, потерявшего связь посреди боя, прежде чем засчитать поражение.</summary>
    public const int MechGraceTicks = 5 * 60 * SimConfig.TickRate;

    private readonly Dictionary<string, MechSession> _mechs = new(StringComparer.Ordinal);
    private readonly Random _mechSeeds;

    /// <summary>Идущий бой аккаунта; null — боя нет.</summary>
    public MechSession? MechOf(string? account) => account is not null ? _mechs.GetValueOrDefault(account) : null;

    public bool InMech(Player player) => MechOf(player.AccountId) is not null;

    /// <summary>Действие наземного боя от игрока.</summary>
    public void Mech(IClientConnection connection, MechActMsg message)
    {
        if (RoomOf(connection) is not { } room || room.PlayerOf(connection) is not { } player) return;
        // Гостю бой не положен: у него нет кампании, а значит и ретранслятора.
        if (player.AccountId is not { } account) return;
        var session = MechOf(account);
        switch (message.Act)
        {
            case Protocol.MechStart:
                if (session is not null)
                {
                    connection.Send(new MechStateMsg(session.Battle.View(), session.Battle.Rules));
                    return;
                }
                StartMech(connection, room, player, account);
                return;
            case Protocol.MechQuit:
                if (session is null) return;
                session.Battle.Surrender();
                EndMech(session, room, player);
                return;
        }

        if (session is null)
        {
            connection.Send(new MechRefusedMsg(MechCodes.NoBattle));
            return;
        }
        var battle = session.Battle;
        if (battle.Turn != MechBattle.PlayerSide || battle.Over)
        {
            connection.Send(new MechRefusedMsg(battle.Over ? MechCodes.Over : MechCodes.NotYourTurn));
            return;
        }
        var events = new List<MechEvent>();
        var command = new MechCommand(message.Act ?? "", message.X, message.Y, message.Dir, message.Target, message.Part);
        if (battle.Apply(command, events) is { } refused)
        {
            connection.Send(new MechRefusedMsg(refused));
            return;
        }
        battle.RunEnemies(events);
        connection.Send(new MechEventsMsg(events));
        connection.Send(new MechStateMsg(battle.View()));
        if (battle.Over) EndMech(session, room, player);
    }

    private void StartMech(IClientConnection connection, Room room, Player player, string account)
    {
        var rules = Balance.Mechs;
        if (room.RelayHere(player) is not { } campaign || rules.Mission(MechRules.FirstSortie) is not { } mission)
        {
            connection.Send(new MechRefusedMsg(MechCodes.NoRelay));
            return;
        }
        var seed = (uint)_mechSeeds.Next(1, int.MaxValue);
        var session = new MechSession(account, campaign, new MechBattle(rules, MechRules.FirstSortie, seed));
        _mechs[account] = session;
        connection.Send(new MechStateMsg(session.Battle.View(), rules));
        room.MechSay(player, campaign, MechRules.FirstSortie, mission.Intro);
        _log.LogInformation("Player {Id} started the mech mission {Mission} (seed {Seed})", player.Id, MechRules.FirstSortie, seed);
    }

    /// <summary>Бой кончен: итог игроку, награда за первую победу, реплика Евы.</summary>
    private void EndMech(MechSession session, Room room, Player player)
    {
        _mechs.Remove(session.Account);
        var battle = session.Battle;
        var won = battle.Winner == MechBattle.PlayerSide;
        var first = false;
        if (won) first = room.MechWon(player, session.Campaign, battle.MissionId, battle.Mission);
        else room.MechSay(player, session.Campaign, battle.MissionId, battle.Mission.Lose);
        player.Connection?.Send(new MechEndMsg(won, first ? battle.Mission.Reward : 0, first));
        _log.LogInformation("Player {Id} {Result} the mech mission {Mission} in {Rounds} rounds",
            player.Id, won ? "won" : "lost", battle.MissionId, battle.Round);
    }

    /// <summary>Пилот вернулся: бой продолжается с того же места.</summary>
    private void MechResume(Player player)
    {
        if (MechOf(player.AccountId) is not { } session) return;
        session.LostAt = null;
        player.Connection?.Send(new MechStateMsg(session.Battle.View(), session.Battle.Rules));
    }

    /// <summary>Связь потеряна: бой ждёт <see cref="MechGraceTicks"/>.</summary>
    private void MechLost(Player player)
    {
        if (MechOf(player.AccountId) is { } session) session.LostAt = Tick;
    }

    /// <summary>Брошенные бои — поражение. Пилота уже нет в игре: награды и реплик не будет, только запись в журнал сервера.</summary>
    private void StepMech()
    {
        if (_mechs.Count == 0) return;
        foreach (var session in _mechs.Values.ToList())
        {
            if (session.LostAt is not { } lost || Tick - lost < MechGraceTicks) continue;
            _mechs.Remove(session.Account);
            _log.LogInformation("Mech mission of account {Account} lost: no connection for {Seconds} s",
                session.Account, MechGraceTicks / SimConfig.TickRate);
        }
    }
}
