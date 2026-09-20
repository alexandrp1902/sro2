using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

public class RoomTests
{
    private const string TokenA = "token-aaaaaaaaaaaaaaaa";
    private static readonly MoveInput Up = new(0, -1, 1);

    private readonly Room _room = new(TestBalance.Create(), NullLogger.Instance);
    private int _nextConnection;

    private FakeConnection Connect(string? token = null, string? name = "Pilot")
    {
        var connection = new FakeConnection(++_nextConnection);
        _room.Join(connection, token, name, null);
        _room.Undock(connection); // вход теперь в доке (M15.6), а здесь нужен корабль в космосе
        return connection;
    }

    /// <summary>Клиент шлёт по входу на тик, как настоящий.</summary>
    private int Fly(FakeConnection connection, int seq, int ticks, MoveInput input)
    {
        for (var i = 0; i < ticks; i++)
        {
            _room.Input(connection, ++seq, input);
            _room.Step();
        }
        return seq;
    }

    private void Steps(int ticks)
    {
        for (var i = 0; i < ticks; i++) _room.Step();
    }

    private static int IdOf(FakeConnection connection) => connection.Last<WelcomeMsg>().Id;

    private static ShipDto ShipOf(FakeConnection observer, int id) => observer.Last<SnapshotMsg>().Ships.Single(s => s.Id == id);

    private static PlayerDto PlayerOf(FakeConnection observer, int id) => observer.Last<PlayersMsg>().Players.Single(p => p.Id == id);

    private static double Speed(ShipDto ship) => Math.Sqrt(ship.Vx * ship.Vx + ship.Vy * ship.Vy);

    [Fact]
    public void Join_SendsWelcomeThenRoster()
    {
        var a = Connect(TokenA, "Alice");

        var welcome = Assert.IsType<WelcomeMsg>(a.Messages[0]);
        Assert.False(welcome.Resumed);
        var roster = Assert.IsType<PlayersMsg>(a.Messages[1]);
        Assert.Equal(new[] { new PlayerDto(welcome.Id, "Alice", true) }, roster.Players);
    }

    [Fact]
    public void TwoPlayers_SeeEachOther()
    {
        var a = Connect(TokenA, "Alice");
        var b = Connect(null, "Bob");
        Assert.Equal(new[] { "Alice", "Bob" }, a.Last<PlayersMsg>().Players.Select(p => p.Name));

        _room.Step();
        foreach (var observer in new[] { a, b })
            Assert.Equal(new[] { IdOf(a), IdOf(b) }, observer.Last<SnapshotMsg>().Ships.Select(s => s.Id));
    }

    [Fact]
    public void LostConnection_ShipStaysInSpaceAndBrakes()
    {
        var a = Connect(TokenA);
        var b = Connect();
        var id = IdOf(a);
        Fly(a, 0, 40, Up);
        Assert.True(Speed(ShipOf(b, id)) > 100);

        _room.Disconnect(a);
        Assert.False(PlayerOf(b, id).Online);
        var snapshotsBefore = a.Count<SnapshotMsg>();

        Steps(100);
        var ghost = ShipOf(b, id);
        Assert.Equal(0, Speed(ghost));
        Assert.Equal(0, ghost.Th);
        Assert.Equal(snapshotsBefore, a.Count<SnapshotMsg>()); // отключённому снапшоты не шлём
    }

    [Fact]
    public void ReturnWithinGrace_RestoresTheSameShip()
    {
        var a = Connect(TokenA, "Alice");
        var b = Connect();
        var id = IdOf(a);
        Fly(a, 0, 40, Up);
        _room.Disconnect(a);
        Steps(100);
        var parked = ShipOf(b, id);

        var a2 = Connect(TokenA, "Alice");
        var welcome = a2.Last<WelcomeMsg>();
        Assert.True(welcome.Resumed);
        Assert.Equal(id, welcome.Id);
        Assert.Equal(new PlayerDto(id, "Alice", true), PlayerOf(b, id));

        _room.Step();
        var resumed = ShipOf(a2, id);
        // Чужой корабль приходит во float32, свой — во float64.
        Assert.Equal(parked.X, resumed.X, 3);
        Assert.Equal(parked.Y, resumed.Y, 3);

        // Новая сессия нумерует входы заново — сервер их принимает.
        Fly(a2, 0, 20, Up);
        var moving = ShipOf(a2, id);
        Assert.True(moving.Ack > 0);
        Assert.True(moving.Y < parked.Y - 10);
    }

