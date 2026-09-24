using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>Комната — участник галактических событий: награда за голову с делёжкой в группе и вторжение пиратов.</summary>
public sealed partial class Room
{
    /// <summary>Идёт вторжение с этим номером (<see cref="InvasionDirector"/>); 0 — нет. Обычные налёты тем временем ждут.</summary>
    public int InvasionId { get; private set; }

    /// <summary>
    /// Пилот сбил корабль. Пират — награда за голову (GDD §31): её делят участники группы убийцы рядом со сбитым
    /// (GDD §37), и каждому из них он засчитывается в задание. Остальное — только убийце.
    /// </summary>
    private void Credit(Player killer, ShipEntity? victim)
    {
        // Сюжетный корабль не стоит ничего и не значит ничего для мира (M20a): ни головы, ни зачёта
        // в работу с доски, ни отношения. Проверка стоит первой, до разбора на фракции: повстанцы —
        // пираты по механике, и без неё они платили бы награду и красили пилота в глазах Новы.
        if (victim is Pirate { Story: true }) return;
        if (victim is not Pirate { Type.IsPirate: true } pirate)
        {
            NoteKillRep(killer, victim);
            CountKill(killer, victim);
            return;
        }
        var events = Balance.Reputation.Event;
        var team = Team(killer, pirate);
        var share = Balance.Party.Share(Balance.Npc.Bounty(pirate.Type, pirate.Level), team.Count);
        foreach (var member in team)
        {
            if (share > 0)
            {
                member.Credits += share;
                SendCargo(member);
                Save(member);
                member.Connection?.Send(new BountyMsg(share, team.Count, pirate.Name));
            }
            CountKill(member, pirate);
            // Голова пирата красит пилота в глазах системы — но мало и с потолком за час:
            // исправлять репутацию надо делом, а не отстрелом ближайшего логова.
            AddSystemRep(member, events.PirateKill, Protocol.RepPirate, events.PirateHourly);
        }
    }

    /// <summary>
    /// Кого пилот сбил, кроме пирата. Торговец и рейнджер — это преступление в глазах властей;
    /// игрок — только там, где драка не разрешена системой.
    /// </summary>
    private void NoteKillRep(Player killer, ShipEntity? victim)
    {
        var events = Balance.Reputation.Event;
        switch (victim)
        {
            case Trader trader:
                AddSystemRep(killer, events.TraderKill, Protocol.RepTraderKill);
                // И той станции, что ждала груз: конвой до неё не дошёл.
                if (trader.Destination(SystemId) is { } destination && Balance.Galaxy.System(destination) is { Station: true })
                    AddRep(killer, Reputation.Station(destination), events.TraderPlace, Protocol.RepTraderKill);
                break;
            case Pirate { Type.IsRanger: true }:
                AddSystemRep(killer, events.RangerKill, Protocol.RepRangerKill);
                break;
            // В free-системах драка согласованная: там за убийство игрока не спрашивают.
            case Player when Balance.SystemDef.Pvp != GalaxyRules.PvpFree:
                AddSystemRep(killer, events.PlayerKill, Protocol.RepPlayerKill);
                break;
        }
    }

    /// <summary>Убийца и те из его группы, кто в этой системе, в космосе, жив и не дальше shareRange от сбитого.</summary>
    private List<Player> Team(Player killer, ShipEntity victim)
    {
        var team = new List<Player> { killer };
        if (_host?.PartyMembers(killer.Id) is not { } members) return team;
        var range = Balance.Party.ShareRange;
        foreach (var id in members)
        {
            if (id == killer.Id || _players.GetValueOrDefault(id) is not { IsDead: false, Docked: false } member) continue;
            var dx = member.Ship.X - victim.Ship.X;
            var dy = member.Ship.Y - victim.Ship.Y;
            if (dx * dx + dy * dy <= range * range) team.Add(member);
        }
        return team;
    }

    /// <summary>Вклад во вторжение (GDD §38): урон пилотов по его пиратам в этот тик — галактике.</summary>
    private void NoteInvasionDamage()
    {
        if (InvasionId == 0 || _host is null) return;
        foreach (var shot in _shots)
        {
            if (!shot.Hit || shot.Dmg <= 0) continue;
            if (_ships.GetValueOrDefault(shot.To) is not Pirate { InvasionId: var id } || id != InvasionId) continue;
            if (_players.GetValueOrDefault(shot.From) is { } player) _host.Contributed(player, id, shot.Dmg);
        }
    }

