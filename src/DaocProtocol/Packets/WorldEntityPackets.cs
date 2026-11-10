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
/// <param name="Model">The model field as sent: the model id (monsters.csv row) in the low 14 bits, and
/// <see cref="Swimming"/> in bit 15.</param>
public sealed record NpcCreate(
    ushort ObjectId, ushort Speed, ushort Heading, ushort Z, uint X, uint Y,
    ushort Model, byte Size, byte Level, byte Flags, string Name, string GuildName)
{
    public const byte Code = 0xDA;

    /// <summary>The model id: the field's low 14 bits, as the retail client reads it (open question W39).</summary>
    public ushort ModelId => (ushort)(Model & 0x3FFF);

    /// <summary>Bit 15: the NPC is created swimming (retail: swim animations, height from the water
    /// surface; the same state a player's 0xA9 flag 0x04 sets). OpenDAoC stores it on its water and swamp
    /// creatures (1,178 of 97,737 spawns: river sprite 32904 = 136, frogs, eels, crabs).</summary>
    public bool Swimming => (Model & 0x8000) != 0;

    public ushort HeadingOnly => (ushort)(Heading & 0x0FFF);

    /// <summary>Flags 0x20: the NPC flies (PacketLib1124.SendNPCCreate: GameNPC.eFlags.FLYING), so its height is its own,
    /// not the ground's.</summary>
    public bool Flying => (Flags & 0x20) != 0;

    /// <summary>The second flags byte (PacketLib1124.SendNPCCreate): 0x01 can't be targeted, 0x02 name
    /// hidden, 0x04 stealthed, 0x08/0x10 quest available/finishable, 0x80 a pet.</summary>
    public byte Flags2 { get; init; }

    /// <summary>
    /// The NPC can't be targeted: an effect or marker rather than a creature (e.g. OpenDAoC's teleporter
    /// indicator, model 1923, whose figure is the client's "Corpse Light Node"). A GM account gets no bit
    /// for it: the server appends "-DOR" to the name instead (and "-NON" for a hidden name).
    /// </summary>
    public bool Untargetable => (Flags2 & 0x01) != 0 || Name.Contains("-DOR", StringComparison.Ordinal);

    /// <summary>The third flags byte: quest states (0x01 lesson, 0x02 lore, 0x20 pending).</summary>
    public byte Flags3 { get; init; }

    /// <summary>The glyph over the NPC's head (PacketLib1124.SendNPCCreate): flags2 0x08 available, 0x10 finish;
    /// flags3 0x01 lesson, 0x02 lore, 0x20 pending.</summary>
    public QuestIndicator Quest =>
        (Flags2 & 0x08) != 0 ? QuestIndicator.Available
        : (Flags2 & 0x10) != 0 ? QuestIndicator.Finish
        : (Flags3 & 0x01) != 0 ? QuestIndicator.Lesson
        : (Flags3 & 0x02) != 0 ? QuestIndicator.Lore
        : (Flags3 & 0x20) != 0 ? QuestIndicator.Pending
        : QuestIndicator.None;

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
        byte flags2 = r.U8();
        byte flags3 = r.U8();
        r.Skip(2);              // unknown ("new in 1.71")
        string name = r.StringU8();
        string guild = r.StringU8();
        return new NpcCreate(objectId, speed, heading, z, x, y, model, size, level, flags, name, guild) { Flags2 = flags2, Flags3 = flags3 };
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

    /// <summary>The strings after the name (PacketLib1124.SendPlayerCreate): guild, last name, prefix (a realm
    /// rank title such as "Sir"), and title (1.74+). Empty when not sent.</summary>
    public string GuildName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Title { get; init; } = "";

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
        // Older writers stop after the name; read what is there.
        string Next(ref PayloadReader reader) => reader.Remaining > 0 ? reader.StringU8() : "";
        string guild = Next(ref r), lastName = Next(ref r), prefix = Next(ref r), title = Next(ref r);
        return new PlayerCreate(x, y, z, sessionId, objectId, heading, model, level, flags, name)
        {
            Look = new CharacterLook(eyeSize, lipSize, eyeColor, hairColor, faceType, hairStyle, mood),
            GuildName = guild, LastName = lastName, Prefix = prefix, Title = title,
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

/// <summary>
/// S→C 0xA9 PlayerPosition (1.127 layout, OpenDAoC PlayerPositionUpdateHandler.CreateOutPak1127): another
/// player's position, relayed from its own 0xA9. Sent "by UDP", which OpenDAoC delivers over TCP while the
/// client hasn't confirmed UDP (our 0xBF says it hasn't). 40 bytes; X/Y/Z region-global little-endian
/// float32 like our own 0xA9, shorts big-endian.
/// </summary>
/// <param name="Health">Health byte: the low 7 bits are the percent, bit 0x80 attack mode (weapons drawn).</param>
public sealed record PlayerPosition(float X, float Y, float Z, float Speed, float FallSpeed, ushort SessionId,
    ushort ObjectId, ushort ZoneId, byte StateFlags, ushort Heading, byte ActionFlags, byte Health)
{
    public const byte Code = 0xA9;

    public ushort HeadingOnly => (ushort)(Heading & 0x0FFF);
    public bool InCombat => (Health & 0x80) != 0;
    public byte HealthPercent => (byte)(Health & 0x7F);

    public static PlayerPosition Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        float x = r.F32LE(), y = r.F32LE(), z = r.F32LE(), speed = r.F32LE(), fall = r.F32LE();
        ushort session = r.U16BE(), objectId = r.U16BE(), zone = r.U16BE();
        byte state = r.U8();
        r.Skip(1);
        r.Skip(2);                 // steed seat
        ushort heading = r.U16BE();
        byte action = r.U8();
        r.Skip(2);                 // roleplay flag, 0
        byte health = r.U8();
        return new PlayerPosition(x, y, z, speed, fall, session, objectId, zone, state, heading, action, health);
    }
}

/// <summary>
/// S→C 0xBA PlayerHeading (1.127 layout, OpenDAoC PlayerHeadingUpdateHandler.CreateOutPak1127): another
/// player turned, or its state changed while standing. Keyed by session id, not object id (the player's
/// 0x4B carries both). Also delivered over TCP without UDP.
/// </summary>
/// <param name="Health">As in <see cref="PlayerPosition"/>: bit 0x80 attack mode.</param>
public sealed record PlayerHeading(ushort SessionId, ushort Heading, byte ActionFlags, byte StateFlags, byte Health)
{
    public const byte Code = 0xBA;

    public ushort HeadingOnly => (ushort)(Heading & 0x0FFF);
    public bool InCombat => (Health & 0x80) != 0;

    public static PlayerHeading Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        ushort session = r.U16BE();
        r.Skip(2);                 // current target (0 from OpenDAoC)
        ushort heading = r.U16BE();
        r.Skip(1);                 // steed seat
        byte action = r.U8();
        r.Skip(1);
        byte state = r.U8();
        byte health = r.U8();
        return new PlayerHeading(session, heading, action, state, health);
    }
}

