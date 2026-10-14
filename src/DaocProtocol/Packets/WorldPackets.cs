// SPDX-License-Identifier: GPL-3.0-or-later
//
// Client 1.127 world entry and keep-alive. Layouts follow OpenDAoC-Core 2026.09.36 (f0aaa1e):
// RegionListRequestHandler, WorldInitRequestHandler, PlayerInitRequestHandler,
// GameOpenRequestHandler, PingRequestHandler, PlayerPositionUpdateHandler (1.124+ branch),
// PacketLib1124.SendPlayerPositionAndObjectID, PacketLib168.SendPingReply. Checked against the
// recorded 1.127e session in the tests.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>Slot numbering shared by 0x9D, 0xD4 and 0xFF: Albion 0–9, Midgard 10–19, Hibernia 20–29.</summary>
public static class CharacterSlot
{
    public static byte ToWire(Realm realm, int slot)
    {
        if (realm == Realm.None)
            throw new ArgumentException("A slot belongs to a realm.", nameof(realm));
        if (slot is < 0 or >= CharacterOverview.SlotsPerRealm)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot must be 0–9.");
        return (byte)(((byte)realm - 1) * CharacterOverview.SlotsPerRealm + slot);
    }
}

/// <summary>
/// 0x9D: asks for the region list for the character in a slot; the server answers 0xB1.
/// The server reads only the slot byte. The recorded client sends 28 more bytes we can't
/// interpret yet (open question L6); we send the slot alone.
/// </summary>
public static class RegionListRequest
{
    public const byte Code = 0x9D;
    public const byte ReplyCode = 0xB1;

    public static byte[] Payload(Realm realm, int slot) => [CharacterSlot.ToWire(realm, slot)];
}

/// <summary>
/// 0xD4: enter the world with the character in a slot. The server loads it (converting the slot
/// with the account's current realm), then sends a burst that includes 0x20 and ends with 0x4E.
/// </summary>
public static class WorldInitRequest
{
    public const byte Code = 0xD4;

    /// <summary>0x4E ControlledHorse, the last packet of the world-init burst.</summary>
    public const byte EndOfBurstCode = 0x4E;

    public static byte[] Payload(Realm realm, int slot) => [CharacterSlot.ToWire(realm, slot)];
}

/// <summary>0xE8: the server ignores the payload; the recorded client sends <c>01</c>. Answered with 0x2B among others.</summary>
public static class PlayerInitRequest
{
    public const byte Code = 0xE8;

    public static byte[] Payload() => [0x01];
}

/// <summary>
/// 0xBF: the client is ready; the server switches to "playing" and answers 0x2D. Flag 1 would
/// confirm a working UDP channel; we have none, so 0 (as the recorded client sends).
/// </summary>
public static class GameOpenRequest
{
    public const byte Code = 0xBF;
    public const byte ReplyCode = 0x2D;

    public static byte[] Payload() => [0x00];
}

/// <summary>
/// 0xA3 keep-alive. The server disconnects a client after 150 s without one
/// (ClientService.HARD_TIMEOUT) and answers each with 0x29 echoing the timestamp.
/// </summary>
public static class PingRequest
{
    public const byte Code = 0xA3;
    public const byte ReplyCode = 0x29;

    /// <summary>4 bytes the server skips, the timestamp (u32 big-endian), 4 zero bytes.</summary>
    public static byte[] Payload(uint timestamp) =>
        new PayloadWriter().Zeros(4).U32BE(timestamp).Zeros(4).ToArray();

    /// <summary>The timestamp echoed by 0x29.</summary>
    public static uint ParseReply(ReadOnlySpan<byte> payload) => new PayloadReader(payload).U32BE();
}

/// <summary>Where the server put us: 0x20, sent during world init.</summary>
/// <param name="X">Region-global coordinate.</param>
/// <param name="Heading">0–4095 = 0–360°.</param>
/// <param name="RegionId">The region's "skin": its ID, except for instances.</param>
/// <param name="Flags">0x80 = diving enabled, 0x01 = underwater.</param>
public sealed record PositionAndObjectId(
    float X, float Y, float Z, ushort ObjectId, ushort Heading, ushort RegionId, byte Flags)
{
    public const byte Code = 0x20;

    public static PositionAndObjectId Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        float x = r.F32LE(), y = r.F32LE(), z = r.F32LE();
        ushort objectId = r.U16BE();
        ushort heading = r.U16BE();
        r.Skip(4); // dungeon zone offsets (x, y) / 8192; 0 outside dungeons
        ushort region = r.U16BE();
        byte flags = r.U8();
        return new PositionAndObjectId(x, y, z, objectId, heading, region, flags);
    }
}

/// <summary>
/// 0xA9: the player's position. The 1.127 client sends it even when standing still: the server
/// marks a player link-dead after 5 s without one (PlayerMovementComponent.SOFT_LINK_DEATH_THRESHOLD).
/// </summary>
public static class PositionUpdate
{
    public const byte Code = 0xA9;

    /// <param name="sessionId">Read and skipped by the server.</param>
    /// <param name="objectId">Read and skipped by the server (1.127+); the recorded client sends 0.</param>
    /// <param name="zoneId">Must be a zone the server knows, or the player is kicked to the character screen.</param>
    /// <param name="heading">0–4095; the recorded client also sets bit 0x1000 at times (meaning unknown).</param>
    /// <param name="actionFlags">Bit 0x80 = torch; the recorded client sends 0x80.</param>
    /// <param name="verticalSpeed">Non-zero while falling or jumping (the server's FallSpeed).</param>
    /// <param name="health">Percent; the server skips it.</param>
    public static byte[] Payload(float x, float y, float z, ushort sessionId, ushort objectId, ushort zoneId,
        ushort heading, float speed = 0, float verticalSpeed = 0, byte stateFlags = 0, byte actionFlags = 0, byte health = 100) =>
        new PayloadWriter()
            .F32LE(x).F32LE(y).F32LE(z)
            .F32LE(speed)
            .F32LE(verticalSpeed)
            .U16BE(sessionId)
            .U16BE(objectId)
            .U16BE(zoneId)
            .U8(stateFlags)
            .U8(0)           // unknown
            .U16BE(0)        // falling damage
            .U16BE(heading)
            .U8(actionFlags)
            .Zeros(2)        // unknown
            .U8(health)
            .Zeros(4)        // trailing
            .ToArray();
}
