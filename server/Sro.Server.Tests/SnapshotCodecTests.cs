using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Game;
using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Tests;

/// <summary>
/// Бинарные дельта-снапшоты (M7): сборка на «клиенте» совпадает с тем, что было на сервере, дельты меньше ключевых
/// кадров, ушедшие сущности забываются, выброшенный кадр чинится ключевым. Эталон для клиента —
/// shared/test-vectors/snapshot.json; перегенерация: SRO_UPDATE_VECTORS=1 dotnet test.
/// </summary>
public class SnapshotCodecTests
{
    private const int Self = 1;

    private static readonly IReadOnlyList<ShotDto> NoShots = [];
    private static readonly IReadOnlyList<KillDto> NoKills = [];
    private static readonly IReadOnlyList<PickDto> NoPicks = [];

    private static ShipDto Ship(int id, double x, double y, int hp = 400, string? ai = null) =>
        new(id, x, y, 0.5, 10.25, -3.5, "light", 1, id == Self ? 7 : 0, hp, 150, "pulse", Ai: ai);

    private static SnapshotCodec.World World(
        long tick,
        IReadOnlyList<ShipDto> ships,
        IReadOnlyList<LootDto>? loot = null,
        IReadOnlyList<MeteorDto>? meteors = null,
        IReadOnlyList<ShotDto>? shots = null,
        IReadOnlyList<KillDto>? kills = null,
        IReadOnlyList<PickDto>? picks = null,
        IReadOnlyList<MissileDto>? missiles = null) =>
        new(tick, ships, loot, meteors, shots ?? NoShots, kills ?? NoKills, picks ?? NoPicks, missiles);

    private static bool Everywhere(double x, double y) => true;

    private static void AssertSame(ShipDto expected, ShipDto actual)
    {
        // Чужие корабли едут во float32 — сравниваем с его точностью.
        Assert.Equal(expected.X, actual.X, 2);
        Assert.Equal(expected.Y, actual.Y, 2);
        Assert.Equal(expected with { X = 0, Y = 0, R = 0, Vx = 0, Vy = 0, Th = 0 }, actual with { X = 0, Y = 0, R = 0, Vx = 0, Vy = 0, Th = 0 });
    }

    [Fact]
    public void RoundTrip_RebuildsTheFullSnapshot()
    {
        var encoder = new SnapshotCodec.Encoder();
        var decoder = new SnapshotCodec.Decoder();
        var ships = new[] { Ship(1, 100.123456789, 200.987654321), Ship(2, -500, 300, ai: "patrol") };
        var loot = new[] { new LootDto(10, 1200, -900, "metal", 4, 0, C: true) };
        var meteors = new[] { new MeteorDto(20, 0, -3000, 0, 250, "small", 60) };
        var shots = new[] { new ShotDto(2, 1, "pulse", true, 100, 100, 71.25) };

        var snapshot = decoder.Decode(encoder.Encode(World(5, ships, loot, meteors, shots), Self, Everywhere));

        Assert.Equal(5, snapshot.Tick);
        // Свой корабль — во float64, до последнего знака: на нём держится сверка предсказания.
        Assert.Equal(ships[0], snapshot.Ships.Single(s => s.Id == 1));
        AssertSame(ships[1], snapshot.Ships.Single(s => s.Id == 2));
        Assert.Equal(loot[0], Assert.Single(snapshot.Loot!));
        Assert.Equal(meteors[0], Assert.Single(snapshot.Meteors!));
        Assert.Equal(shots[0], Assert.Single(snapshot.Shots!));
        Assert.Null(snapshot.Kills);
    }

