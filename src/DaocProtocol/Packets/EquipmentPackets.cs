// SPDX-License-Identifier: GPL-3.0-or-later
//
// What a character wears. 0x15 EquipmentUpdate (S→C) lists a living's visible items, sent to the
// players near it when it comes into view or changes gear; never to the wearer itself (OpenDAoC
// GamePlayer.BroadcastEquipmentUpdate skips the sender), whose own gear is in the character overview
// (CharacterSummary.Worn). Layout ported (field order only) from OpenDAoC-Core PacketLib1124
// .SendLivingEquipmentUpdate, GPL-3.0. Shorts are big-endian.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>Inventory slots of worn items (OpenDAoC Slot / eInventorySlot).</summary>
public static class WornSlot
{
    public const byte RightHand = 10, LeftHand = 11, TwoHand = 12, Ranged = 13;
    public const byte Helm = 21, Hands = 22, Feet = 23, Torso = 25, Cloak = 26, Legs = 27, Arms = 28;
}

/// <summary>One visible item: its slot, model (an objects.csv row), extension (an armour piece's look
/// variant), dye colour or guild emblem (0 = none) and effect.</summary>
public readonly record struct WornItem(byte Slot, ushort Model, byte Extension = 0, ushort Color = 0, ushort Effect = 0);

/// <summary>0x15 EquipmentUpdate: a living's visible items.</summary>
public sealed record EquipmentUpdate(ushort ObjectId, bool CloakHidden, bool HelmHidden, bool HoodUp,
    IReadOnlyList<WornItem> Items)
{
    public const byte Code = 0x15;

    /// <summary>
    /// object id, active weapon slots, speed, visibility (bit 0 cloak hidden, bit 1 helm hidden), hood
    /// (bit 0) and quiver, item count; per item: slot (bit 7: new emblem, on the cloak and left hand),
    /// model (low 13 bits; 0x8000: a u16 colour follows, 0x4000: a u8 colour, 0x2000: a u16 effect), an
    /// extension byte for anything but the four weapon slots, then the colour and effect.
    /// </summary>
    public static EquipmentUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        ushort objectId = r.U16BE();
        r.U8();                                  // active weapon slots
        r.U8();                                  // speed
        byte visibility = r.U8();
        byte hood = r.U8();
        int count = r.U8();
        var items = new List<WornItem>(count);
        for (int i = 0; i < count; i++)
        {
            byte slot = (byte)(r.U8() & 0x7F);
            ushort raw = r.U16BE();
            byte extension = slot is < WornSlot.RightHand or > WornSlot.Ranged ? r.U8() : (byte)0;
            ushort color = (raw & 0x8000) != 0 ? r.U16BE() : (raw & 0x4000) != 0 ? r.U8() : (ushort)0;
            ushort effect = (raw & 0x2000) != 0 ? r.U16BE() : (ushort)0;
            items.Add(new WornItem(slot, (ushort)(raw & 0x1FFF), extension, color, effect));
        }
        return new EquipmentUpdate(objectId, (visibility & 0x01) != 0, (visibility & 0x02) != 0, (hood & 0x01) != 0, items);
    }
}
