// SPDX-License-Identifier: GPL-3.0-or-later
//
// Moving objects: boats and siege weapons. Layout ported (field order only) from OpenDAoC-Core @f0aaa1e, GPL-3.0:
// PacketLib168.SendMovingObjectCreate (S→C 0x12), read as the retail 1.130 client reads it (daoc-re RE/98, 2026-11-24).
// OpenDAoC sends every GameMovingObject (GameBoat, and GameSiegeWeapon with its rams, catapults, ballistas, trebuchets and
// cauldrons) with this packet instead of 0xDA when it comes into view.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// S→C 0x12 MovingObjectCreate, as the retail client reads it: u16 BE object id; u16 BE the object's tint or emblem (as
/// 0xD9's, for a model with Apply Texture; OpenDAoC 0); u16 BE heading; s16 BE z; u32 BE x, y; u16 BE model (an
/// objects.csv row; 0xFFFF: none, drawn as nothing; above 5499: 0); a byte the client doesn't read (OpenDAoC's "type"
/// high byte and level); the flags byte (<see cref="Flags"/>); u16 BE vehicle emblem; u16 BE figurehead (1–14); u8 hull
/// tint; u8 trim tint (tints.csv rows; 0 none); u8 stored, unused; u8 whose bit 0 adds 0x10000 to the emblem; pascal name
/// (at most 47 bytes); then a byte, and when it isn't 0 a u32 BE id (the 0xD9 door-id path). OpenDAoC writes 0 for the
/// tint, figurehead and tints.
/// </summary>
public sealed record MovingObjectCreate(ushort ObjectId, ushort Heading, short Z, uint X, uint Y, ushort Model, byte Flags,
    uint Emblem, string Name)
{
    public const byte Code = 0x12;
    public const int MaxModel = 5499, MaxName = 47;

    /// <summary>Bytes 2–3: the object's tint (under 256) or emblem, for a model with Apply Texture.</summary>
    public ushort ObjectTint { get; init; }

    /// <summary>Byte 18, which the client doesn't read (OpenDAoC: its type's high byte, or'd with the level × 2).</summary>
    public byte Unread { get; init; }

    public ushort Figurehead { get; init; }
    public byte HullTint { get; init; }
    public byte TrimTint { get; init; }
    public byte Byte26 { get; init; }
    public byte Byte27 { get; init; }

    /// <summary>The id after the name (the 0xD9 door-id path), when its flag byte isn't 0.</summary>
    public uint? ExtraId { get; init; }

    public ushort HeadingOnly => (ushort)(Heading & 0x0FFF);

    /// <summary>The objects.csv row the client uses: 0xFFFF means none (no model drawn), anything above 5499 counts as 0.</summary>
    public ushort ModelRow => Model == 0xFFFF ? (ushort)0xFFFF : Model > MaxModel ? (ushort)0 : Model;

    /// <summary>Flags 0x01: not grounded (it floats, e.g. on water).</summary>
    public bool Floating => (Flags & 0x01) != 0;

    /// <summary>Flags 0x02: a vehicle: the look bytes count, 0xA1 moves it like an NPC, riders can sit on it.</summary>
    public bool Vehicle => (Flags & 0x02) != 0;

    /// <summary>Flags 0x04 and 0x08: it counts for Nearest Loot.</summary>
    public bool Lootable => (Flags & 0x0C) == 0x0C;

    /// <summary>The realm: 0x10 Albion, 0x20 Midgard, 0x40 Hibernia, tested in that order with the last set one winning;
    /// none 0.</summary>
    public int Realm => (Flags & 0x40) != 0 ? 3 : (Flags & 0x20) != 0 ? 2 : (Flags & 0x10) != 0 ? 1 : 0;

    public static MovingObjectCreate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        ushort id = r.U16BE(), tint = r.U16BE(), heading = r.U16BE();
        short z = (short)r.U16BE();
        uint x = r.U32BE(), y = r.U32BE();
        ushort model = r.U16BE();
        byte unread = r.U8(), flags = r.U8();
        ushort emblem = r.U16BE(), figurehead = r.U16BE();
        byte hull = r.U8(), trim = r.U8(), b26 = r.U8(), b27 = r.U8();
        string name = r.StringU8();
        if (name.Length > MaxName)
            name = name[..MaxName];
        uint? extra = r.Remaining > 0 && r.U8() != 0 && r.Remaining >= 4 ? r.U32BE() : null;
        return new MovingObjectCreate(id, heading, z, x, y, model, flags, emblem | ((b27 & 1u) << 16), name)
        {
            ObjectTint = tint, Unread = unread, Figurehead = figurehead, HullTint = hull, TrimTint = trim, Byte26 = b26,
            Byte27 = b27, ExtraId = extra,
        };
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U16BE(ObjectId).U16BE(ObjectTint).U16BE(Heading).U16BE((ushort)Z).U32BE(X).U32BE(Y)
            .U16BE(Model).U8(Unread).U8(Flags).U16BE((ushort)Emblem).U16BE(Figurehead).U8(HullTint).U8(TrimTint).U8(Byte26)
            .U8((byte)(Byte27 | (Emblem >> 16 & 1))).StringU8(Name);
        if (ExtraId is uint extraId)
            w.U8(1).U32BE(extraId);
        else
            w.U8(0);
        return w.ToArray();
    }
}