    [Fact]
    public void Delta_SendsOnlyWhatChanged()
    {
        var encoder = new SnapshotCodec.Encoder();
        var decoder = new SnapshotCodec.Decoder();
        var ships = Enumerable.Range(2, 20).Select(id => Ship(id, id * 100, 0, ai: "patrol")).Prepend(Ship(1, 0, 0)).ToList();
        var key = encoder.Encode(World(1, ships), Self, Everywhere);
        decoder.Decode(key);

        // Во втором кадре двинулся один пират, у другого упал щит.
        ships[3] = ships[3] with { X = 333 };
        ships[4] = ships[4] with { Sh = 10 };
        var delta = encoder.Encode(World(2, ships), Self, Everywhere);
        var snapshot = decoder.Decode(delta);

        Assert.True(delta.Length * 4 < key.Length, $"delta {delta.Length} B vs key {key.Length} B");
        Assert.Equal(21, snapshot.Ships.Count);
        Assert.Equal(333, snapshot.Ships.Single(s => s.Id == ships[3].Id).X, 3);
        Assert.Equal(10, snapshot.Ships.Single(s => s.Id == ships[4].Id).Sh);
        Assert.Equal("patrol", snapshot.Ships.Single(s => s.Id == 10).Ai);
        Assert.Equal(0, decoder.Desyncs);
    }

    [Fact]
    public void GoneEntities_AreForgotten_AndComeBackWhole()
    {
        var encoder = new SnapshotCodec.Encoder();
        var decoder = new SnapshotCodec.Decoder();
        var near = Ship(2, 100, 0);
        decoder.Decode(encoder.Encode(World(1, [Ship(1, 0, 0), near]), Self, Everywhere));

        var gone = decoder.Decode(encoder.Encode(World(2, [Ship(1, 0, 0)]), Self, Everywhere));
        Assert.DoesNotContain(gone.Ships, s => s.Id == 2);

        var back = decoder.Decode(encoder.Encode(World(3, [Ship(1, 0, 0), near]), Self, Everywhere));
        AssertSame(near, back.Ships.Single(s => s.Id == 2));
        Assert.Equal(0, decoder.Desyncs);
    }

    [Fact]
    public void Radar_CutsFarEntities_ButKeepsTheOwnShip()
    {
        var encoder = new SnapshotCodec.Encoder();
        var decoder = new SnapshotCodec.Decoder();
        var loot = new[] { new LootDto(10, 50, 0, "metal", 1, 100), new LootDto(11, 5000, 0, "ore", 1, 100) };
        var snapshot = decoder.Decode(encoder.Encode(
            World(1, [Ship(1, 9000, 0), Ship(2, 100, 0), Ship(3, 6000, 0)], loot),
            Self,
            (x, _) => x < 1000));

        Assert.Equal([1, 2], snapshot.Ships.Select(s => s.Id).Order());
        Assert.Equal(10, Assert.Single(snapshot.Loot!).Id);
    }

    [Fact]
    public void Events_OnlyAboutWhatThePlayerSees()
    {
        var encoder = new SnapshotCodec.Encoder();
        var decoder = new SnapshotCodec.Decoder();
        var shots = new[] { new ShotDto(2, 3, "pulse", true, 10, 10, 50), new ShotDto(4, 5, "pulse", false, 0, 0, 50) };
        var kills = new[] { new KillDto(3, 2), new KillDto(5, 4) };
        var snapshot = decoder.Decode(encoder.Encode(
            World(1, [Ship(1, 0, 0), Ship(2, 100, 0), Ship(4, 9000, 0), Ship(5, 9100, 0)], shots: shots, kills: kills),
            Self,
            (x, _) => x < 1000));

        Assert.Equal(2, Assert.Single(snapshot.Shots!).From);
        Assert.Equal(3, Assert.Single(snapshot.Kills!).Id); // цель видимого выстрела — тоже в деле
    }

    [Fact]
    public void Keyframe_ComesRegularly_AndAfterReset()
    {
        var encoder = new SnapshotCodec.Encoder();
        var ships = new[] { Ship(1, 0, 0), Ship(2, 100, 0) };
        var key = encoder.Encode(World(1, ships), Self, Everywhere).Length;
        var delta = encoder.Encode(World(2, ships), Self, Everywhere).Length;
        Assert.True(delta < key);
        Assert.Equal(key, encoder.Encode(World(1 + SnapshotCodec.KeyframeTicks, ships), Self, Everywhere).Length);
        encoder.Reset();
        Assert.Equal(key, encoder.Encode(World(2 + SnapshotCodec.KeyframeTicks, ships), Self, Everywhere).Length);
    }

