using Sro.Server.Net;
using Sro.Sim;
using Sro.Sim.Mech;

namespace Sro.Server.Game;

/// <summary>
/// Наземный бой одного пилота (M21): бой, кампания (null — тренировка), и с какого тика пилот
/// без связи (null — на связи).
/// </summary>
public sealed class MechSession(string account, string? campaign, MechBattle battle)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int Revision { get; set; }
    public Dictionary<string, MechStateMsg> Requests { get; } = [];
    public MechResult? PendingResult { get; set; }
    public uint ResultSeed { get; set; }
    public string? StartRequest { get; set; }
    public string Account { get; } = account;
    public string? Campaign { get; } = campaign;
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

    public bool InMech(Player player) => MechOf(player.AccountId) is not null || TerritoryOf(player.AccountId) is not null || InRace(player);

    /// <summary>Действие наземного боя от игрока.</summary>
    public void Mech(IClientConnection connection, MechActMsg message)
    {
        if (RoomOf(connection) is not { } room || room.PlayerOf(connection) is not { } player) return;
        // Аккаунт нужен, чтобы восстановить бой после переподключения.
        if (player.AccountId is not { } account) return;
        if (TerritoryMech(connection,message,player)) return;
        var session = MechOf(account);
        if (message.Request is not null && player.Workshop.Results.FirstOrDefault(r => r.StartRequest == message.Request) is { } duplicateStart)
        { connection.Send(new MechEndMsg(duplicateStart.Won,duplicateStart.Credits,duplicateStart.Credits>0,Result:duplicateStart)); return; }
        if (message.Session is { } ended && player.Workshop.Results.FirstOrDefault(r => r.Session == ended) is { } saved)
        {
            connection.Send(new MechEndMsg(saved.Won, saved.Credits, saved.Credits > 0, Result: saved)); return;
        }
        switch (message.Act)
        {
            case Protocol.MechStart:
            case Protocol.MechTrain:
                if (session is not null)
                {
                    if (message.Request is not null && session.Requests.ContainsKey(message.Request)) { connection.Send(State(session, true)); return; }
                    if (session.Battle.Rules.Workshop is not null) connection.Send(new MechRefusedMsg("mechBusy"));
                    connection.Send(State(session, true));
                    return;
                }
                StartMech(connection, room, player, account, message, training: message.Act == Protocol.MechTrain);
                return;
            case Protocol.MechQuit:
                if (session is null) return;
                if (!CheckRevision(connection, session, message)) return;
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
        if (!CheckRevision(connection, session, message)) return;
        if (battle.Over) { EndMech(session, room, player); return; }
        if (battle.Turn != MechBattle.PlayerSide || battle.Over)
        {
            connection.Send(new MechRefusedMsg(battle.Over ? MechCodes.Over : MechCodes.NotYourTurn));
            return;
        }
        var events = new List<MechEvent>();
        if (battle.Current?.Control != "player") { connection.Send(new MechRefusedMsg(MechCodes.NotYourTurn)); return; }
        var command = new MechCommand(message.Act ?? "", message.X, message.Y, message.Dir, message.Target, message.Part, message.Unit, message.Hand);
        if (battle.Apply(command, events) is { } refused)
        {
            connection.Send(new MechRefusedMsg(refused));
            return;
        }
        battle.RunEnemies(events);
        session.Revision++;
        connection.Send(new MechEventsMsg(events));
        var state = State(session);
        connection.Send(state);
        if (message.Request is not null) session.Requests[message.Request] = state;
        if (battle.Over) EndMech(session, room, player);
    }

    private static MechStateMsg State(MechSession s, bool rules = false) => new(s.Battle.View(), rules ? s.Battle.Rules : null, s.Id, s.Revision);

    private static bool CheckRevision(IClientConnection c, MechSession s, MechActMsg m)
    {
        if (m.Request is not null && s.Requests.TryGetValue(m.Request, out var known)) { c.Send(known); return false; }
        if (s.Battle.Rules.Workshop is not null && (m.Session != s.Id || m.Revision != s.Revision || string.IsNullOrEmpty(m.Request)
            || m.Request.Length > 80 || m.Act is not ("quit" or "select") && m.Unit is null))
        { c.Send(new MechRefusedMsg("staleRevision")); c.Send(State(s, true)); return false; }
        return true;
    }

    private void StartMech(IClientConnection connection, Room room, Player player, string account, MechActMsg message, bool training)
    {
        var rules = Balance.Mechs;
        if (rules.Workshop is not null && (string.IsNullOrEmpty(message.Request) || message.Request.Length > 80))
        { connection.Send(new MechRefusedMsg("staleRevision")); return; }
        var campaign = training ? null : room.RelayHere(player);
        var missionId = training ? MechRules.FirstSortie : message.Mission ?? MechRules.FirstSortie;
        room.MigrateMechs(player);
        if ((training ? !room.MechTrainingHere(player) : campaign is null) || rules.Mission(missionId) is not { } mission
            || !training && rules.Workshop is { } workshop && (player.DockedPlace != workshop.Place || campaign != workshop.Campaign || !room.MechAvailable(player).Contains(missionId)))
        {
            connection.Send(new MechRefusedMsg(MechCodes.NoRelay));
            return;
        }
        OwnedMech[] squad = [];
        if (!training && rules.Workshop is not null)
        {
            var ids = message.Squad ?? player.Workshop.Squad.Take(mission.Player.Length).ToArray();
            if (ids.Length != mission.Player.Length || ids.Distinct().Count() != ids.Length)
            { connection.Send(new MechRefusedMsg("squadRequired")); room.SendWorkshop(player); return; }
            var selected = ids.Select(id => player.Workshop.Mechs.FirstOrDefault(m => m.Id == id)).ToArray();
            if (selected.Any(m => m is null)) { connection.Send(new MechRefusedMsg("ownership")); return; }
            squad = selected.Select(m => m!).ToArray();
            var defs = squad.Select(m => MechCatalog.Resolve(rules, player.Workshop, m)).ToArray();
            if (defs.Any(d => d is null || MechCatalog.Fitting(rules, d) is not null))
            { connection.Send(new MechRefusedMsg("incomplete")); room.SendWorkshop(player); return; }
            var units = new Dictionary<string, MechUnitDef>(rules.UnitMap);
            var spawns = mission.Player.ToArray();
            for (var i = 0; i < squad.Length; i++) { units[squad[i].Id] = defs[i]!; spawns[i] = spawns[i] with { Unit = squad[i].Id }; }
            mission = mission with { Player = spawns };
            rules = rules with { Units = units, Missions = new Dictionary<string, MechMission>(rules.MissionMap) { [missionId] = mission } };
        }
        if (training)
        {
            // Та же карта, сборки и противники, но самостоятельный бой без сюжетных наград и спойлеров.
            mission = mission with
            {
                Name = "Тренировочная вылазка",
                Reward = 0,
                Intro = new MechLines("Ева Морен", "инженер рудника",
                    ["Полигон на связи. Прототип против двух налётчиков — отработайте движение, стрельбу и защиту щитом.",
                     "Щит слева. Заходите противнику во фланг и берегите спину. Тренировку можно повторять бесплатно."]),
                Win = new MechLines("Ева Морен", "инженер рудника", ["Тренировка пройдена. Хотите попробовать другую тактику — полигон всегда открыт."]),
                Lose = new MechLines("Ева Морен", "инженер рудника", ["Тренировка завершена. Прототип восстановим — можете попробовать снова."]),
            };
            rules = rules with { Missions = new Dictionary<string, MechMission>(rules.MissionMap) { [MechRules.FirstSortie] = mission } };
        }
        var seed = (uint)_mechSeeds.Next(1, int.MaxValue);
        var session = new MechSession(account, campaign, new MechBattle(rules, missionId, seed)) { ResultSeed = seed ^ 0xA5A5A5A5u, StartRequest=message.Request };
        for (var i = 0; i < squad.Length; i++)
        { var u = session.Battle.Unit($"p{i + 1}")!; u.Owner = squad[i].Id; u.Skills = squad[i].Skills ?? []; }
        if (message.Request is not null) session.Requests[message.Request] = State(session, true);
        _mechs[account] = session;
        connection.Send(State(session, true));
        if (campaign is not null) room.MechSay(player, campaign, missionId, mission.Intro);
        else TrainingSay(player, mission.Intro);
        _log.LogInformation("Player {Id} started the mech mission {Mission} (seed {Seed})", player.Id, MechRules.FirstSortie, seed);
    }

    /// <summary>Бой кончен: итог игроку, награда за первую победу, реплика Евы.</summary>
    private void EndMech(MechSession session, Room room, Player player)
    {
        var battle = session.Battle;
        var won = battle.Winner == MechBattle.PlayerSide;
        var first = false;
        MechResult? result = null;
        if (session.Campaign is not null && battle.Rules.Workshop is not null)
        {
            first = won && !player.Workshop.Completed.Contains(battle.MissionId);
            result = session.PendingResult ??= new(session.Id, battle.MissionId, won, battle.Reason ?? (won ? "objectiveComplete" : "squadDestroyed"),
                first ? battle.Mission.Reward : 0, first ? battle.Mission.RewardParts ?? [] : [],
                [.. battle.Units.Where(u => u.Owner is not null).Select(u => new MechExperience(u.Owner!, u.Def.Name,
                    u.Xp + (won && u.Alive ? battle.Rules.ProgressionDef.VictoryXp + battle.Mission.ObjectiveXp : 0), u.Def.Level, 0, []))],session.StartRequest);
            var draft = result with { Experience = result.Experience.Select(e => e with { NewSkills = [] }).ToArray() };
            if (!room.RecordMechResult(player, draft, battle.Rules, new MechRandom(session.ResultSeed)))
            { player.Connection?.Send(new MechRefusedMsg("saveFailed")); return; }
            result = player.Workshop.Results.First(r => r.Session == session.Id);
            room.MechSay(player, session.Campaign, battle.MissionId, won ? battle.Mission.Win : battle.Mission.Lose);
        }
        else if (session.Campaign is { } campaign)
        {
            if (won) first = room.MechWon(player, campaign, battle.MissionId, battle.Mission);
            else room.MechSay(player, campaign, battle.MissionId, battle.Mission.Lose);
        }
        else TrainingSay(player, won ? battle.Mission.Win : battle.Mission.Lose);
        _mechs.Remove(session.Account);
        player.Connection?.Send(new MechEndMsg(won, first ? battle.Mission.Reward : 0, first, Training: session.Campaign is null, Result: result));
        room.SendWorkshop(player);
        _log.LogInformation("Player {Id} {Result} the mech mission {Mission} in {Rounds} rounds",
            player.Id, won ? "won" : "lost", battle.MissionId, battle.Round);
    }

    private static void TrainingSay(Player player, MechLines? lines)
    {
        if (lines is not null)
            player.Connection?.Send(new DialogMsg("", MechRules.FirstSortie, lines.Who, lines.Role, lines.Lines));
    }

    /// <summary>Пилот вернулся: бой продолжается с того же места.</summary>
    private void MechResume(Player player)
    {
        if (MechOf(player.AccountId) is not { } session) return;
        session.LostAt = null;
        player.Connection?.Send(State(session, true));
    }

    /// <summary>Связь потеряна: бой ждёт <see cref="MechGraceTicks"/>.</summary>
    private void MechLost(Player player)
    {
        if (MechOf(player.AccountId) is { } session) session.LostAt = Tick;
    }

    /// <summary>Брошенные бои сохраняют опыт за уничтожения. Уже рассчитанный итог переживает сбой диска и обрыв связи.</summary>
    private void StepMech()
    {
        if (_mechs.Count == 0) return;
        foreach (var session in _mechs.Values.ToList())
        {
            if (session.LostAt is not { } lost || Tick - lost < MechGraceTicks) continue;
            if (session.Campaign is not null && session.Battle.Rules.Workshop is not null && _accounts?.Profile(session.Account) is { } profile)
            {
                var state=profile.Workshop ?? MechWorkshopState.Empty;
                if (!state.Results.Any(r=>r.Session==session.Id))
                {
                    var rules=session.Battle.Rules;
                    var result=session.PendingResult ?? new MechResult(session.Id,session.Battle.MissionId,false,"disconnected",0,[],
                        [..session.Battle.Units.Where(u=>u.Owner is not null).Select(u=>new MechExperience(u.Owner!,u.Def.Name,u.Xp,u.Def.Level,0,[]))],session.StartRequest);
                    state=MechCatalog.CommitResult(rules,state,result,new(session.ResultSeed),out result);
                    var story=new Dictionary<string,Sro.Server.Accounts.StoryProgress>(profile.Story ?? new Dictionary<string,Sro.Server.Accounts.StoryProgress>());
                    if(result.Won && rules.Workshop is { } workshop && story.TryGetValue(workshop.Campaign,out var log))
                        story[workshop.Campaign]=log with {Done=[..log.Done.Where(id=>id!=result.Mission),result.Mission],
                            Flags=result.Mission==MechRules.FirstSortie?[..(log.Flags ?? []).Where(id=>id!=Protocol.SortieWonFlag),Protocol.SortieWonFlag]:log.Flags};
                    if (!_accounts.SaveDurable(session.Account,profile with {Workshop=state,Credits=profile.Credits+result.Credits,Story=story})) continue;
                }
            }
            _mechs.Remove(session.Account);
            _log.LogInformation("Mech session of account {Account} closed: no connection for {Seconds} s",
                session.Account, MechGraceTicks / SimConfig.TickRate);
        }
    }
}