    /// <summary>
    /// Точка сбора вторжения: за станцией, прочь от звезды, на pointOffset — вне укрытия, но рядом с ним: отступить есть куда.
    /// </summary>
    public (double X, double Y) InvasionPoint()
    {
        var (sx, sy) = StationPosition;
        var r = Math.Sqrt(sx * sx + sy * sy);
        var (ux, uy) = r > 1 ? (sx / r, sy / r) : (0.0, -1.0);
        var offset = Balance.Invasion.PointOffset;
        var limit = NpcRules.WorldLimit - Balance.Npc.PatrolRadius;
        return (Math.Clamp(sx + ux * offset, -limit, limit), Math.Clamp(sy + uy * offset, -limit, limit));
    }

    /// <summary>
    /// Волна вторжения: пираты прилетают через случайные врата (каждая группа — через свои) и летят к точке сбора;
    /// уровень — с поправкой на опасность системы. Без врат — сразу на точке.
    /// </summary>
    /// <returns>Сколько пиратов прилетело.</returns>
    public int SpawnInvasion(int id, IReadOnlyList<InvasionGroup> wave, (double X, double Y) point)
    {
        InvasionId = id;
        var count = SpawnWave(wave, point, invasionId: id, missionId: 0);
        BroadcastPlayers();
        return count;
    }

    /// <summary>
    /// Волна пиратов по группам: каждая прилетает через свои случайные врата и идёт к точке; без врат —
    /// сразу на точке. Уровень — с поправкой на опасность системы. Сама уйти волна не может: её кончает тот,
    /// кто её выпустил. Общая для вторжений (GDD §38) и для засад на конвой (M14).
    /// </summary>
    /// <param name="onSite">Появиться прямо на точке, не залетая с врат: так встают засады перед конвоем.</param>
    /// <returns>Сколько пиратов прилетело.</returns>
    /// <param name="storyName">
    /// Имя над корпусом для кораблей сюжета (M20a); null — обычное «Тип Ур.N». Заодно помечает их
    /// сюжетными: за таких не платят и отношение за них не меняется.
    /// </param>
    private int SpawnWave(
        IReadOnlyList<InvasionGroup> wave,
        (double X, double Y) point,
        int invasionId,
        int missionId,
        bool onSite = false,
        string? storyName = null)
    {
        var npc = Balance.Npc;
        var gates = onSite ? [] : Balance.SystemDef.GateList;
        var danger = Balance.SystemDef.Danger;
        var label = invasionId != 0 ? $"Invasion {invasionId}" : $"Ambush {missionId}";
        var slot = 0;
        foreach (var group in wave)
        {
            if (!npc.TypeMap.TryGetValue(group.Type, out var type)) continue;
            var level = group.LevelIn(danger);
            var spot = new NpcSpawn(group.Type, level, point.X, point.Y, group.Count);
            var gate = gates.Count > 0 ? gates[_ai.Next(gates.Count)] : null;
            var raidId = ++_raidCount;
            for (var i = 0; i < group.Count; i++, slot++)
            {
                var pirate = new Pirate(_newId(), spot, slot, type, npc)
                {
                    RaidId = raidId,
                    InvasionId = invasionId,
                    MissionId = missionId,
                    ExitX = gate?.X ?? point.X,
                    ExitY = gate?.Y ?? point.Y,
                    ExitIsGate = gate is not null,
                    PatrolTicks = long.MaxValue / 4,
                };
                if (storyName is not null)
                {
                    pirate.Story = true;
                    pirate.Name = storyName;
                }
                SpawnHere(pirate);
                if (gate is null)
                {
                    pirate.PatrolUntilTick = Tick + pirate.PatrolTicks;
                }
                else
                {
                    var (sx, sy) = pirate.SpawnPoint;
                    var x = gate.X + sx - point.X;
                    var y = gate.Y + sy - point.Y;
                    pirate.Ship = new ShipState { X = x, Y = y, Rot = Math.Atan2(point.X - x, -(point.Y - y)) };
                    pirate.State = PirateState.Return;
                }
                _pirates.Add(pirate);
                _ships[pirate.Id] = pirate;
            }
            _log.LogInformation(
                "{Label}: {Count} × {Type} Ур.{Level} {From} → ({X:0}, {Y:0})",
                label, group.Count, group.Type, level, gate is null ? "on site" : $"from the gate to {gate.To}", point.X, point.Y);
        }
        return slot;
    }

    /// <summary>Пиратов этого вторжения в строю: живых и не ушедших.</summary>
    public int InvadersLeft(int id) => _pirates.Count(p => p.InvasionId == id && !p.IsDead && !p.Gone);

    /// <summary>Вторжение окончено: уцелевшие его пираты уходят, обычные налёты возвращаются.</summary>
    public void EndInvasion(int id)
    {
        if (InvasionId == id) InvasionId = 0;
        foreach (var pirate in _pirates)
        {
            if (pirate.InvasionId != id || pirate.IsDead || pirate.State == PirateState.Leave) continue;
            pirate.State = PirateState.Leave;
            pirate.TargetId = 0;
            pirate.FireHeld = false;
            pirate.PatrolUntilTick = Tick;
        }
    }
}
