// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

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
}
