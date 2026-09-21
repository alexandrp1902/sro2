using Microsoft.Extensions.Logging;
using Sro.Server.Net;

namespace Sro.Server.Game;

/// <summary>
/// Обмен между игроками (M16b). Живёт в галактике, а не в комнате: комната отдаёт пилота при прыжке, и
/// сессию пришлось бы переносить. Сама сделка исполняется в комнате (<see cref="Room.Swap"/>) — оба
/// участника по условию стоят рядом, то есть в одной системе, а трюм и профиль знает комната.
/// </summary>
public sealed partial class Galaxy
{
    /// <summary>Команда обмена от клиента.</summary>
    public void Trade(IClientConnection connection, string? action, int id, int credits,
        IReadOnlyDictionary<string, int>? items, int rev)
    {
        if (RoomOf(connection)?.PlayerOf(connection) is not { } player) return;
        switch (action)
        {
            case TradeCodes.InviteAction:
            {
                if (FindPilot(id) is not { Player: { Connection: { } to } target })
                {
                    TradeEvent(player, TradeCodes.Gone);
                    return;
                }
                if (Refuse(player, target) is { } why)
                {
                    TradeEvent(player, why, target.Name);
                    return;
                }
                if (_trades.Invite(player.Id, target.Id, Tick, Balance.Trade.InviteTicks) is { } problem)
                {
                    TradeEvent(player, problem, target.Name);
                    return;
                }
                to.Send(new TradeInviteMsg(player.Id, player.Name, Balance.Trade.InviteSeconds));
                TradeEvent(player, TradeCodes.Invited, target.Name);
                _log.LogInformation("Player {From} offered a trade to {To}", player.Id, target.Id);
                return;
            }
            case TradeCodes.AcceptAction:
            {
                if (FindPilot(id) is not { Player: var host })
                {
                    _trades.Decline(player.Id, id);
                    TradeEvent(player, TradeCodes.Gone);
                    return;
                }
                if (_trades.Accept(player.Id, id, Tick) is { } problem)
                {
                    TradeEvent(player, problem, host.Name);
                    return;
                }
                // Условия могли разойтись, пока предложение висело: стол открывать незачем.
                if (Refuse(host, player) is { } why)
                {
                    CloseTrade(_trades.End(player.Id), why);
                    return;
                }
                var session = _trades.Of(player.Id)!;
                TradeEvent(host, TradeCodes.Opened, player.Name);
                TradeEvent(player, TradeCodes.Opened, host.Name);
                SendTrade(session);
                _log.LogInformation("Trade {A}<->{B} opened", session.A, session.B);
                return;
            }
            case TradeCodes.DeclineAction:
                if (_trades.Decline(player.Id, id) is not null && FindPilot(id)?.Player is { } inviter)
                    TradeEvent(inviter, TradeCodes.Declined, player.Name);
                return;
            case TradeCodes.OfferAction:
            {
                var loot = Balance.Loot;
                if (_trades.Set(player.Id, credits, items, loot.Knows) is { } session) SendTrade(session);
                return;
            }
            case TradeCodes.ReadyAction:
            {
                if (_trades.Ready(player.Id, rev, out var session) is { } problem)
                {
                    TradeEvent(player, problem);
                    if (session is not null) SendTrade(session); // «устарело» — покажем, что на столе сейчас
                    return;
                }
                if (!TradeBook.Agreed(session!))
                {
                    SendTrade(session!);
                    return;
                }
                Execute(session!);
                return;
            }
            case TradeCodes.CancelAction:
                CloseTrade(_trades.End(player.Id), TradeCodes.Left, player.Name);
                return;
        }
    }

    /// <summary>Почему этим двоим нельзя меняться; null — можно.</summary>
    private string? Refuse(Player a, Player b)
    {
        var rooms = (RoomOfPlayer(a.Id), RoomOfPlayer(b.Id));
        if (rooms.Item1 is null || rooms.Item2 is null) return TradeCodes.Gone;
        if (rooms.Item1 != rooms.Item2) return TradeCodes.Jumped;
        if (a.IsDead || b.IsDead) return TradeCodes.Dead;
        if (a.Docked || b.Docked) return TradeCodes.Docked;
        var range = Balance.Trade.Range;
        var dx = a.Ship.X - b.Ship.X;
        var dy = a.Ship.Y - b.Ship.Y;
        return dx * dx + dy * dy > range * range ? TradeCodes.TooFar : null;
    }

