// SPDX-License-Identifier: GPL-3.0-or-later
//
// Incoming server→client packets that describe the OTHER entities in the world: NPCs, mobs,
// other players and world objects appearing, moving and leaving. These are what makes the world
// alive (roadmap M5). Layouts ported (field order only, not code) from OpenDAoC-Core @ master,
// GPL-3.0 — GameServer/packets/Server/PacketLib168.cs (SendObjectUpdate 0xA1, SendObjectRemove
// 0xA2, SendObjectDelete 0xE1), PacketLib171.cs (SendObjectCreate 0xD9), PacketLib1124.cs
// (SendNPCCreate 0xDA, SendPlayerCreate/PlayerCreate172 0x4B). Verified byte-for-byte for 0xA1
// against the RustDAoC 1.127e capture (tests/DaocProtocol.Tests/Fixtures).
//
// Endianness, as the server's PacketOut writes it: WriteShort/WriteInt are big-endian;
// WriteFloatLowEndian is little-endian. So the create packets carry X/Y as BIG-endian u32
// integers, while only the player-position packets (0x20, 0xA9) and 0x4B use little-endian
// float32. Do not conflate them.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// 0xA1 ObjectUpdate: the high-frequency position+movement update for one in-view entity (NPC,
/// mob or player), the bulk of in-world traffic (1143 of 1886 S→C packets in the 1.127e capture).
/// Written by PacketLib168.SendObjectUpdate and never overridden through 1.130, so a 1.127 client
/// receives this 1.68-era 24-byte layout. All shorts are big-endian.
/// </summary>
/// <remarks>
/// Coordinates are ZONE-LOCAL 16-bit offsets (obj.X − zone.XOffset), NOT the region-global floats
/// of 0x20/0xA9. Add the zone's offsets to recover region-global; the zone is identified by
/// <see cref="ZoneSkinId"/> (its low 8 bits here, 9th bit smuggled into <see cref="Flags"/>).
/// </remarks>
/// <param name="Speed">Packed: 0 when stationary; carries Z-velocity sign (bit 15) and bits.</param>
/// <param name="Heading">Low 12 bits = heading 0–4095; top nibble carries Z-speed bits when moving.</param>
/// <param name="XOffsetInZone">Zone-local X.</param>
/// <param name="YOffsetInZone">Zone-local Y.</param>
/// <param name="Z">Absolute Z (a real height, not an offset).</param>
/// <param name="ObjectId">The in-view entity's object id.</param>
/// <param name="TargetId">Attack target oid; 0 = not attacking, 0xFFFF = moving-attacker sentinel.</param>
/// <param name="HealthPercent">0 for non-living objects.</param>
/// <param name="Flags">bits 6–7 realm; 0x01 can't-target, 0x02 hide-name, 0x10 underwater, 0x20 flying.</param>
/// <param name="ZoneSkinId">Low 8 bits of the zone's skin id (9th bit is in <see cref="Flags"/>).</param>
public sealed record ObjectUpdate(
    ushort Speed, ushort Heading, ushort XOffsetInZone, ushort YOffsetInZone, ushort Z,
    ushort ObjectId, ushort TargetId, byte HealthPercent, byte Flags, byte ZoneSkinId)
{
    public const byte Code = 0xA1;

    /// <summary>Heading with the Z-speed bits masked off: 0–4095.</summary>
    public ushort HeadingOnly => (ushort)(Heading & 0x0FFF);

    /// <summary>Realm from the flag byte: 0 none, 1 Albion, 2 Midgard, 3 Hibernia.</summary>
    public int Realm => (Flags >> 6) & 0x3;

    public static ObjectUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        ushort speed = r.U16BE();
        ushort heading = r.U16BE();
        ushort xOffset = r.U16BE();
        r.Skip(2);              // xOffsetInTargetZone (move destination; 0 when not moving to one)
        ushort yOffset = r.U16BE();
        r.Skip(2);              // yOffsetInTargetZone
        ushort z = r.U16BE();
        r.Skip(2);              // zOffsetInTargetZone
        ushort objectId = r.U16BE();
        ushort targetId = r.U16BE();
        byte health = r.U8();
        byte flags = r.U8();
        byte zoneSkin = r.U8();
        // r.U8() targetZoneSkinId — ignored; we don't follow move destinations yet.
        return new ObjectUpdate(speed, heading, xOffset, yOffset, z, objectId, targetId, health, flags, zoneSkin);
    }
}