/// <summary>A quest giver's glyph state; the values are OpenDAoC's eQuestIndicator, which 0x4C sends as is.</summary>
public enum QuestIndicator : byte
{
    None = 0x00,
    /// <summary>A quest to take (retail: a gold knot).</summary>
    Available = 0x01,
    /// <summary>A quest to hand in (a cyan knot).</summary>
    Finish = 0x02,
    Lesson = 0x04,
    Lore = 0x08,
    /// <summary>Taken, not finished yet (a grey knot).</summary>
    Pending = 0x10,
}

/// <summary>
/// 0x4C (server → client): a visual effect on an object. Object id u16 BE, kind u8, then the kind's data.
/// Kind 7 (PacketLib173.SendNPCsQuestEffect) changes an NPC's quest glyph: indicator u8, then u32 0. The
/// recorded session has only kinds 5 and 9.
/// </summary>
public sealed record VisualEffect(ushort ObjectId, byte Kind, byte[] Data)
{
    public const byte Code = 0x4C;
    public const byte QuestKind = 0x07;

    /// <summary>The new glyph when this is a quest effect, else null.</summary>
    public QuestIndicator? Quest => Kind == QuestKind && Data.Length > 0 ? (QuestIndicator)Data[0] : null;

    public static VisualEffect Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        ushort id = r.U16BE();
        byte kind = r.U8();
        return new VisualEffect(id, kind, payload[3..].ToArray());
    }
}