    [Fact]
    public void DroppedFrames_AreRepairedByAKeyframe()
    {
        var room = new Room(TestBalance.Create(), NullLogger.Instance);
        var a = new FakeConnection(1);
        var b = new FakeConnection(2);
        room.Join(a, null, "A", null);
        room.Join(b, null, "B", null);
        var bId = b.Last<WelcomeMsg>().Id;
        room.Step();

        // У a переполнилась очередь: кадры, где b разгоняется, до него не дошли.
        a.Congested = true;
        for (var seq = 1; seq <= 10; seq++)
        {
            room.Input(b, seq, new MoveInput(1, 0, 1));
            room.Step();
        }
        a.Congested = false;
        room.Step();

        var seen = a.Last<SnapshotMsg>().Ships.Single(s => s.Id == bId);
        var truth = b.Last<SnapshotMsg>().Ships.Single(s => s.Id == bId);
        Assert.Equal(truth.X, seen.X, 2);
        Assert.Equal(truth.Vx, seen.Vx, 2);
        Assert.Equal(0, a.Snapshots.Desyncs);
    }

    [Fact]
    public void RoomTraffic_DeltaIsMuchSmallerThanJson()
    {
        var room = new Room(TestBalance.Create(), NullLogger.Instance);
        var connections = Enumerable.Range(1, 10).Select(i => new FakeConnection(i)).ToList();
        foreach (var c in connections) room.Join(c, null, $"P{c.Id}", null);
        for (var tick = 0; tick < 100; tick++)
        {
            foreach (var c in connections) room.Input(c, tick + 1, new MoveInput(Math.Cos(tick * 0.1 + c.Id), Math.Sin(tick * 0.1 + c.Id), 1));
            room.Step();
        }
        // Тот же снапшот в JSON, как слал сервер до M7.
        var json = Protocol.Encode(connections[0].Last<SnapshotMsg>()).Length;
        var binary = connections[0].FrameBytes / 101.0;
        Assert.True(binary * 2 < json, $"binary {binary:0} B/tick vs JSON {json} B/tick");
    }

    // ── Общий эталон с клиентом ──────────────────────────────────────────────────────────────────────

    private sealed record VectorFrame(string Frame, JsonElement Expected);