    [Fact]
    public void NoReturnWithinGrace_RemovesTheShip()
    {
        var a = Connect(TokenA);
        var b = Connect();
        var id = IdOf(a);
        _room.Disconnect(a);

        Steps(Room.ReconnectGraceTicks - 1);
        Assert.Contains(b.Last<PlayersMsg>().Players, p => p.Id == id);

        Steps(2);
        Assert.DoesNotContain(b.Last<PlayersMsg>().Players, p => p.Id == id);
        Assert.DoesNotContain(b.Last<SnapshotMsg>().Ships, s => s.Id == id);

        var late = Connect(TokenA);
        Assert.False(late.Last<WelcomeMsg>().Resumed);
        Assert.NotEqual(id, IdOf(late));
    }

    [Fact]
    public void SameSessionFromAnotherConnection_ReplacesTheOldOne()
    {
        var a = Connect(TokenA);
        var b = Connect();
        var id = IdOf(a);

        var a2 = Connect(TokenA);
        Assert.Equal(Room.ReplacedCloseCode, a.ClosedWith);
        Assert.Equal(id, IdOf(a2));
        Assert.True(a2.Last<WelcomeMsg>().Resumed);

        // Старый сокет закрылся позже нового входа — корабль остаётся у нового соединения.
        _room.Disconnect(a);
        Assert.True(PlayerOf(b, id).Online);
        var before = a2.Count<SnapshotMsg>();
        _room.Step();
        Assert.Equal(before + 1, a2.Count<SnapshotMsg>());
    }

    [Fact]
    public void WithoutSession_ShipIsRemovedAtOnce()
    {
        var a = Connect(null);
        var b = Connect();
        var id = IdOf(a);

        _room.Disconnect(a);
        Assert.DoesNotContain(b.Last<PlayersMsg>().Players, p => p.Id == id);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces in the token")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void InvalidToken_MakesANewPlayer(string token)
    {
        var a = Connect(token);
        var a2 = Connect(token);

        Assert.Null(a.ClosedWith);
        Assert.NotEqual(IdOf(a), IdOf(a2));
        Assert.False(a2.Last<WelcomeMsg>().Resumed);
    }

    [Theory]
    [InlineData("  Bob  ", "Bob")]
    [InlineData("Bob", "Bob")]
    [InlineData("A\u200BB\u202E", "AB")]
    [InlineData("a \t  b", "a b")]
    [InlineData("0123456789abcdefXYZ", "0123456789abcdef")]
    [InlineData("123456789012345😀", "123456789012345")]
    [InlineData("   ", Room.DefaultName)]
    [InlineData(null, Room.DefaultName)]
    public void SanitizeName(string? raw, string expected) => Assert.Equal(expected, Room.SanitizeName(raw));

    [Fact]
    public void DuplicateNames_GetNumbers()
    {
        Connect(null, "Bob");
        Connect(null, "bob");
        Connect(null, "0123456789abcdef");
        var last = Connect(null, "0123456789abcdef");

        Assert.Equal(
            new[] { "Bob", "bob 2", "0123456789abcdef", "0123456789abcd 2" },
            last.Last<PlayersMsg>().Players.Select(p => p.Name));
    }

    [Fact]
    public void Rename_IsBroadcastAndKeptUnique()
    {
        var a = Connect(null, "Alice");
        var b = Connect(null, "Bob");

        _room.Rename(b, "  Alice ");
        Assert.Equal(new[] { "Alice", "Alice 2" }, a.Last<PlayersMsg>().Players.Select(p => p.Name));

        var rosters = a.Count<PlayersMsg>();
        _room.Rename(b, "Alice 2"); // имя не меняется — рассылки нет
        Assert.Equal(rosters, a.Count<PlayersMsg>());
    }
}