/// <summary>
/// 0xDA NPCCreate (PacketLib1124.SendNPCCreate): an NPC/mob enters view. Variable length; we read
/// through the trailing strings. X/Y are region-global BIG-endian u32 integers.
/// </summary>
/// <param name="Model">Appearance model id (into the asset library later; a capsule for now).</param>
public sealed record NpcCreate(
    ushort ObjectId, ushort Speed, ushort Heading, ushort Z, uint X, uint Y,
    ushort Model, byte Size, byte Level, byte Flags, string Name, string GuildName)
{
    public const byte Code = 0xDA;

    public ushort HeadingOnly => (ushort)(Heading & 0x0FFF);

    public static NpcCreate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        ushort objectId = r.U16BE();
        ushort speed = r.U16BE();
        ushort heading = r.U16BE();
        ushort z = r.U16BE();
        uint x = r.U32BE();
        uint y = r.U32BE();
        r.Skip(2);              // speedZ (Velocity.Z)
        ushort model = r.U16BE();
        byte size = r.U8();
        byte level = r.U8();
        byte flags = r.U8();
        r.Skip(1);              // maxStickDistance (hardcoded 0x20)
        r.Skip(1);              // flags2 (pet/stealth/quest)
        r.Skip(1);              // flags3 (quest indicators)
        r.Skip(2);              // unknown ("new in 1.71")
        string name = r.StringU8();
        string guild = r.StringU8();
        return new NpcCreate(objectId, speed, heading, z, x, y, model, size, level, flags, name, guild);
    }
}

/// <summary>
/// 0x4B PlayerCreate (PacketLib1124, PlayerCreate172): another player enters view. X/Y/Z are
/// region-global LITTLE-endian float32 (like 0x20/0xA9). We read the fixed head, the appearance and
/// the name; the guild, title strings and horse block are skipped.
/// </summary>
public sealed record PlayerCreate(
    float X, float Y, float Z, ushort SessionId, ushort ObjectId, ushort Heading,
    ushort Model, byte Level, byte Flags, string Name)
{
    /// <summary>The player's face and hair (Catacombs appearance).</summary>
    public CharacterLook Look { get; init; }

    public const byte Code = 0x4B;

    public ushort HeadingOnly => (ushort)(Heading & 0x0FFF);

    public static PlayerCreate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        float x = r.F32LE(), y = r.F32LE(), z = r.F32LE();
        ushort sessionId = r.U16BE();
        ushort objectId = r.U16BE();
        ushort heading = r.U16BE();
        ushort model = r.U16BE();
        byte level = r.U8();
        byte flags = r.U8();
        // Appearance, in this packet's order: eye size, lip size, mood, eye colour, hair colour, face, hair style.
        byte eyeSize = r.U8(), lipSize = r.U8(), mood = r.U8(), eyeColor = r.U8(), hairColor = r.U8();
        byte faceType = r.U8(), hairStyle = r.U8();
        r.Skip(3);              // "new in 1.74" + 2 unknown
        string name = r.StringU8();
        return new PlayerCreate(x, y, z, sessionId, objectId, heading, model, level, flags, name)
        {
            Look = new CharacterLook(eyeSize, lipSize, eyeColor, hairColor, faceType, hairStyle, mood),
        };
    }
}

/// <summary>
/// 0xD9 ObjectCreate (PacketLib171.SendObjectCreate): a static world object (door, item, banner)
/// enters view. X/Y are region-global BIG-endian u32 integers.
/// </summary>
public sealed record ObjectCreate(
    ushort ObjectId, ushort Heading, ushort Z, uint X, uint Y, ushort Model, string Name)
{
    public const byte Code = 0xD9;

    public static ObjectCreate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        ushort objectId = r.U16BE();
        r.Skip(2);              // emblem
        ushort heading = r.U16BE();
        ushort z = r.U16BE();
        uint x = r.U32BE();
        uint y = r.U32BE();
        ushort model = r.U16BE();
        r.Skip(2);              // flag
        r.Skip(4);              // unknown ("new in 1.71"; present on 171+)
        string name = r.StringU8();
        return new ObjectCreate(objectId, heading, z, x, y, model, name);
    }
}

/// <summary>
/// 0xA2 RemoveObject (PacketLib168.SendObjectRemove): an entity leaves view. 4-byte payload.
/// </summary>
/// <param name="ObjectType">2 = player, 1 = living NPC, 0 = dead NPC/other.</param>
public sealed record RemoveObject(ushort ObjectId, ushort ObjectType)
{
    public const byte Code = 0xA2;

    public static RemoveObject Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new RemoveObject(r.U16BE(), r.U16BE());
    }
}

/// <summary>
/// 0xE1 ObjectDelete (PacketLib168.SendObjectDelete): an entity is destroyed. 4-byte payload; the
/// second short is a constant 1.
/// </summary>
public sealed record ObjectDelete(ushort ObjectId)
{
    public const byte Code = 0xE1;

    public static ObjectDelete Parse(ReadOnlySpan<byte> payload) =>
        new(new PayloadReader(payload).U16BE());
}
