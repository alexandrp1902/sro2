using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

public sealed class Player(IClientConnection connection, string name, string hullId)
{
    public IClientConnection Connection { get; } = connection;
    public string Name { get; } = name;
    public string HullId { get; set; } = hullId;

    /// <summary>Поле, а не свойство: Movement.Step меняет состояние по ссылке.</summary>
    public ShipState Ship = new() { X = SimConfig.SpawnX, Y = SimConfig.SpawnY };

    public InputBuffer Inputs { get; } = new(new MoveInput(0, -1, 0));
}
