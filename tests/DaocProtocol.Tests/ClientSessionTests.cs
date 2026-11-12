// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;

namespace DaocProtocol.Tests;

/// <summary>
/// The whole client flow over a real socket against <see cref="FakeServer"/>, which answers with
/// the server packets of the recorded 1.127e session (see Fixtures/README.md).
/// </summary>
public class ClientSessionTests
{
    private static readonly List<ServerPacket> Recorded = CaptureFile.Login1127e().ServerPackets();

    // Recorded server order: 22 2A 29 28 FE 28 FC.
    private static readonly ServerPacket CryptKey = Recorded[0];
    private static readonly ServerPacket Granted = Recorded[1];
    private static readonly ServerPacket Ping = Recorded[2];
    private static readonly ServerPacket Session = Recorded[3];
    private static readonly ServerPacket RealmNone = Recorded[4];
    private static readonly ServerPacket Overview = Recorded[6];

    /// <summary>Answers like the recorded server; <paramref name="extra"/> overrides by code.</summary>
    private static Func<ClientPacket, ServerPacket[]?> Recording(Func<ClientPacket, ServerPacket[]?>? extra = null) => p =>
        extra?.Invoke(p) ?? p.Code switch
        {
            CryptKeyRequest.Code => [CryptKey],
            LoginRequest.Code => [Granted, Ping], // the ping reply is unprompted noise to skip
            CharacterSelectRequest.Code => [Session],
            CharacterOverviewRequest.Code when p.Payload[0] == 0 => [RealmNone],
            CharacterOverviewRequest.Code => [Overview],
            _ => [],
        };

    private static async Task<(DaocConnection, ClientSession)> Connect(FakeServer server)
    {
        var connection = new DaocConnection();
        await connection.ConnectAsync("127.0.0.1", server.Port);
        return (connection, new ClientSession(connection) { ReplyTimeout = TimeSpan.FromSeconds(5) });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // every reply byte in its own write: the receive loop must reassemble
    public async Task LogsInAndListsCharacters(bool dribble)
    {
        await using var server = new FakeServer(Recording(), dribble);
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            LoginResult login = await session.LoginAsync("rustdaoc", "secret");
            Assert.True(login.Success);
            Assert.Equal(SessionState.LoggedIn, session.State);

            Assert.Equal(Realm.None, await session.OpenCharacterScreenAsync());
            Assert.Equal(1, connection.SessionId);

            CharacterOverview overview = await session.GetCharactersAsync(Realm.Albion);
            Assert.Equal("Lilillyn", Assert.Single(overview.Characters).Name);
        }

        Assert.Null(server.Error);
        Assert.Equal(new byte[] { 0xF4, 0xA7, 0x10, 0xFC, 0xFC }, server.Received.Select(p => p.Code));
        Assert.Equal(new ushort[] { 0, 1, 2, 3, 4 }, server.Received.Select(p => p.Sequence));
        // The session id goes into every header after 0x28 arrives.
        Assert.Equal(new ushort[] { 0, 0, 0, 1, 1 }, server.Received.Select(p => p.SessionId));
    }

