// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.World;

namespace DaocProtocol.Session;

public enum SessionState
{
    /// <summary>Connected, nothing sent yet.</summary>
    Connected,
    /// <summary>Login granted; the character screen isn't open yet.</summary>
    LoggedIn,
    /// <summary>Session id assigned; characters can be listed, created and deleted. Keep alive with <see cref="ClientSession.KeepAliveAsync"/> from here on.</summary>
    CharacterScreen,
    /// <summary>A character is in the world.</summary>
    InWorld,
    /// <summary>The server ended the game (0xA4 with "leave the game"); close the connection.</summary>
    LoggedOut,
}

/// <summary>Where a character entered the world.</summary>
public sealed record WorldPosition(PositionAndObjectId Position, Zone Zone);

/// <param name="Granted">Set when the server accepted the login.</param>
/// <param name="Error">Set when it refused.</param>
public sealed record LoginResult(LoginGranted? Granted, LoginError? Error)
{
    public bool Success => Granted is not null;
}

/// <summary>
/// The client side of a 1.127 session, from login to the character screen. Each method sends a
/// request and waits for its reply. Packets that arrive in between and aren't the awaited reply
/// are skipped (the server sends some unprompted, e.g. 0x29 ping replies or a repeated 0x2A).
/// </summary>
public sealed class ClientSession(DaocConnection connection, ClientVersion? version = null)
{
    private readonly ClientVersion _version = version ?? ClientVersion.V1127e;
    private readonly Dictionary<Realm, CharacterOverview> _overviews = [];
    private Realm _lastListedRealm;
    private CryptKeyReply? _handshake;

    /// <summary>How long to wait for each reply before throwing <see cref="TimeoutException"/>.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public SessionState State { get; private set; } = SessionState.Connected;

    /// <summary>Set by <see cref="EnterWorldAsync"/>.</summary>
    public WorldPosition? World { get; private set; }

    /// <summary>How often <see cref="KeepAliveAsync"/> sends 0xA9. The server marks a player link-dead after 5 s without one.</summary>
    public TimeSpan PositionInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often <see cref="KeepAliveAsync"/> sends 0xA3. The server disconnects after 150 s without one.</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The account's realm from the last 0xFE; <see cref="Realm.None"/> = not chosen yet or all realms allowed.</summary>
    public Realm AccountRealm { get; private set; }

    /// <summary>
    /// 0xF4 → 0x22 only: shows whether the server accepts our client version. A server that doesn't
    /// closes the connection without a reply (<see cref="IOException"/>). <see cref="LoginAsync"/>
    /// does this step itself unless it has already been done.
    /// </summary>
    public async Task<CryptKeyReply> HandshakeAsync(CancellationToken cancellationToken = default)
    {
        Require(SessionState.Connected);
        await connection.SendAsync(CryptKeyRequest.Code, CryptKeyRequest.Payload(_version), cancellationToken).ConfigureAwait(false);
        _handshake = CryptKeyReply.Parse((await ExpectAsync(cancellationToken, CryptKeyReply.Code).ConfigureAwait(false)).Payload);
        return _handshake;
    }

    /// <summary>0xF4 → 0x22 (unless <see cref="HandshakeAsync"/> ran), then 0xA7 → 0x2A or 0x2C.</summary>
    public async Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
    {
        Require(SessionState.Connected);
        _handshake ??= await HandshakeAsync(cancellationToken).ConfigureAwait(false);

        await connection.SendAsync(LoginRequest.Code, LoginRequest.Payload(account, password), cancellationToken).ConfigureAwait(false);
        ServerPacket reply = await ExpectAsync(cancellationToken, LoginGranted.Code, LoginDenied.Code).ConfigureAwait(false);
        if (reply.Code == LoginDenied.Code)
            return new LoginResult(null, LoginDenied.Parse(reply.Payload).Error);

        State = SessionState.LoggedIn;
        return new LoginResult(LoginGranted.Parse(reply.Payload), null);
    }

    /// <summary>0x10 → 0x28 (session id), then 0xFC realm 0 → 0xFE. Returns the account's realm.</summary>
    public async Task<Realm> OpenCharacterScreenAsync(CancellationToken cancellationToken = default)
    {
        Require(SessionState.LoggedIn);
        await connection.SendAsync(CharacterSelectRequest.Code, CharacterSelectRequest.Payload(), cancellationToken).ConfigureAwait(false);
        connection.SessionId = SessionId.Parse((await ExpectAsync(cancellationToken, SessionId.Code).ConfigureAwait(false)).Payload);
        State = SessionState.CharacterScreen;

        await connection.SendAsync(CharacterOverviewRequest.Code, CharacterOverviewRequest.Payload(Realm.None), cancellationToken).ConfigureAwait(false);
        AccountRealm = RealmReply.Parse((await ExpectAsync(cancellationToken, RealmReply.Code).ConfigureAwait(false)).Payload);
        return AccountRealm;
    }

