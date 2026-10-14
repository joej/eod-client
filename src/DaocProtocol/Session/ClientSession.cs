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
    /// <summary>Session id assigned; characters can be listed, created and deleted.</summary>
    CharacterScreen,
    /// <summary>A character is in the world; keep the session alive with <see cref="ClientSession.KeepAliveAsync"/>.</summary>
    InWorld,
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

    /// <summary>0xF4 → 0x22, then 0xA7 → 0x2A or 0x2C.</summary>
    public async Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
    {
        Require(SessionState.Connected);
        await connection.SendAsync(CryptKeyRequest.Code, CryptKeyRequest.Payload(_version), cancellationToken).ConfigureAwait(false);
        CryptKeyReply.Parse((await ExpectAsync(cancellationToken, CryptKeyReply.Code).ConfigureAwait(false)).Payload);

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
    /// Sends 0xA9 (standing still at the entry position) every <see cref="PositionInterval"/> and
    /// 0xA3 every <see cref="PingInterval"/> until cancelled. It only sends: the caller keeps
    /// draining incoming packets (<see cref="DaocConnection.TryReceive"/> or ReceiveAsync).
    /// </summary>
    public async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        Require(SessionState.InWorld);
        PositionAndObjectId p = World!.Position;
        byte[] position = PositionUpdate.Payload(p.X, p.Y, p.Z, connection.SessionId, objectId: 0, World.Zone.Id, p.Heading);
        DateTime nextPing = DateTime.UtcNow;
        using var timer = new PeriodicTimer(PositionInterval);
        try
        {
            do
            {
                await connection.SendAsync(PositionUpdate.Code, position, cancellationToken).ConfigureAwait(false);
                if (DateTime.UtcNow >= nextPing)
                {
                    await connection.SendAsync(PingRequest.Code, PingRequest.Payload((uint)Environment.TickCount), cancellationToken).ConfigureAwait(false);
                    nextPing = DateTime.UtcNow + PingInterval;
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Require(SessionState state)
    {
        if (State != state)
            throw new InvalidOperationException($"Session is {State}; this needs {state}.");
    }

    private async Task<ServerPacket> ExpectAsync(CancellationToken cancellationToken, params byte[] codes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReplyTimeout);
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
            throw new TimeoutException($"No {string.Join(" or ", codes.Select(c => $"0x{c:X2}"))} within {ReplyTimeout.TotalSeconds:0.#} s.");
        }
    }
}
