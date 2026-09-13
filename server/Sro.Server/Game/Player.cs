using Sro.Server.Net;

namespace Sro.Server.Game;

public sealed class Player(IClientConnection connection, string name)
{
    public IClientConnection Connection { get; } = connection;
    public string Name { get; } = name;
}
