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

        Assert.Null(server.Error);
        ClientPacket command = Assert.Single(server.Received, IsJump);
        Assert.Equal(CommandRequest.Payload("&jump to 560000 570000 1000 1"), command.Payload);
        Assert.Equal(PositionUpdate.Payload(560001, 570000, 1000, sessionId: 1, objectId: 0, zoneId: 1, heading: 2048),
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
            u => Assert.Equal(PositionUpdate.Payload(560000, 570000, 1000, sessionId: 1, objectId: 0, zoneId: 1, heading: 0), u.Payload));
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

        byte[] expected = PositionUpdate.Payload(560871f, 511685f, 2344f, sessionId: 1, objectId: 0, zoneId: 0, heading: 1512);
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
        byte[] expected = PositionUpdate.Payload(561000f, 511700f, 2344f, sessionId: 1, objectId: 0, zoneId: 0, heading: 2048, speed: 191f);
        Assert.Equal(expected, updates[0].Payload);
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
}