    [Fact]
    public async Task ReportsADeniedLogin()
    {
        await using var server = new FakeServer(Recording(p =>
            p.Code == LoginRequest.Code ? [new ServerPacket(LoginDenied.Code, [0x01, 0x01, 0x01, 0x1b, 0x00])] : null));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            LoginResult login = await session.LoginAsync("rustdaoc", "wrong");
            Assert.False(login.Success);
            Assert.Equal(LoginError.IncorrectPassword, login.Error);
            Assert.Equal(SessionState.Connected, session.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenCharacterScreenAsync());
        }
    }

    [Fact]
    public async Task TimesOutWhenTheServerIsSilent()
    {
        await using var server = new FakeServer(_ => []);
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            session.ReplyTimeout = TimeSpan.FromMilliseconds(200);
            await Assert.ThrowsAsync<TimeoutException>(() => session.LoginAsync("rustdaoc", "secret"));
        }
    }

    [Fact]
    public async Task HandshakeAloneThenLoginSendsOneVersionPacket()
    {
        int announced = 0;
        await using var server = new FakeServer(Recording(p =>
        {
            if (p.Code == CryptKeyRequest.Code)
                announced++;
            return null;
        }));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            CryptKeyReply reply = await session.HandshakeAsync();
            Assert.Equal("1.127", reply.Version);
            Assert.True((await session.LoginAsync("rustdaoc", "secret")).Success);
            Assert.Equal(1, announced);
        }
    }

    [Fact]
    public async Task HandshakeFailsWhenTheServerRefusesTheVersion()
    {
        await using var server = new FakeServer(_ => null);
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await Assert.ThrowsAsync<IOException>(() => session.HandshakeAsync());
        }
    }

    [Fact]
    public async Task FailsWhenTheServerHangsUp()
    {
        await using var server = new FakeServer(_ => null);
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await Assert.ThrowsAsync<IOException>(() => session.LoginAsync("rustdaoc", "secret"));
        }
    }

    [Fact]
    public async Task CreatesAndDeletesThenListsAgain()
    {
        // The server answers both with 0x2A; the fresh 0xFC listing is the confirmation.
        await using var server = new FakeServer(Recording(p => p.Code == CharacterCreateRequest.Code ? [Granted] : null));
        var (connection, session) = await Connect(server);
        var character = new NewCharacter("Testarms", Realm.Albion, 1, ClassId: 2, Race: 1, Female: false, Model: 32,
            new CharacterStats(70, 70, 70, 60, 60, 60, 60, 60));
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);

            Assert.NotNull(await session.CreateCharacterAsync(character));
            await session.DeleteCharacterAsync(Realm.Albion, slot: 0);
        }

        Assert.Null(server.Error);
        ClientPacket[] changes = [.. server.Received.Where(p => p.Code == CharacterCreateRequest.Code)];
        Assert.Equal(CharacterCreateRequest.CreatePayload(character), changes[0].Payload);
        Assert.Equal(CharacterCreateRequest.DeletePayload(Realm.Albion, 0), changes[1].Payload);
        Assert.Equal(new byte[] { 0xFF, 0xFC, 0xFF, 0xFC }, server.Received.Skip(5).Select(p => p.Code));
    }

    [Fact]
    public async Task RefusesToDeleteAnEmptySlot()
    {
        await using var server = new FakeServer(Recording());
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => session.DeleteCharacterAsync(Realm.Albion, 0)); // not listed yet
            await session.GetCharactersAsync(Realm.Albion);
            await Assert.ThrowsAsync<ArgumentException>(() => session.DeleteCharacterAsync(Realm.Albion, 5));
        }

        Assert.DoesNotContain(server.Received, p => p.Code == CharacterCreateRequest.Code);
    }

    /// <summary>A 0x20 as PacketLib1124 writes it, for a world entry somewhere the recording never went.</summary>
    private static ServerPacket PositionPacket(float x, float y, float z, ushort region, ushort heading = 0) =>
        new(PositionAndObjectId.Code, new PayloadWriter().F32LE(x).F32LE(y).F32LE(z).U16BE(0x4167).U16BE(heading)
            .Zeros(4).U16BE(region).U8(0x80).Zeros(5).ToArray());

    /// <summary>Logs in and enters the world against <paramref name="server"/>.</summary>
    private static async Task<(DaocConnection, ClientSession)> InWorld(FakeServer server)
    {
        var (connection, session) = await Connect(server);
        await session.LoginAsync("rustdaoc", "secret");
        await session.OpenCharacterScreenAsync();
        await session.GetCharactersAsync(Realm.Albion);
        await session.EnterWorldAsync(Realm.Albion, slot: 0);
        return (connection, session);
    }

    private static bool IsJump(ClientPacket p) =>
        p.Code == CommandRequest.Code && System.Text.Encoding.Latin1.GetString(p.Payload).Contains("&jump");

    [Fact]
    public async Task JumpWithinTheRegionTakesOverTheServersPosition()
    {
        // Salisbury Plains (zone 1) is in Camelot Hills' region 1: the server answers with 0x04.
        await using var server = new FakeServer(Recording(p => IsJump(p)
            ? [new ServerPacket(CharacterJump.Code, new CharacterJump(560000, 570000, 0x4167, 1000, 2048, 0).ToBytes())]
            : WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            WorldPosition world = await session.JumpAsync(1, 560000, 570000, 1000);

            Assert.Equal(SessionState.InWorld, session.State);
            Assert.Equal((560000f, 570000f, 1000f), (world.Position.X, world.Position.Y, world.Position.Z));
            Assert.Equal("Salisbury Plains", world.Zone.Name);
            Assert.Equal(world, session.World);

            // The next 0xA9 reports the new position in the new zone.
            await session.MoveToAsync(560001, 570000, 1000, heading: 2048);
        }

        await server.ClosedAsync();   // the last 0xA9 may still be in flight
        Assert.Null(server.Error);
        ClientPacket command = Assert.Single(server.Received, IsJump);
        Assert.Equal(CommandRequest.Payload("&jump to 560000 570000 1000 1"), command.Payload);
        Assert.Equal(PositionUpdate.Payload(560001, 570000, 1000, sessionId: 1, objectId: 0, zoneId: 1, heading: 0x1000 | 2048, actionFlags: 0x80),
            server.Received.Last(p => p.Code == PositionUpdate.Code).Payload);
    }

    [Fact]
    public async Task JumpToAnotherRegionEntersTheWorldThere()
    {
        // Cursed Forest (zone 208, region 200): 0xB1 + 0xB7, then the client's 0xD4 brings the new
        // world's burst, and 0xE8 + 0xBF finish the entry as at login.
        bool moved = false;
        await using var server = new FakeServer(Recording(p =>
        {
            if (IsJump(p))
            {
                moved = true;
                return [Recorded[7], new ServerPacket(RegionChanged.Code, new RegionChanged(200, 208).ToBytes())];
            }
            if (p.Code == WorldInitRequest.Code && moved)
                return [PositionPacket(455000, 488000, 3000, region: 200, heading: 100), new ServerPacket(WorldInitRequest.EndOfBurstCode, [0])];
            return WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null);
        }));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            WorldPosition world = await session.JumpAsync(200, 455000, 488000, 3000);

            Assert.Equal(SessionState.InWorld, session.State);
            Assert.Equal((455000f, 488000f, (ushort)200), (world.Position.X, world.Position.Y, world.Position.RegionId));
            Assert.Equal("Cursed Forest", world.Zone.Name);
        }

        Assert.Null(server.Error);
        int jump = server.Received.FindIndex(p => IsJump(p));
        Assert.Equal(new byte[] { 0xD4, 0xE8, 0xBF },
            server.Received.Skip(jump + 1).Where(p => p.Code != PositionUpdate.Code && p.Code != PingRequest.Code).Select(p => p.Code));
        // The re-entry names the same character as the first one.
        Assert.Equal(WorldInitRequest.Payload(Realm.Albion, 0), server.Received.Skip(jump).First(p => p.Code == WorldInitRequest.Code).Payload);
    }

    [Fact]
    public async Task ChangeRegionAfterAnUnpromptedRegionChangeEntersTheNewWorld()
    {
        // A region change the session didn't ask for (a portal, a recall): the world drains the 0xB7 and
        // calls ChangeRegionAsync, which re-enters from 0xD4 and takes the new world's position.
        int worldInits = 0;
        await using var server = new FakeServer(Recording(p =>
        {
            if (p.Code == WorldInitRequest.Code && ++worldInits == 2)
                return [PositionPacket(455000, 488000, 3000, region: 200, heading: 100), new ServerPacket(WorldInitRequest.EndOfBurstCode, [0])];
            return WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null);
        }));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            int before = server.Received.Count;
            WorldPosition world = await session.ChangeRegionAsync();

            Assert.Equal(SessionState.InWorld, session.State);
            Assert.Equal(world, session.World);
            Assert.Equal((455000f, 488000f, (ushort)200, (ushort)100),
                (world.Position.X, world.Position.Y, world.Position.RegionId, world.Position.Heading));
            Assert.Equal("Cursed Forest", world.Zone.Name);
            Assert.Equal(new byte[] { 0xD4, 0xE8, 0xBF },
                server.Received.Skip(before).Where(p => p.Code is not (PositionUpdate.Code or PingRequest.Code)).Select(p => p.Code));
        }

        Assert.Null(server.Error);
        Assert.Equal(2, worldInits);
    }

    [Fact]
    public async Task ChangeRegionNeedsTheWorld()
    {
        await using var server = new FakeServer(Recording());
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ChangeRegionAsync());
            Assert.Equal(SessionState.CharacterScreen, session.State);
        }
    }

    [Fact]
    public async Task HoldsPositionUpdatesWhileTheServerMovesUs()
    {
        // The server takes 400 ms to answer the jump; a heartbeat in between would report the old
        // position, which the server would believe. Only after the 0x04 may 0xA9 go out again.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long jumpAt = -1;
        bool answered = false;
        await using var server = new FakeServer(Recording(p =>
        {
            if (IsJump(p))
            {
                jumpAt = clock.ElapsedMilliseconds;
                return [];
            }
            if (p.Code == PingRequest.Code && jumpAt >= 0 && !answered && clock.ElapsedMilliseconds - jumpAt >= 400)
            {
                answered = true;
                return [new ServerPacket(CharacterJump.Code, new CharacterJump(560000, 570000, 0x4167, 1000, 0, 0).ToBytes())];
            }
            return WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null);
        }));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            session.PositionInterval = TimeSpan.FromMilliseconds(50);
            session.PingInterval = TimeSpan.FromMilliseconds(50);
            using var stop = new CancellationTokenSource();
            Task keepAlive = session.KeepAliveAsync(stop.Token);
            await Task.Delay(200);
            await session.JumpAsync(1, 560000, 570000, 1000);
            await Task.Delay(200);
            stop.Cancel();
            await keepAlive;
        }

        Assert.Null(server.Error);
        Assert.True(answered);
        int jump = server.Received.FindIndex(p => IsJump(p));
        List<ClientPacket> after = server.Received.Skip(jump + 1).ToList();
        int firstUpdate = after.FindIndex(p => p.Code == PositionUpdate.Code);
        Assert.True(firstUpdate >= 0, "position updates resume after the jump");
        // Pings kept going while we waited (the server answered on one of them)...
        Assert.True(after.Take(firstUpdate).Count(p => p.Code == PingRequest.Code) >= 4);
        // ...and every 0xA9 after the command carries the new position, none the old one.
        Assert.All(after.Where(p => p.Code == PositionUpdate.Code),
            u => Assert.Equal(PositionUpdate.Payload(560000, 570000, 1000, sessionId: 1, objectId: 0, zoneId: 1, heading: 0x1000 | 0, actionFlags: 0x80), u.Payload));
    }

    [Fact]
    public async Task ARefusedJumpLeavesUsWhereWeWere()
    {
        // A player without GM rights gets a chat line, not a move.
        await using var server = new FakeServer(Recording(p => IsJump(p)
            ? [new ServerPacket(ChatMessage.Code, [0, 0, 0, 0, 0, 0, 0, 0])]
            : WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            WorldPosition before = session.World!;
            var e = await Assert.ThrowsAsync<TimeoutException>(() => session.JumpAsync(1, 560000, 570000, 1000));
            Assert.Contains("GM", e.Message);
            Assert.Equal(SessionState.InWorld, session.State);
            Assert.Equal(before, session.World);
        }
    }

    [Fact]
    public async Task ACancelledJumpLeavesUsWhereWeWere()
    {
        // Not just a timeout: any end to the wait must leave Relocating, or 0xA9 stays held back.
        await using var server = new FakeServer(Recording(p => IsJump(p)
            ? []
            : WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.JumpAsync(1, 560000, 570000, 1000, cancel.Token));
            Assert.Equal(SessionState.InWorld, session.State);
        }
    }

    /// <summary>Recorded world entry: 9D → B1 + 29, D4 → the burst from C5 to 4E (packets 10–36), BF → 2D (packet 237).</summary>
    private static ServerPacket[]? WorldEntry(ClientPacket p) => p.Code switch
    {
        RegionListRequest.Code => [Recorded[7], Recorded[8]],
        WorldInitRequest.Code => [.. Recorded[10..37]],
        GameOpenRequest.Code => [Recorded[237]],
        PingRequest.Code => [Ping],
        _ => null,
    };

    [Fact]
    public async Task EntersTheWorldAndKeepsTheSessionAlive()
    {
        await using var server = new FakeServer(Recording(p => WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);

            WorldPosition world = await session.EnterWorldAsync(Realm.Albion, slot: 0);
            Assert.Equal(SessionState.InWorld, session.State);
            Assert.Equal((560871f, 511685f, 2344f), (world.Position.X, world.Position.Y, world.Position.Z));
            Assert.Equal("Camelot Hills", world.Zone.Name);

            session.PositionInterval = TimeSpan.FromMilliseconds(50);
            session.PingInterval = TimeSpan.FromMilliseconds(200);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            await session.KeepAliveAsync(stop.Token);
        }

        Assert.Null(server.Error);
        Assert.Equal(new byte[] { 0x9D, 0x10, 0xD4, 0xE8, 0xBF }, server.Received.Skip(5).Take(5).Select(p => p.Code));

        byte[] expected = PositionUpdate.Payload(560871f, 511685f, 2344f, sessionId: 1, objectId: 0, zoneId: 0, heading: 0x1000 | 1512, actionFlags: 0x80);
        List<ClientPacket> updates = server.Received.Where(p => p.Code == PositionUpdate.Code).ToList();
        Assert.InRange(updates.Count, 5, 20);
        Assert.All(updates, u => Assert.Equal(expected, u.Payload));
        Assert.InRange(server.Received.Count(p => p.Code == PingRequest.Code), 2, 6);
    }

    [Fact]
    public async Task TheWorldPacketsPassedOverDuringEntryAreKeptForTheWorldView()
    {
        // The recorded entry burst (0xD4 -> packets 10-36) holds more than the 0x20 and 0x4E the session
        // waits for: object creates and state the world view must see. Until 2026-11-03 they were
        // dropped, so NPCs announced during entry never appeared as themselves.
        await using var server = new FakeServer(Recording(p => WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);
            Assert.Empty(session.TakeSkipped());   // nothing kept before the world

            await session.EnterWorldAsync(Realm.Albion, slot: 0);
            byte[] expected = Recorded[10..37].Select(p => p.Code)
                .Where(c => c is not PositionAndObjectId.Code and not WorldInitRequest.EndOfBurstCode).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, session.TakeSkipped().Where(p => p.Code != PingRequest.ReplyCode).Select(p => p.Code).ToArray());
            Assert.Empty(session.TakeSkipped());   // handed over once
        }
    }

    [Fact]
    public async Task MoveToSendsOneUpdateAtTheNewPositionAndAdvancesWorld()
    {
        await using var server = new FakeServer(Recording(p => WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);
            await session.EnterWorldAsync(Realm.Albion, slot: 0);

            // One step east: new X/Y/Z and heading, with a non-zero speed.
            await session.MoveToAsync(561000f, 511700f, 2344f, heading: 2048, speed: 191f);

            Assert.Equal((561000f, 511700f), (session.World!.Position.X, session.World.Position.Y));
            Assert.Equal((ushort)2048, session.World.Position.Heading);
            Assert.Equal(191f, session.MoveSpeed);
            // Zone, object id and region are preserved across the move.
            Assert.Equal("Camelot Hills", session.World.Zone.Name);
        }

        Assert.Null(server.Error);
        List<ClientPacket> updates = server.Received.Where(p => p.Code == PositionUpdate.Code).ToList();
        Assert.Single(updates); // no keep-alive ran; MoveToAsync sent exactly one 0xA9
        byte[] expected = PositionUpdate.Payload(561000f, 511700f, 2344f, sessionId: 1, objectId: 0, zoneId: 0, heading: 0x1000 | 2048, speed: 191f, actionFlags: 0x80);
        Assert.Equal(expected, updates[0].Payload);
    }

    [Fact]
    public async Task TheKeepAliveRepeatsASittingOrSwimmingPostureButNotAJumpOrAStrafe()
    {
        // Others read a posture from every 0xA9: a keep-alive without the sit bit would stand us up every 2 s.
        await using var server = new FakeServer(Recording(p => WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);
            await session.EnterWorldAsync(Realm.Albion, slot: 0);
            session.PositionInterval = TimeSpan.FromMilliseconds(50);
            session.PingInterval = TimeSpan.FromMilliseconds(200);

            async Task Hold(byte sent)
            {
                await session.MoveToAsync(561000f, 511700f, 2344f, heading: 2048, speed: 0, verticalSpeed: 0, landed: false, stateFlags: sent);
                using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await session.KeepAliveAsync(stop.Token);
            }
            await Hold(0x10 | 0x40);   // sitting (and a strafe bit, which isn't repeated)
            int sat = server.Received.Count;
            await Hold(0x08);          // a jump: the keep-alive says normal
            int jumped = server.Received.Count;
            await Hold(0x04);          // swimming
            List<byte> States(IEnumerable<ClientPacket> ps) => ps.Where(p => p.Code == PositionUpdate.Code).Skip(1).Select(p => p.Payload[26]).ToList();
            Assert.Equal([0x10], States(server.Received.Take(sat)).Distinct());
            Assert.Equal([0x00], States(server.Received.Take(jumped).Skip(sat)).Distinct());
            Assert.Equal([0x04], States(server.Received.Skip(jumped)).Distinct());
        }
        Assert.Null(server.Error);
    }

    [Fact]
    public async Task TheTargetGoesInEveryPositionUpdateAndInteractTargetsFirst()
    {
        await using var server = new FakeServer(Recording(p => WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code
            or TargetRequest.Code or InteractRequest.Code ? [] : null)));
        PositionAndObjectId spawn = default!;
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);
            await session.EnterWorldAsync(Realm.Albion, slot: 0);
            spawn = session.World!.Position;

            await session.InteractAsync(0x1234);          // not the target yet: 0xB0 first, then 0x7A
            await session.MoveToAsync(561000f, 511700f, 2344f, heading: 2048);
            await session.SetTargetAsync(0);              // cleared
            await session.MoveToAsync(561001f, 511700f, 2344f, heading: 2048);
        }

        await server.ClosedAsync();
        Assert.Null(server.Error);
        var sent = server.Received.Where(p => p.Code is TargetRequest.Code or InteractRequest.Code or PositionUpdate.Code).ToList();
        Assert.Equal(new byte[] { TargetRequest.Code, InteractRequest.Code, PositionUpdate.Code, TargetRequest.Code, PositionUpdate.Code },
            sent.Select(p => p.Code).ToArray());
        Assert.Equal(new byte[] { 0x12, 0x34, 0xE0, 0x00 }, sent[0].Payload);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, sent[3].Payload);
        // 0x7A: whole-unit x, y (BE) where the player stands, the session, the object.
        Assert.Equal(InteractRequest.Payload(spawn.X, spawn.Y, 1, 0x1234), sent[1].Payload);
        Assert.Equal(new byte[] { 0, 1, 0x12, 0x34 }, sent[1].Payload[8..]);
        Assert.Equal(PositionUpdate.Payload(561000f, 511700f, 2344f, 1, objectId: 0x1234, zoneId: 0, heading: 0x1000 | 2048, actionFlags: 0xB0), sent[2].Payload);
        Assert.Equal(PositionUpdate.Payload(561001f, 511700f, 2344f, 1, objectId: 0, zoneId: 0, heading: 0x1000 | 2048, actionFlags: 0x80), sent[4].Payload);
    }

    [Fact]
    public async Task WhileDeadEachPositionUpdateSaysSoAndReleaseIsACommand()
    {
        await using var server = new FakeServer(Recording(p => WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code
            or CommandRequest.Code ? [] : null)));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);
            await session.EnterWorldAsync(Realm.Albion, slot: 0);

            session.HealthPercent = 40;
            await session.MoveToAsync(561000f, 511700f, 2344f, heading: 2048, speed: 191f);   // alive, hurt
            session.IsDead = true;
            await session.MoveToAsync(561000f, 511700f, 2344f, heading: 2048, speed: 191f, verticalSpeed: -300f, landed: true, stateFlags: 0x08);
            await session.ReleaseAsync();
            session.IsDead = false;
            session.HealthPercent = 100;
            await session.MoveToAsync(561000f, 511700f, 2344f, heading: 2048);
        }

        await server.ClosedAsync();
        Assert.Null(server.Error);
        var sent = server.Received.Where(p => p.Code is PositionUpdate.Code or CommandRequest.Code).ToList();
        Assert.Equal(new byte[] { PositionUpdate.Code, PositionUpdate.Code, CommandRequest.Code, PositionUpdate.Code },
            sent.Select(p => p.Code).ToArray());
        Assert.Equal(PositionUpdate.Payload(561000f, 511700f, 2344f, 1, objectId: 0, zoneId: 0, heading: 0x1000 | 2048, speed: 191f,
            actionFlags: 0x80, health: 40), sent[0].Payload);
        // Dead: the dead pose (0x14), standing, no speed, no fall, health 0, whatever the caller passed.
        Assert.Equal(PositionUpdate.Payload(561000f, 511700f, 2344f, 1, objectId: 0, zoneId: 0, heading: 0x1000 | 2048,
            stateFlags: 0x14, actionFlags: 0x80, health: 0), sent[1].Payload);
        Assert.Equal(CommandRequest.Payload("&release"), sent[2].Payload);
        Assert.Equal(PositionUpdate.Payload(561000f, 511700f, 2344f, 1, objectId: 0, zoneId: 0, heading: 0x1000 | 2048, actionFlags: 0x80),
            sent[3].Payload);
    }

    [Fact]
    public async Task WalkingOverAZoneBorderReportsTheNewZoneAndAGapKeepsTheLast()
    {
        await using var server = new FakeServer(Recording(p => WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);
            await session.EnterWorldAsync(Realm.Albion, slot: 0);
            Assert.Equal("Camelot Hills", session.World!.Zone.Name);

            // Camelot Hills ends at y 67 × 8192 = 548864; Salisbury Plains (zone 1) starts there.
            await session.MoveToAsync(570000f, 548863f, 2344f, heading: 0);
            Assert.Equal("Camelot Hills", session.World.Zone.Name);
            await session.MoveToAsync(570000f, 548864f, 2344f, heading: 0);
            Assert.Equal("Salisbury Plains", session.World.Zone.Name);
            // West of both, south of Black Mtns. South: no zone. The last one stays.
            await session.MoveToAsync(548000f, 549000f, 2344f, heading: 0);
            Assert.Equal("Salisbury Plains", session.World.Zone.Name);
        }

        Assert.Null(server.Error);
        ushort[] zones = server.Received.Where(p => p.Code == PositionUpdate.Code)
            .Select(p => (ushort)(p.Payload[24] << 8 | p.Payload[25])).ToArray();
        Assert.Equal(new ushort[] { 0, 1, 1 }, zones);
    }

    [Fact]
    public async Task MoveToRejectsOutsideTheWorld()
    {
        await using var server = new FakeServer(Recording());
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.MoveToAsync(1f, 2f, 3f, heading: 0));
        }
    }

    [Fact]
    public async Task QuitsWhileTheHeartbeatContinues()
    {
        // The server answers "&quit" only when its timer runs out: here, with the first
        // position update at least 300 ms after the command.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long quitAt = -1;
        bool answered = false;
        await using var server = new FakeServer(Recording(p =>
        {
            if (p.Code == CommandRequest.Code)
            {
                quitAt = clock.ElapsedMilliseconds;
                return [];
            }
            if (p.Code == PositionUpdate.Code && quitAt >= 0 && !answered && clock.ElapsedMilliseconds - quitAt >= 300)
            {
                answered = true;
                return [new ServerPacket(QuitReply.Code, [0x00, 0x01])];
            }
            return WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null);
        }));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);
            await session.EnterWorldAsync(Realm.Albion, slot: 0);

            session.PositionInterval = TimeSpan.FromMilliseconds(50);
            using var stop = new CancellationTokenSource();
            Task keepAlive = session.KeepAliveAsync(stop.Token);
            CharacterOverview? back = await session.QuitAsync();
            stop.Cancel();
            await keepAlive;
            Assert.Equal(SessionState.CharacterScreen, session.State);
            Assert.Equal("Lilillyn", Assert.Single(back!.Characters).Name);
        }

        Assert.Null(server.Error);
        Assert.True(answered);
        int quit = server.Received.FindIndex(p => p.Code == CommandRequest.Code);
        Assert.Equal(CommandRequest.Payload("&quit"), server.Received[quit].Payload);
        // Position updates kept coming while the server's quit timer ran...
        Assert.True(server.Received.Skip(quit).Count(p => p.Code == PositionUpdate.Code) >= 3);
        // ...and after 0xA4 the client asked for the realm's characters (back to the character screen).
        ClientPacket listing = server.Received.Skip(quit).First(p => p.Code == CharacterOverviewRequest.Code);
        Assert.Equal(CharacterOverviewRequest.Payload(Realm.Albion), listing.Payload);
    }

    [Fact]
    public async Task AQuitThatLeavesTheGameEndsTheSession()
    {
        // 0xA4 with 1 first: leave the game, not the character screen. Nothing is listed afterwards.
        await using var server = new FakeServer(Recording(p =>
            p.Code == CommandRequest.Code ? [new ServerPacket(QuitReply.Code, [0x01, 0x01])]
            : WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            int before = server.Received.Count;
            Assert.Null(await session.QuitAsync());
            Assert.Equal(SessionState.LoggedOut, session.State);
            Assert.Null(session.World);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendCommandAsync("/who"));
            Assert.Equal(new[] { CommandRequest.Code }, server.Received.Skip(before).Select(p => p.Code).Where(c => c != PositionUpdate.Code));
        }

        Assert.Null(server.Error);
    }

    [Theory]
    [InlineData("/who", "&who")]
    [InlineData("&who", "&who")]
    [InlineData("who", "&who")]
    [InlineData("  /say hello there", "&say hello there")]
    public async Task CommandsGoOutInTheServersAmpersandForm(string typed, string wire)
    {
        await using var server = new FakeServer(Recording(p =>
            p.Code == CommandRequest.Code ? []
            : WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            await session.SendCommandAsync(typed);
            await session.SendCommandAsync("   ");                // nothing to send
            await session.SayAsync("hi");
            await server.WaitForAsync(p => p.Code == CommandRequest.Code, count: 2);
        }

        Assert.Null(server.Error);
        Assert.Equal([CommandRequest.Payload(wire), CommandRequest.Payload("&say hi")],
            server.Received.Where(p => p.Code == CommandRequest.Code).Select(p => p.Payload).ToArray());
    }

    [Fact]
    public async Task AQuitReplyDrainedElsewhereFinishesTheQuit()
    {
        // A /quit typed as text: the world drained the 0xA4, and hands it over. The session lists the
        // realm (the server's state moves to the character screen) and is back at the character screen.
        await using var server = new FakeServer(Recording(p =>
            WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            CharacterOverview? back = await session.FinishQuitAsync(new ServerPacket(QuitReply.Code, [0x00, 0x01]));
            Assert.Equal(SessionState.CharacterScreen, session.State);
            Assert.Null(session.World);
            Assert.NotNull(back);
        }
    }

    [Fact]
    public async Task StandingUpCallsAQuitOff()
    {
        // The player moves during the quit timer: the client stands up (0xC7 00), which stops the
        // server's timer, and stops waiting for 0xA4. The character stays in the world, and what
        // arrived meanwhile (here the server's chat line) is kept for the world to show.
        await using var server = new FakeServer(Recording(p =>
            p.Code == CommandRequest.Code ? [new ServerPacket(ChatMessage.Code, [0, 0, 0, 0, 0, 0, 0, 0])]
            : p.Code == SitRequest.Code ? []
            : WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            using var cancel = new CancellationTokenSource();
            Task<CharacterOverview?> quit = session.QuitAsync(cancel.Token);
            await Task.Delay(200);
            await session.SitAsync(false);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quit);

            Assert.Equal(SessionState.InWorld, session.State);
            Assert.NotNull(session.World);
            Assert.Contains(session.TakeSkipped(), p => p.Code == ChatMessage.Code);
            // The server reads the stand-up on its own task: wait for it (asserting at once failed under a full parallel run).
            await server.WaitForAsync(p => p.Code == SitRequest.Code);
        }

        ClientPacket stand = Assert.Single(server.Received, p => p.Code == SitRequest.Code);
        Assert.Equal([0x00], stand.Payload);
    }

    [Fact]
    public async Task KeepAlivePingsFromTheCharacterScreenAndFollowsIntoTheWorld()
    {
        await using var server = new FakeServer(Recording(p => WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.KeepAliveAsync(CancellationToken.None));
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();

            session.PositionInterval = TimeSpan.FromMilliseconds(50);
            session.PingInterval = TimeSpan.FromMilliseconds(100);
            using var stop = new CancellationTokenSource();
            Task keepAlive = session.KeepAliveAsync(stop.Token);

            await Task.Delay(300); // character screen: pings only
            int beforeWorld = server.Received.Count;
            await session.GetCharactersAsync(Realm.Albion); // ping replies arriving meanwhile are skipped
            await session.EnterWorldAsync(Realm.Albion, slot: 0);
            await Task.Delay(300); // world: positions and pings
            stop.Cancel();
            await keepAlive;

            List<ClientPacket> early = server.Received.Take(beforeWorld).ToList();
            Assert.True(early.Count(p => p.Code == PingRequest.Code) >= 2);
            Assert.DoesNotContain(early, p => p.Code == PositionUpdate.Code);
            Assert.True(server.Received.Skip(beforeWorld).Count(p => p.Code == PositionUpdate.Code) >= 3);
        }

        Assert.Null(server.Error);
    }

    [Fact]
    public async Task KeepAliveSendsPositionsOnlyWhenNothingElseHas()
    {
        // Retail rates: 0xA9 every ~2 s standing still, but none extra while movement updates go out;
        // pings keep their own rate whatever the position interval.
        await using var server = new FakeServer(Recording(p => WorldEntry(p) ?? (p.Code is PlayerInitRequest.Code or PositionUpdate.Code ? [] : null)));
        var (connection, session) = await InWorld(server);
        await using (connection)
        {
            session.PositionInterval = TimeSpan.FromMilliseconds(300);
            session.PingInterval = TimeSpan.FromMilliseconds(100);
            using var stop = new CancellationTokenSource();
            int start = server.Received.Count;
            Task keepAlive = session.KeepAliveAsync(stop.Token);

            // Moving: an update every 50 ms for 600 ms, each at a new X.
            PositionAndObjectId p = session.World!.Position;
            for (int i = 1; i <= 12; i++)
            {
                await session.MoveToAsync(p.X + i, p.Y, p.Z, p.Heading, speed: 191);
                await Task.Delay(50);
            }
            int moved = server.Received.Count;
            await Task.Delay(1000); // standing still
            stop.Cancel();
            await keepAlive;

            List<ClientPacket> moving = server.Received.Skip(start).Take(moved - start).ToList();
            List<ClientPacket> idle = server.Received.Skip(moved).ToList();
            byte[] lastMove = PositionUpdate.Payload(p.X + 12, p.Y, p.Z, sessionId: 1, objectId: 0, session.World.Zone.Id, (ushort)(0x1000 | p.Heading), speed: 191, actionFlags: 0x80);
            // While moving, at most the keep-alive's first send (at start) besides our 12.
            Assert.InRange(moving.Count(u => u.Code == PositionUpdate.Code), 12, 13);
            Assert.True(moving.Count(u => u.Code == PingRequest.Code) >= 4);
            // Standing still: about one per 300 ms, at the last position.
            Assert.InRange(idle.Count(u => u.Code == PositionUpdate.Code), 2, 4);
            Assert.All(idle.Where(u => u.Code == PositionUpdate.Code), u => Assert.Equal(lastMove, u.Payload));
            Assert.True(idle.Count(u => u.Code == PingRequest.Code) >= 7);
        }

        Assert.Null(server.Error);
    }

    [Fact]
    public async Task EnterWorldNeedsTheRealmListedLast()
    {
        await using var server = new FakeServer(Recording());
        var (connection, session) = await Connect(server);
        await using (connection)
        {
            await session.LoginAsync("rustdaoc", "secret");
            await session.OpenCharacterScreenAsync();
            await session.GetCharactersAsync(Realm.Albion);
            await Assert.ThrowsAsync<ArgumentException>(() => session.EnterWorldAsync(Realm.Albion, slot: 4));   // empty
            await Assert.ThrowsAsync<ArgumentException>(() => session.EnterWorldAsync(Realm.Midgard, slot: 0));  // not listed
        }

        Assert.DoesNotContain(server.Received, p => p.Code == RegionListRequest.Code);
    }

    [Fact]
    public async Task PollingSeesEveryPacket()
    {
        // The Godot client drains the queue once per frame instead of awaiting.
        await using var server = new FakeServer(Recording());
        var (connection, _) = await Connect(server);
        await using (connection)
        {
            await connection.SendAsync(LoginRequest.Code, LoginRequest.Payload("rustdaoc", "secret"));
            var codes = new List<byte>();
            for (int frame = 0; frame < 500 && codes.Count < 2; frame++)
            {
                while (connection.TryReceive(out ServerPacket packet))
                    codes.Add(packet.Code);
                await Task.Delay(10);
            }

            Assert.Equal(new byte[] { LoginGranted.Code, 0x29 }, codes);
        }
    }

    [Fact]
    public void PacketLogNeverHexLogsTheLogin()
    {
        var lines = new List<string>();
        var log = new PacketLog(lines.Add, hex: true);
        log.Sent(new ClientPacket(0, 0, 0, LoginRequest.Code, LoginRequest.Payload("rustdaoc", "secret")));
        log.Sent(new ClientPacket(1, 0, 0, CryptKeyRequest.Code, CryptKeyRequest.Payload(ClientVersion.V1127e)));

        Assert.Equal("C→S [A7] 36 bytes", lines[0]);
        Assert.DoesNotContain(lines, l => l.Contains(Convert.ToHexString("secret"u8)));
        Assert.StartsWith("C→S [F4] 0007", lines[1]);
    }

    [Fact]
    public void ASwitchedOffPacketLogWritesNothingUntilSwitchedOn()
    {
        var lines = new List<string>();
        var log = new PacketLog(lines.Add) { Enabled = false };
        var ping = new ClientPacket(0, 0, 0, CryptKeyRequest.Code, CryptKeyRequest.Payload(ClientVersion.V1127e));
        log.Sent(ping);
        Assert.Empty(lines);
        log.Enabled = true;
        log.Sent(ping);
        Assert.Equal(["C→S [F4] 19 bytes"], lines);
    }

    [Theory]
    [InlineData(0x18, "riding")]
    [InlineData(0x0C, "flying")]
    [InlineData(0x14, "dead")]
    [InlineData(0x98, "riding")]    // a strafe bit doesn't hide it
    [InlineData(0x00, null)]
    [InlineData(0x08, null)]        // a jump: what our client sends in the air
    [InlineData(0x04, null)]
    [InlineData(0x10, null)]
    [InlineData(0x1C, null)]        // climbing: all three, allowed
    public void StateFlagPairsTheServerTakesAsForgedAreNamed(byte flags, string? forged) =>
        Assert.Equal(forged, ClientSession.ForbiddenStateFlags(flags));

    [Fact]
    public void TheHeadingWordMarksBeingOnTheGround()
    {
        Assert.Equal(0x1800, PositionUpdate.Heading(0x0800, onGround: true));
        Assert.Equal(0x0800, PositionUpdate.Heading(0x0800, onGround: false));
        Assert.Equal(0x1FFF, PositionUpdate.Heading(0x1FFF, onGround: true));   // a stray high bit doesn't carry over
        Assert.Equal(0x0FFF, PositionUpdate.Heading(0xFFFF, onGround: false));
    }
}
