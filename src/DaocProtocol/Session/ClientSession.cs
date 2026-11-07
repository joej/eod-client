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
    /// <summary>
    /// The server is moving the character (a /jump we asked for, or a region change). 0xA9 is held
    /// back until the new position is known, so the server isn't sent the old one; pings go on.
    /// </summary>
    Relocating,
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
    private (Realm Realm, int Slot) _inWorldAs;
    private readonly List<ServerPacket> _skipped = [];
    private bool _enteringWorld;
    private const int MaxSkipped = 10_000;

    /// <summary>
    /// The world packets a session method passed over while it waited for its reply (world entry, a
    /// jump, a region change): NPC and player creates, positions, chat. The world view processes these
    /// before its own reads, or it would never see the NPCs announced during entry. Clears the list.
    /// </summary>
    public IReadOnlyList<ServerPacket> TakeSkipped()
    {
        lock (_skipped)
        {
            ServerPacket[] taken = [.. _skipped];
            _skipped.Clear();
            return taken;
        }
    }
    // Taken while checking the state and sending a 0xA9, and while entering Relocating and sending
    // the move: otherwise a heartbeat that saw "in world" could send the old position just after
    // the /jump, and the server, handling packets in order, would move us back.
    private readonly SemaphoreSlim _positionLock = new(1, 1);
    private CryptKeyReply? _handshake;

    /// <summary>How long to wait for each reply before throwing <see cref="TimeoutException"/>.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public SessionState State { get; private set; } = SessionState.Connected;

    /// <summary>
    /// The player's current position, set by <see cref="EnterWorldAsync"/>, advanced by
    /// <see cref="MoveToAsync"/>, and replaced when the server moves us (<see cref="ApplyJump"/>,
    /// <see cref="ChangeRegionAsync"/>, <see cref="JumpAsync"/>). The keep-alive reports it in every 0xA9.
    /// </summary>
    public WorldPosition? World { get; private set; }

    /// <summary>
    /// The speed reported in 0xA9. 0 = standing still; the keep-alive and <see cref="MoveToAsync"/>
    /// send it. The server trusts the client's X/Y/Z, so this is informational (animation/anti-cheat
    /// on retail), not the thing that actually moves the player.
    /// </summary>
    public float MoveSpeed { get; set; }

    /// <summary>The object the player has selected (0: none), as last sent in 0xB0; it goes in every 0xA9.</summary>
    public ushort TargetId { get; private set; }

    /// <summary>
    /// Set by the front end when the server says we died (0xAE with our id) and cleared when it revives us
    /// (0x89). While set, every 0xA9 says so as retail 1.127's does: state 0x14 (the dead pose; OpenDAoC
    /// accepts it only at 0 health), speed 0, health 0. The character can't move until revived.
    /// </summary>
    public bool IsDead { get; set; }

    /// <summary>Our health percent from the last 0xAD, sent in each 0xA9 (the server skips it).</summary>
    public byte HealthPercent { get; set; } = 100;

    /// <summary>0xA9's state byte while dead: sitting and swimming together (OpenDAoC StateFlags.DEAD).</summary>
    public const byte DeadStateFlags = 0x14;

    /// <summary>
    /// The longest <see cref="KeepAliveAsync"/> lets pass without an 0xA9: it sends one when nothing
    /// else (<see cref="MoveToAsync"/>) has for this long. Retail 1.127 sends one every 2.08 s standing
    /// still; the server marks a player link-dead after 5 s without one.
    /// </summary>
    public TimeSpan PositionInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often <see cref="KeepAliveAsync"/> sends 0xA3: every 5 s, as retail 1.127 does. The server disconnects after 150 s without one.</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(5);

    // Environment.TickCount64 of the last 0xA9 sent, by the keep-alive or MoveToAsync.
    private long _lastPositionSent = long.MinValue / 2;

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
        if (realm != _lastListedRealm || !_overviews.TryGetValue(realm, out CharacterOverview? listed)
            || !listed.Characters.Any(c => c.Slot == slot))
            throw new ArgumentException($"Slot {slot} in {realm} is empty, or {realm} wasn't the realm listed last.", nameof(slot));

        await connection.SendAsync(RegionListRequest.Code, RegionListRequest.Payload(realm, slot), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(cancellationToken, RegionListRequest.ReplyCode).ConfigureAwait(false);

        await connection.SendAsync(CharacterSelectRequest.Code, CharacterSelectRequest.Payload(), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(cancellationToken, SessionId.Code).ConfigureAwait(false);

        _inWorldAs = (realm, slot);
        return await InitWorldAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The in-world part of entering, shared with <see cref="ChangeRegionAsync"/>: 0xD4 → burst
    /// with 0x20 … 0x4E, then 0xE8 and 0xBF → 0x2D. 0xD4 puts the server's connection in its
    /// "world enter" state and only 0xBF takes it back to "playing" (clearing its cache of the
    /// objects we've been sent, so the new surroundings arrive).
    /// </summary>
    private async Task<WorldPosition> InitWorldAsync(CancellationToken cancellationToken)
    {
        _enteringWorld = true;
        try { return await InitWorldCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _enteringWorld = false; }
    }

    private async Task<WorldPosition> InitWorldCoreAsync(CancellationToken cancellationToken)
    {
        // A new world entry (login or a region change): what was skipped before belongs to the old one.
        lock (_skipped) _skipped.Clear();
        (Realm realm, int slot) = _inWorldAs;
        await connection.SendAsync(WorldInitRequest.Code, WorldInitRequest.Payload(realm, slot), cancellationToken).ConfigureAwait(false);
        PositionAndObjectId position = PositionAndObjectId.Parse((await ExpectAsync(cancellationToken, PositionAndObjectId.Code).ConfigureAwait(false)).Payload);
        await ExpectAsync(cancellationToken, WorldInitRequest.EndOfBurstCode).ConfigureAwait(false);

        await connection.SendAsync(PlayerInitRequest.Code, PlayerInitRequest.Payload(), cancellationToken).ConfigureAwait(false);
        await connection.SendAsync(GameOpenRequest.Code, GameOpenRequest.Payload(), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(cancellationToken, GameOpenRequest.ReplyCode).ConfigureAwait(false);

        World = new WorldPosition(position, ZoneAt(position));
        State = SessionState.InWorld;
        return World;
    }

    // The zone a walk has reached: the one containing the position, else (a gap between zones, the
    // world's edge) the last one, which the server knows.
    private static Zone ZoneOrLast(PositionAndObjectId p, Zone last) =>
        last.Contains(p.X, p.Y) ? last : Zones.Find(p.RegionId, p.X, p.Y) ?? last;

    // Position updates must name a zone the server knows, or it kicks the player.
    private static Zone ZoneAt(PositionAndObjectId p) =>
        Zones.Find(p.RegionId, p.X, p.Y)
        ?? throw new InvalidDataException($"No zone in region {p.RegionId} contains ({p.X}, {p.Y}).");

    /// <summary>
    /// Takes over a position the server set with 0x04 (a move within the region: /jump, a GM moving
    /// us, a recall nearby). Call it as soon as the packet arrives: until then the keep-alive keeps
    /// reporting the old position, which the server would believe. Returns the new position.
    /// </summary>
    public WorldPosition ApplyJump(CharacterJump jump)
    {
        if (State is not (SessionState.InWorld or SessionState.Relocating) || World is not WorldPosition world)
            throw new InvalidOperationException($"Session is {State}; a jump needs the world.");

        PositionAndObjectId p = jump.HeadingOnly
            ? world.Position with { Heading = jump.Heading }
            : world.Position with { X = jump.X, Y = jump.Y, Z = jump.Z, Heading = jump.Heading };
        World = new WorldPosition(p, ZoneAt(p));
        State = SessionState.InWorld;
        return World;
    }

    /// <summary>
    /// Follows the server into another region after its 0xB7: enters the world again (as
    /// <see cref="EnterWorldAsync"/> does from 0xD4 on) and returns the new position. Nothing else may
    /// read packets meanwhile: this method must see the 0x20 and 0x4E. The keep-alive holds back
    /// 0xA9 until it returns (the player isn't in any region in between).
    /// </summary>
    public async Task<WorldPosition> ChangeRegionAsync(CancellationToken cancellationToken = default)
    {
        await _positionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not (SessionState.InWorld or SessionState.Relocating))
                throw new InvalidOperationException($"Session is {State}; a region change needs the world.");
            State = SessionState.Relocating;
        }
        finally
        {
            _positionLock.Release();
        }
        return await InitWorldAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the server to move the player (OpenDAoC's GM command "/jump to X Y Z REGION", so the
    /// account must be a GM) and follows it: 0x04 within the region, 0xB7 and a new world entry for
    /// another one. Nothing else may read packets meanwhile. A server that refuses (not a GM, or no
    /// zone at that spot) sends a chat message instead, and this times out.
    /// </summary>
    public async Task<WorldPosition> JumpAsync(ushort regionId, float x, float y, float z, CancellationToken cancellationToken = default)
    {
        await _positionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Require(SessionState.InWorld);
            State = SessionState.Relocating;
            await connection.SendAsync(CommandRequest.Code,
                CommandRequest.Payload($"&jump to {(int)x} {(int)y} {(int)z} {regionId}"), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (State == SessionState.Relocating)
                State = SessionState.InWorld;
            throw;
        }
        finally
        {
            _positionLock.Release();
        }

        ServerPacket reply;
        try
        {
            // Shorter than the server's 5 s link-death limit: 0xA9 is held back while we wait.
            reply = await ExpectAsync(cancellationToken, JumpTimeout, CharacterJump.Code, RegionChanged.Code).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Nothing moved: carry on where we were, whatever ended the wait (a timeout, cancellation,
            // a lost connection). Left Relocating, the keep-alive would hold back 0xA9 for good.
            State = SessionState.InWorld;
            if (e is TimeoutException)
                throw new TimeoutException($"The server didn't move us within {JumpTimeout.TotalSeconds:0} s (is the account a GM?).");
            throw;
        }

        return reply.Code == CharacterJump.Code
            ? ApplyJump(CharacterJump.Parse(reply.Payload))
            : await InitWorldAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How long <see cref="JumpAsync"/> waits for the server's move.</summary>
    public static readonly TimeSpan JumpTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Moves the player to a new region-global position and heading, then sends one 0xA9 so the
    /// server sees it at once (the keep-alive would otherwise hold it for up to 2 s).
    /// Call this from the input/physics loop while the player moves; the server trusts the X/Y/Z
    /// it is given, so updating them is the movement. <paramref name="heading"/> is 0–4095
    /// (4096 = 360°). Walking over a zone border moves the session into the zone that now contains the
    /// position (the server reads 0xA9's zone field for "You have entered …" and the zone's areas);
    /// in a gap between zones it keeps the last one.
    /// </summary>
    /// <summary>
    /// The name of the forged state <paramref name="stateFlags"/> spells, or null. OpenDAoC reads two of
    /// sitting (0x10), jumping (0x08) and swimming (0x04) together as riding (0x18) while not mounted,
    /// flying (0x0C) or playing dead (0x14); the first two ban the account and its IP when BAN_HACKERS is on,
    /// with no GM exemption (PlayerPositionUpdateHandler.ProcessStateFlags). All three is climbing, allowed.
    /// So a jump while swimming or sitting must not send both bits.
    /// </summary>
    public static string? ForbiddenStateFlags(byte stateFlags) => (stateFlags & 0x1C) switch
    {
        0x18 => "riding",
        0x0C => "flying",
        0x14 => "dead",
        _ => null,
    };

    public async Task MoveToAsync(float x, float y, float z, ushort heading, float speed = 0,
        CancellationToken cancellationToken = default) =>
        await MoveToAsync(x, y, z, heading, speed, verticalSpeed: 0, landed: false, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// As <see cref="MoveToAsync(float, float, float, ushort, float, CancellationToken)"/>, with the vertical
    /// speed in region units per second (negative falling: OpenDAoC's FallSpeed) and, on the one update that
    /// lands from a fall, <paramref name="landed"/>: the server then charges fall damage from that speed.
    /// <paramref name="stateFlags"/> is 0xA9's state byte, which other players see (retail: 0x08 in the
    /// air, 0x04 swimming, 0x10 sitting). Throws <see cref="ArgumentException"/> for a pair of those that
    /// OpenDAoC takes as a forged packet (<see cref="ForbiddenStateFlags"/>).
    /// </summary>
    public async Task MoveToAsync(float x, float y, float z, ushort heading, float speed, float verticalSpeed, bool landed,
        byte stateFlags = 0, CancellationToken cancellationToken = default)
    {
        if (ForbiddenStateFlags(stateFlags) is { } forged)
            throw new ArgumentException($"State flags 0x{stateFlags:X2} read as {forged} on the server, which bans for it.", nameof(stateFlags));
        await _positionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // While the server moves us, our idea of the position is stale: drop the step.
            if (State == SessionState.Relocating)
                return;
            if (State != SessionState.InWorld || World is not WorldPosition world)
                throw new InvalidOperationException($"Session is {State}; MoveToAsync needs the world.");

            MoveSpeed = speed;
            PositionAndObjectId moved = world.Position with { X = x, Y = y, Z = z, Heading = heading };
            World = new WorldPosition(moved, ZoneOrLast(moved, world.Zone));
            await SendPositionAsync(World, speed, cancellationToken, verticalSpeed, landed, stateFlags).ConfigureAwait(false);
        }
        finally
        {
            _positionLock.Release();
        }
    }

    /// <summary>Sends one 0xA9 for the given position at the given speed. Shared by the keep-alive and <see cref="MoveToAsync"/>.</summary>
    private Task SendPositionAsync(WorldPosition world, float speed, CancellationToken cancellationToken,
        float verticalSpeed = 0, bool landed = false, byte stateFlags = 0)
    {
        PositionAndObjectId p = world.Position;
        _lastPositionSent = Environment.TickCount64;
        // On the ground: not in a jump and not falling, or the update that lands.
        bool onGround = landed || (verticalSpeed == 0 && (stateFlags & 0x08) == 0);
        if (IsDead)
            (speed, verticalSpeed, stateFlags, landed, onGround) = (0, 0, DeadStateFlags, false, true);
        return connection.SendAsync(PositionUpdate.Code,
            PositionUpdate.Payload(p.X, p.Y, p.Z, connection.SessionId, objectId: TargetId, world.Zone.Id,
                PositionUpdate.Heading(p.Heading, onGround), speed,
                verticalSpeed, stateFlags,
                actionFlags: (byte)(PositionUpdate.ActionAlways | (TargetId != 0 ? PositionUpdate.TargetInView : 0)),
                health: IsDead ? (byte)0 : HealthPercent,
                landed: landed),
            cancellationToken);
    }

    /// <summary>
    /// Keeps the session alive until cancelled: 0xA3 every <see cref="PingInterval"/> from the
    /// character screen on (OpenDAoC's 150 s ping timeout applies there too), plus, in the world,
    /// an 0xA9 at the current position whenever none has gone out for <see cref="PositionInterval"/>
    /// (so while <see cref="MoveToAsync"/> is sending, this adds none). Start it once after
    /// <see cref="OpenCharacterScreenAsync"/>; it follows the session into and out of the world.
    /// It only sends: replies are skipped by the session's own waits, and in the world the caller
    /// drains incoming packets.
    /// </summary>
    public async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        if (State is not (SessionState.CharacterScreen or SessionState.InWorld))
            throw new InvalidOperationException($"Session is {State}; keep-alive needs the character screen or the world.");

        long nextPing = Environment.TickCount64;
        try
        {
            // Relocating counts as staying: pings go on, 0xA9 waits for the new position.
            while (State is SessionState.CharacterScreen or SessionState.InWorld or SessionState.Relocating)
            {
                long positionInterval = (long)PositionInterval.TotalMilliseconds;
                await _positionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (State == SessionState.InWorld && World is WorldPosition world
                        && Environment.TickCount64 - _lastPositionSent >= positionInterval)
                        await SendPositionAsync(world, MoveSpeed, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _positionLock.Release();
                }

                long now = Environment.TickCount64;
                if (now >= nextPing)
                {
                    await connection.SendAsync(PingRequest.Code, PingRequest.Payload((uint)Environment.TickCount), cancellationToken).ConfigureAwait(false);
                    nextPing = now + (long)PingInterval.TotalMilliseconds;
                }

                // Wake for whichever is due first: the next 0xA9 counts from the last one sent (a timer
                // that fires a tick early then retries at once). Outside the world none is due, but wake
                // every position interval so entering the world is noticed.
                long nextPosition = (State == SessionState.InWorld ? _lastPositionSent : now) + positionInterval;
                long due = Math.Min(nextPing, nextPosition);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, due - Environment.TickCount64)), cancellationToken)
                    .ConfigureAwait(false);
            }
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
    /// ended the game instead. The server sits the character down for the timer; to call the quit
    /// off, stand up with <see cref="SitAsync"/> and cancel this wait: the session stays in the world,
    /// and the packets read meanwhile wait in <see cref="TakeSkipped"/>.
    /// </summary>
    public async Task<CharacterOverview?> QuitAsync(CancellationToken cancellationToken = default)
    {
        Require(SessionState.InWorld);
        await connection.SendAsync(CommandRequest.Code, CommandRequest.Payload("&quit"), cancellationToken).ConfigureAwait(false);
        ServerPacket reply = await ExpectAsync(cancellationToken, QuitTimeout, QuitReply.Code).ConfigureAwait(false);
        return await FinishQuitAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes a quit from its 0xA4: as <see cref="QuitAsync"/> does once the reply is in. Call it for
    /// a 0xA4 drained elsewhere (a quit this session didn't start, e.g. a command sent as text): the
    /// server has already removed the character, and until the realm is listed it still counts the
    /// connection as playing. Returns the fresh listing, or null if the server ended the game.
    /// </summary>
    public async Task<CharacterOverview?> FinishQuitAsync(ServerPacket reply, CancellationToken cancellationToken = default)
    {
        if (reply.Code != QuitReply.Code)
            throw new ArgumentException($"0x{reply.Code:X2} is not a quit reply.", nameof(reply));
        World = null;
        if (QuitReply.LeavesGame(reply.Payload))
        {
            State = SessionState.LoggedOut;
            return null;
        }

        State = SessionState.CharacterScreen;
        return await GetCharactersAsync(_lastListedRealm, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sit down or stand up (0xC7). The server answers with a status update and a chat line (or a
    /// refusal, e.g. while moving), which arrive with the other unprompted packets. Standing up while
    /// <see cref="QuitAsync"/> waits cancels the server's quit timer: cancel that wait too. Moving (an
    /// 0xA9 with a new X/Y) stands the character up on the server by itself; no 0xC7 is needed then.
    /// </summary>
    public Task SitAsync(bool sit, CancellationToken cancellationToken = default)
    {
        Require(SessionState.InWorld);
        return connection.SendAsync(SitRequest.Code, SitRequest.Payload(sit), cancellationToken);
    }

    /// <summary>Longest quit timer (60 s after combat) plus slack.</summary>
    public static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(75);

    /// <summary>
    /// Say something on the local /say channel. Fire-and-forget: the server echoes it back as a
    /// 0xAF Message (and sends it to others nearby), which the world drains and shows in the chat
    /// log — this does not wait for that. Must be in the world.
    /// </summary>
    public Task SayAsync(string text, CancellationToken cancellationToken = default) =>
        SendCommandAsync("say " + text, cancellationToken);

    /// <summary>
    /// Send a slash-command or chat over the 0xAF command channel. Accepts either the player-typed
    /// form with a leading '/' (e.g. "/say hi", "/who") or the bare command ("say hi"); both are
    /// sent as the "&amp;"-prefixed form the server reads (see WorldPackets.CommandRequest). The
    /// server's reply, if any, arrives asynchronously as a 0xAF Message for the chat log.
    /// </summary>
    /// <summary>Asks the server to send an object's create packet (0xBE), for one we get updates for but
    /// never saw created. No reply if it is out of range or gone.</summary>
    public Task RequestObjectAsync(ushort objectId, CancellationToken cancellationToken = default)
    {
        Require(SessionState.InWorld);
        return connection.SendAsync(CreateObjectRequest.Code, CreateObjectRequest.Payload(objectId), cancellationToken);
    }

    /// <summary>Selects <paramref name="objectId"/> (0 clears the target): C->S 0xB0. The server answers in chat.</summary>
    public Task SetTargetAsync(ushort objectId, CancellationToken cancellationToken = default)
    {
        Require(SessionState.InWorld);
        TargetId = objectId;
        return connection.SendAsync(TargetRequest.Code, TargetRequest.Payload(objectId), cancellationToken);
    }

    /// <summary>
    /// Interacts with <paramref name="objectId"/> (a right-click: talk to an NPC, open a merchant), targeting it
    /// first when it isn't the target yet, as retail does. C->S 0xB0 then 0x7A from the player's position.
    /// </summary>
    public async Task InteractAsync(ushort objectId, CancellationToken cancellationToken = default)
    {
        Require(SessionState.InWorld);
        if (World is not WorldPosition world)
            return;
        if (TargetId != objectId)
            await SetTargetAsync(objectId, cancellationToken).ConfigureAwait(false);
        await connection.SendAsync(InteractRequest.Code,
            InteractRequest.Payload(world.Position.X, world.Position.Y, connection.SessionId, objectId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks to use jump point <paramref name="zonePointId"/> (the player walked through its doorway): the
    /// server moves the player, and its 0x04 or 0xB7 arrives like any other move (NetworkBridge).
    /// </summary>
    public Task UseJumpPointAsync(ushort zonePointId, CancellationToken cancellationToken = default)
    {
        Require(SessionState.InWorld);
        return connection.SendAsync(RegionChangeRequest.Code, RegionChangeRequest.Payload(zonePointId), cancellationToken);
    }

    /// <summary>
    /// After death: asks to be released to the bind point (retail sends "&amp;release"). The server answers
    /// that it will release us in a few seconds, then moves us (0x04, or 0xB7 to another region) and revives
    /// us (0x89), both arriving as world packets. <paramref name="to"/> is OpenDAoC's optional target:
    /// "city", "house" or "bind".
    /// </summary>
    public Task ReleaseAsync(string? to = null, CancellationToken cancellationToken = default) =>
        SendCommandAsync(to is null ? "/release" : $"/release {to}", cancellationToken);

    public Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        Require(SessionState.InWorld);
        string trimmed = command.TrimStart();
        if (trimmed.Length == 0)
            return Task.CompletedTask;

        // The player may type '/say'; the wire form is '&say'. A bare word ("say") is also accepted.
        string wire = trimmed[0] switch
        {
            '/' => "&" + trimmed[1..],
            '&' => trimmed,
            _ => "&" + trimmed,
        };
        return connection.SendAsync(CommandRequest.Code, CommandRequest.Payload(wire), cancellationToken);
    }

    /// <summary>
    /// Ask to enter or leave attack mode (0x74). The server answers with its own 0x74 AttackMode, which
    /// arrives with the other unprompted packets: that, not this request, says whether weapons are drawn
    /// (it refuses, e.g., melee with a bow readied, and ends attack mode itself).
    /// </summary>
    public Task AttackAsync(bool start, CancellationToken cancellationToken = default)
    {
        Require(SessionState.InWorld);
        return connection.SendAsync(AttackRequest.Code, AttackRequest.Payload(start), cancellationToken);
    }

    private void Require(SessionState state)
    {
        if (State != state)
            throw new InvalidOperationException($"Session is {State}; this needs {state}.");
    }

    private Task<ServerPacket> ExpectAsync(CancellationToken cancellationToken, params byte[] codes) =>
        ExpectAsync(cancellationToken, ReplyTimeout, codes);

    /// <summary>
    /// Set while a session method waits for its reply. Anything else that reads packets (a game loop
    /// polling <see cref="DaocConnection.TryReceive"/>) must hold off meanwhile, or it takes the reply.
    /// </summary>
    public bool AwaitingReply => Volatile.Read(ref _awaiting) > 0;

    private int _awaiting;

    private async Task<ServerPacket> ExpectAsync(CancellationToken cancellationToken, TimeSpan replyTimeout, params byte[] codes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(replyTimeout);
        Interlocked.Increment(ref _awaiting);
        try
        {
            while (true)
            {
                ServerPacket packet = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (codes.Contains(packet.Code))
                    return packet;
                // Not the reply: kept in the world (NPC creates, positions, chat that arrive during
                // world entry or a move), for whoever handles world packets to take with TakeSkipped.
                if (State is SessionState.InWorld or SessionState.Relocating || _enteringWorld)
                    lock (_skipped) { if (_skipped.Count < MaxSkipped) _skipped.Add(packet); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No {string.Join(" or ", codes.Select(c => $"0x{c:X2}"))} within {replyTimeout.TotalSeconds:0.#} s.");
        }
        finally
        {
            Interlocked.Decrement(ref _awaiting);
        }
    }
}
