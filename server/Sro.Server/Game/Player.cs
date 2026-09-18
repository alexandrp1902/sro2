using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <param name="token">Сессия клиента; null — вернуться к кораблю после обрыва нельзя, он удаляется сразу.</param>
public sealed class Player(int id, string? token, string name, string hullId, string weaponId)
    : ShipEntity(id, name, hullId, weaponId)
{
    public string? Token { get; } = token;

    /// <summary>null — связи нет: корабль висит в космосе и тормозит, пока игрок не вернётся.</summary>
    public IClientConnection? Connection { get; private set; }
    public long LostAtTick { get; private set; }

    /// <summary>
    /// Выбранный предмет (боевой документ §45); 0 — нет. На подбор не влияет: тракторный луч берёт всё,
    /// что попало в радиус. Сервер хранит выбор, чтобы гасить его, когда предмет исчез, и ради M6.
    /// </summary>
    public int SelectedLootId { get; set; }

    /// <summary>Трюм (GDD §21). Переживает уничтожение корабля (§24) и обрыв связи — он у игрока, а не у корабля.</summary>
    public Cargo Cargo { get; } = new();

    /// <summary>До этого тика про полный трюм молчим: иначе сообщение повторялось бы каждый тик у обломков.</summary>
    public long CargoFullUntilTick;

    /// <summary>Кредиты за сданный на станции груз.</summary>
    public int Credits;

    public InputBuffer Inputs { get; private set; } = new(new MoveInput(0, -1, 0));

    /// <summary>Вход корабля без связи: курс прежний, тяга 0 — Movement сам гасит скорость.</summary>
    public MoveInput StopInput => Inputs.Last with { Throttle = 0 };

    public void Attach(IClientConnection connection)
    {
        Connection = connection;
        // Новая сессия нумерует входы с 1 — старый буфер отбросил бы их как устаревшие.
        Inputs = new InputBuffer(StopInput);
    }

    public void Detach(long tick)
    {
        Connection = null;
        LostAtTick = tick;
        FireHeld = false; // иначе корабль без связи палил бы сам до возвращения игрока
    }
}