    /// <summary>
    /// 0xFC realm → 0xFC overview. The server makes this the account's realm, which a later
    /// delete uses to address its slot.
    /// </summary>
    public async Task<CharacterOverview> GetCharactersAsync(Realm realm, CancellationToken cancellationToken = default)
    {
        Require(SessionState.CharacterScreen);
        if (realm == Realm.None)
            throw new ArgumentException("Pick a realm; Realm.None asks for the account's realm instead.", nameof(realm));

        await connection.SendAsync(CharacterOverviewRequest.Code, CharacterOverviewRequest.Payload(realm), cancellationToken).ConfigureAwait(false);
        CharacterOverview overview = CharacterOverview.Parse((await ExpectAsync(cancellationToken, CharacterOverview.Code).ConfigureAwait(false)).Payload);
        _overviews[realm] = overview;
        _lastListedRealm = realm;
        if (AccountRealm == Realm.None)
            AccountRealm = realm;
        return overview;
    }

    /// <summary>
    /// 0xFF create → 0x2A, then lists the realm again. The server answers a create it rejects
    /// the same way, so check the returned overview for the new character.
    /// </summary>
    public async Task<CharacterOverview> CreateCharacterAsync(NewCharacter character, CancellationToken cancellationToken = default)
    {
        Require(SessionState.CharacterScreen);
        byte[] payload = CharacterCreateRequest.CreatePayload(character);
        await connection.SendAsync(CharacterCreateRequest.Code, payload, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(cancellationToken, LoginGranted.Code).ConfigureAwait(false);
        return await GetCharactersAsync(character.Realm, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 0xFF delete → 0x2A, then lists the realm again. The slot must be occupied in the last
    /// listing of that realm: the server doesn't answer a delete of an empty slot.
    /// </summary>
    public async Task<CharacterOverview> DeleteCharacterAsync(Realm realm, int slot, CancellationToken cancellationToken = default)
    {
        Require(SessionState.CharacterScreen);
        if (!_overviews.TryGetValue(realm, out CharacterOverview? overview) || !overview.Characters.Any(c => c.Slot == slot))
            throw new ArgumentException($"Slot {slot} in {realm} is empty, or {realm} hasn't been listed yet.", nameof(slot));

        await connection.SendAsync(CharacterCreateRequest.Code, CharacterCreateRequest.DeletePayload(realm, slot), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(cancellationToken, LoginGranted.Code).ConfigureAwait(false);
        return await GetCharactersAsync(realm, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enters the world with the character in <paramref name="slot"/>: 0x9D → 0xB1, 0x10 → 0x28,
    /// 0xD4 → burst with 0x20 … 0x4E, then 0xE8 and 0xBF → 0x2D. The realm must be the one listed
    /// last: the server converts the slot with the account's current realm, which that listing set.
    /// </summary>
    public async Task<WorldPosition> EnterWorldAsync(Realm realm, int slot, CancellationToken cancellationToken = default)
    {
        Require(SessionState.CharacterScreen);
        if (realm != _lastListedRealm || !_overviews[realm].Characters.Any(c => c.Slot == slot))
            throw new ArgumentException($"Slot {slot} in {realm} is empty, or {realm} wasn't the realm listed last.", nameof(slot));

        await connection.SendAsync(RegionListRequest.Code, RegionListRequest.Payload(realm, slot), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(cancellationToken, RegionListRequest.ReplyCode).ConfigureAwait(false);

        await connection.SendAsync(CharacterSelectRequest.Code, CharacterSelectRequest.Payload(), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(cancellationToken, SessionId.Code).ConfigureAwait(false);

        await connection.SendAsync(WorldInitRequest.Code, WorldInitRequest.Payload(realm, slot), cancellationToken).ConfigureAwait(false);
        PositionAndObjectId position = PositionAndObjectId.Parse((await ExpectAsync(cancellationToken, PositionAndObjectId.Code).ConfigureAwait(false)).Payload);
        await ExpectAsync(cancellationToken, WorldInitRequest.EndOfBurstCode).ConfigureAwait(false);

        await connection.SendAsync(PlayerInitRequest.Code, PlayerInitRequest.Payload(), cancellationToken).ConfigureAwait(false);
        await connection.SendAsync(GameOpenRequest.Code, GameOpenRequest.Payload(), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(cancellationToken, GameOpenRequest.ReplyCode).ConfigureAwait(false);

        // Position updates must name a zone the server knows, or it kicks the player.
        Zone zone = Zones.Find(position.RegionId, position.X, position.Y)
            ?? throw new InvalidDataException($"No zone in region {position.RegionId} contains ({position.X}, {position.Y}).");
        World = new WorldPosition(position, zone);
        State = SessionState.InWorld;
        return World;
    }

    /// <summary>
    /// Keeps the session alive until cancelled: 0xA3 every <see cref="PingInterval"/> from the
    /// character screen on (OpenDAoC's 150 s ping timeout applies there too), plus 0xA9 (standing
    /// still at the entry position) every <see cref="PositionInterval"/> while in the world. Start
    /// it once after <see cref="OpenCharacterScreenAsync"/>; it follows the session into and out
    /// of the world. It only sends: replies are skipped by the session's own waits, and in the
    /// world the caller drains incoming packets.
    /// </summary>
    public async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        if (State is not (SessionState.CharacterScreen or SessionState.InWorld))
            throw new InvalidOperationException($"Session is {State}; keep-alive needs the character screen or the world.");

        DateTime nextPing = DateTime.UtcNow;
        using var timer = new PeriodicTimer(PositionInterval);
        try
        {
            do
            {
                if (State == SessionState.InWorld && World is WorldPosition world)
                {
                    PositionAndObjectId p = world.Position;
                    await connection.SendAsync(PositionUpdate.Code,
                        PositionUpdate.Payload(p.X, p.Y, p.Z, connection.SessionId, objectId: 0, world.Zone.Id, p.Heading),
                        cancellationToken).ConfigureAwait(false);
                }

                if (DateTime.UtcNow >= nextPing)
                {
                    await connection.SendAsync(PingRequest.Code, PingRequest.Payload((uint)Environment.TickCount), cancellationToken).ConfigureAwait(false);
                    nextPing = DateTime.UtcNow + PingInterval;
                }
            }
            while (State is SessionState.CharacterScreen or SessionState.InWorld
                   && await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Logs out cleanly: sends "&amp;quit" and waits for 0xA4, which OpenDAoC sends when its quit
    /// timer runs out (20 s, up to 60 s after combat; immediate for GMs or with
    /// disable_quit_timer). Then, like a client returning to the character screen, lists the
    /// realm again: that moves the server's connection state from "playing" to the character
    /// screen. Closing the socket while it still says "playing" (even after 0xA4) makes the
    /// server run link death: 60 s during which logins are refused.
    /// Keep <see cref="KeepAliveAsync"/> running until this returns, and stop draining packets
    /// elsewhere: this method must see the 0xA4. Returns the fresh listing, or null if the server
    /// ended the game instead.
    /// </summary>
    public async Task<CharacterOverview?> QuitAsync(CancellationToken cancellationToken = default)
    {
        Require(SessionState.InWorld);
        await connection.SendAsync(CommandRequest.Code, CommandRequest.Payload("&quit"), cancellationToken).ConfigureAwait(false);
        ServerPacket reply = await ExpectAsync(cancellationToken, QuitTimeout, QuitReply.Code).ConfigureAwait(false);
        World = null;
        if (QuitReply.LeavesGame(reply.Payload))
        {
            State = SessionState.LoggedOut;
            return null;
        }

        State = SessionState.CharacterScreen;
        return await GetCharactersAsync(_lastListedRealm, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Longest quit timer (60 s after combat) plus slack.</summary>
    public static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(75);

    private void Require(SessionState state)
    {
        if (State != state)
            throw new InvalidOperationException($"Session is {State}; this needs {state}.");
    }

    private Task<ServerPacket> ExpectAsync(CancellationToken cancellationToken, params byte[] codes) =>
        ExpectAsync(cancellationToken, ReplyTimeout, codes);

    private async Task<ServerPacket> ExpectAsync(CancellationToken cancellationToken, TimeSpan replyTimeout, params byte[] codes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(replyTimeout);
        try
        {
            while (true)
            {
                ServerPacket packet = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (codes.Contains(packet.Code))
                    return packet;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No {string.Join(" or ", codes.Select(c => $"0x{c:X2}"))} within {replyTimeout.TotalSeconds:0.#} s.");
        }
    }
}