    private static readonly JsonSerializerOptions VectorJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string VectorPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", "galaxy.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "shared", "test-vectors", "snapshot.json");
    }

    /// <summary>Кадры: ключевой, дельта с новым кораблём и выстрелом, уход корабля и лута, ключевой после сброса.</summary>
    private static List<byte[]> VectorFrames()
    {
        var encoder = new SnapshotCodec.Encoder();
        bool Radar(double x, double y) => x * x + y * y <= 2000 * 2000;
        var self = Ship(1, 12.345678901234, -420.5) with { R = 1.2345678901, Th = 0.75, Pu = 200 };
        var pirate = Ship(2, 700.25, -1300.75, hp: 300, ai: "patrol") with { W = "plasma", Hull = "heavy" };
        var box = new LootDto(100, 1200, -900, "metal", 4, 0, C: true);
        var drop = new LootDto(101, 650.5, -1250.25, "energy", 1, 2400);
        var rock = new MeteorDto(200, -1500, 300, 120.5, -40.25, "medium", 140);
        var missile = new MissileDto(300, 600.5, -1200.25, 3.5, 2, 1, "missiles");
        var frames = new List<byte[]>
        {
            encoder.Encode(World(100, [self, pirate], [box, drop], [rock], missiles: [missile]), Self, Radar),
        };

        self = self with { X = 20.5, Vx = 55.125, Ack = 8 };
        pirate = pirate with { X = 690, Sh = 20, Ai = "attack", Tg = 1 };
        var newcomer = Ship(3, -300, 900) with { J = 160 };
        rock = rock with { X = -1494, Y = 298, Hp = 70 };
        frames.Add(encoder.Encode(
            World(101, [self, pirate, newcomer], [box, drop], [rock],
                shots: [new ShotDto(2, 1, "plasma", true, 70, 60, 64.5)],
                missiles: [missile with { X = 590, Y = -1180.5, R = 3.25 }]),
            Self, Radar));

        pirate = pirate with { Rt = 400, Hp = 0, Ai = "patrol", Tg = 0 };
        newcomer = newcomer with { X = 5000 }; // ушёл за радар
        frames.Add(encoder.Encode(
            World(102, [self, pirate, newcomer], [box], null,
                kills: [new KillDto(2, 1)],
                picks: [new PickDto(1, 101, "energy", 1)]),
            Self, Radar));

        encoder.Reset();
        frames.Add(encoder.Encode(World(103, [self, pirate], [box], null), Self, Radar));
        return frames;
    }

    /// <summary>Снапшот со всеми полями: клиенту в эталоне не нужно угадывать, какие поля опущены.</summary>
    private static object Full(SnapshotMsg s) => new
    {
        tick = s.Tick,
        ships = s.Ships.OrderBy(x => x.Id).Select(x => new
        {
            id = x.Id, x = x.X, y = x.Y, r = x.R, vx = x.Vx, vy = x.Vy, hull = x.Hull, th = x.Th, ack = x.Ack, hp = x.Hp,
            sh = x.Sh, w = x.W, rt = x.Rt, pu = x.Pu, tg = x.Tg, ai = x.Ai, j = x.J, sl = x.Sl,
        }),
        loot = (s.Loot ?? []).OrderBy(x => x.Id).Select(x => new { id = x.Id, x = x.X, y = x.Y, i = x.I, n = x.N, e = x.E, c = x.C }),
        meteors = (s.Meteors ?? []).OrderBy(x => x.Id).Select(x => new { id = x.Id, x = x.X, y = x.Y, vx = x.Vx, vy = x.Vy, s = x.S, hp = x.Hp }),
        missiles = (s.Missiles ?? []).OrderBy(x => x.Id).Select(x => new { id = x.Id, x = x.X, y = x.Y, r = x.R, o = x.O, t = x.T, w = x.W }),
        shots = s.Shots,
        kills = s.Kills,
        picks = s.Picks,
    };

    [Fact]
    public void SnapshotFramesMatchSharedVectors()
    {
        var frames = VectorFrames();
        var decoder = new SnapshotCodec.Decoder();
        var generated = frames.Select(f => new VectorFrame(
            Convert.ToBase64String(f),
            JsonSerializer.SerializeToElement(Full(decoder.Decode(f)), VectorJson))).ToList();
        Assert.Equal(0, decoder.Desyncs);

        var path = VectorPath();
        if (Environment.GetEnvironmentVariable("SRO_UPDATE_VECTORS") == "1")
        {
            var lines = generated.Select(v => "    " + JsonSerializer.Serialize(v, VectorJson));
            File.WriteAllText(path, "{\n  \"frames\": [\n" + string.Join(",\n", lines) + "\n  ]\n}\n");
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing: run 'SRO_UPDATE_VECTORS=1 dotnet test'");
        using var stored = JsonDocument.Parse(File.ReadAllText(path));
        var storedFrames = stored.RootElement.GetProperty("frames").EnumerateArray().ToList();
        Assert.Equal(generated.Count, storedFrames.Count);
        for (var i = 0; i < generated.Count; i++)
        {
            Assert.Equal(generated[i].Frame, storedFrames[i].GetProperty("frame").GetString());
            Assert.Equal(generated[i].Expected.GetRawText(), storedFrames[i].GetProperty("expected").GetRawText());
        }
    }
}