    private Room? RoomOfPlayer(int id) => FindPilot(id)?.Room;

    /// <summary>
    /// Истёкшие предложения и ежетиковая проверка условий: разошлись, состыковались, погибли или прыгнули —
    /// сделка рвётся. Проверяется и после того, как хуки комнаты уже сработали: на них одних держаться нельзя.
    /// </summary>
    private void StepTrades()
    {
        foreach (var expired in _trades.Expire(Tick))
        {
            if (FindPilot(expired.A)?.Player is { } host)
                TradeEvent(host, TradeCodes.Expired, FindPilot(expired.B)?.Player.Name);
        }
        foreach (var session in _trades.Sessions.ToList())
        {
            if (!session.Open || session.Done) continue;
            var a = FindPilot(session.A)?.Player;
            var b = FindPilot(session.B)?.Player;
            if (a is null || b is null)
            {
                CloseTrade(_trades.End(session.A), TradeCodes.Gone);
                continue;
            }
            if (Refuse(a, b) is { } why) CloseTrade(_trades.End(session.A), why);
        }
    }

    /// <summary>
    /// Пилот выбыл из обмена: док, гибель, прыжок, обрыв связи или выход. Сделка снимается со стола сразу —
    /// ждать тика нельзя, второй за это время успел бы её подтвердить.
    /// </summary>
    public void Busy(Player player, string code) => CloseTrade(_trades.End(player.Id), code, player.Name);

    /// <summary>Сделки больше нет: обоим код и пустой стол. Окно закрывается только по этому сообщению.</summary>
    private void CloseTrade(TradeSession? session, string code, string? name = null)
    {
        if (session is null) return;
        foreach (var id in new[] { session.A, session.B })
        {
            if (FindPilot(id)?.Player is not { } player) continue;
            // Предложение ещё не принято: тому, кого звали, сообщать не о чем — он и стола не видел.
            if (!session.Open && id != session.A) continue;
            TradeEvent(player, code, name == player.Name ? FindPilot(session.Other(id))?.Player.Name : name);
            player.Connection?.Send(new TradeStateMsg(false, null, null, session.Rev));
        }
    }

    /// <summary>
    /// Исполнение. Сессия снимается со стола первым делом: второе подтверждение в том же тике её уже не найдёт.
    /// Дальше — все проверки и только потом запись; резервировать заранее нечего, трюм до конца принадлежит хозяину.
    /// </summary>
    private void Execute(TradeSession session)
    {
        session.Done = true;
        _trades.End(session.A);
        var a = FindPilot(session.A);
        var b = FindPilot(session.B);
        if (a is null || b is null || a.Value.Room != b.Value.Room)
        {
            Tell(session, TradeCodes.Gone);
            return;
        }
        var problem = a.Value.Room.Swap(a.Value.Player, b.Value.Player, session.OfferA, session.OfferB);
        Tell(session, problem ?? TradeCodes.Done);
        if (problem is null)
            _log.LogInformation("Trade {A}<->{B} done, rev {Rev}", session.A, session.B, session.Rev);
    }

    /// <summary>Итог сделки обоим: код и пустой стол — окно закрывается.</summary>
    private void Tell(TradeSession session, string code)
    {
        foreach (var id in new[] { session.A, session.B })
        {
            if (FindPilot(id)?.Player is not { } player) continue;
            TradeEvent(player, code, FindPilot(session.Other(id))?.Player.Name);
            player.Connection?.Send(new TradeStateMsg(false, null, null, session.Rev));
        }
    }

    /// <summary>Стол обеим сторонам: своя половина и чужая.</summary>
    private void SendTrade(TradeSession session)
    {
        foreach (var id in new[] { session.A, session.B })
        {
            if (FindPilot(id)?.Player is not { Connection: { } connection } player) continue;
            connection.Send(new TradeStateMsg(true, Offer(id, player.Name), Offer(session.Other(id), null), session.Rev));
        }
        return;

        TradeOfferDto Offer(int id, string? name) => new(
            id,
            name ?? FindPilot(id)?.Player.Name ?? "",
            session.OfferOf(id).Credits,
            new Dictionary<string, int>(session.OfferOf(id).Items, StringComparer.Ordinal),
            session.OfferOf(id).Ready);
    }

    private static void TradeEvent(Player player, string code, string? name = null) =>
        player.Connection?.Send(new TradeEventMsg(code, name));
}
