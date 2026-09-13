using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <param name="token">Сессия клиента; null — вернуться к кораблю после обрыва нельзя, он удаляется сразу.</param>
public sealed class Player(int id, string? token, string name, string hullId)
{
    /// <summary>Id корабля в снапшотах. Не меняется при переподключении, в отличие от id соединения.</summary>
    public int Id { get; } = id;
    public string? Token { get; } = token;
    public string Name { get; set; } = name;
    public string HullId { get; set; } = hullId;

    /// <summary>null — связи нет: корабль висит в космосе и тормозит, пока игрок не вернётся.</summary>
    public IClientConnection? Connection { get; private set; }
    public long LostAtTick { get; private set; }

    /// <summary>Поле, а не свойство: Movement.Step меняет состояние по ссылке.</summary>
    public ShipState Ship = new() { X = SimConfig.SpawnX, Y = SimConfig.SpawnY };

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
    }
}
