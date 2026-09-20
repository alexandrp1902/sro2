using Sro.Server.Game;

namespace Sro.Server.Tests;

/// <summary>
/// С M15.6 вход в игру всегда в доке (<c>Room.Enter</c>), а тестам боя, лута, метеоритов и NPC нужен
/// корабль в системе. Эти два расширения — «войти и сразу вылететь»: вылет ставит корабль в ту же точку,
/// из которой он появился (<c>DockOffset</c> считается от неё), так что проверки положения не меняются.
/// </summary>
internal static class TestEntry
{
    /// <summary>Вылететь из дока, в котором пилот оказался при входе.</summary>
    public static void Undock(this Room room, FakeConnection connection) => room.Dock(connection, on: false);

    /// <summary>То же в галактике: команда идёт в комнату, где сейчас корабль.</summary>
    public static void Undock(this Galaxy galaxy, FakeConnection connection) =>
        galaxy.With(connection, room => room.Dock(connection, on: false));
}
