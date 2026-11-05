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

    /// <summary>
    /// The weapon slots in use, from an active-weapons byte (OpenDAoC VisibleActiveWeaponSlots): the low
    /// half the right hand's, the high half the left's, each 0 right hand, 1 left hand, 2 two-hand,
    /// 3 ranged, 0xF none (0x10: sword and shield, 0x22: a two-hander, 0x33: a bow, 0xFF: none).
    /// </summary>
    public static (byte? Right, byte? Left) Active(byte activeWeapons)
    {
        static byte? Slot(int n) => n <= 3 ? (byte)(RightHand + n) : null;
        return (Slot(activeWeapons & 0x0F), Slot(activeWeapons >> 4));
    }
}

/// <summary>One visible item: its slot, model (an objects.csv row), extension (an armour piece's look
/// variant), dye colour or guild emblem (0 = none) and effect. The server sends one number for the dye or
/// the emblem (the emblem when the item has one, OpenDAoC SendLivingEquipmentUpdate); an emblem's 17th bit
/// travels as the slot byte's 0x80 (<see cref="EmblemHigh"/>, cloak and left hand only).</summary>
public readonly record struct WornItem(byte Slot, ushort Model, byte Extension = 0, ushort Color = 0, ushort Effect = 0,
    bool EmblemHigh = false)
{
    /// <summary>The item's guild emblem, or null when its number is a dye (dyes are tints.csv rows, 0-143,
    /// below the smallest emblem with a logo, 512).</summary>
    public Emblem? Emblem => Packets.Emblem.FromNumber(Color | (EmblemHigh ? 0x10000 : 0));

    /// <summary>The item's dye (a tints.csv row), 0 when undyed or carrying an emblem.</summary>
    public int Dye => Emblem is null ? Color : 0;
}

/// <summary>
/// A guild emblem: a logo (insignia.csv row, 1-255) over a two-colour pattern (0-3) in a primary colour
/// (patterns.csv set 1, 0-15) and a secondary one (set 2, 0-7). The number, as OpenDAoC's
/// EmblemDialogReponseHandler builds it: logo &lt;&lt; 9 | pattern &lt;&lt; 7 | primary &lt;&lt; 3 | secondary.
/// </summary>
public readonly record struct Emblem(int Logo, int Pattern, int Primary, int Secondary)
{
    public int Number => Logo << 9 | (Pattern & 3) << 7 | (Primary & 15) << 3 | (Secondary & 7);

    /// <summary>The emblem a number encodes; null without a logo (then it is a dye, or nothing).</summary>
    public static Emblem? FromNumber(int number) =>
        number >> 9 is > 0 and var logo ? new Emblem(logo, (number >> 7) & 3, (number >> 3) & 15, number & 7) : null;
}

/// <summary>0x15 EquipmentUpdate: a living's visible items. <see cref="ActiveWeapons"/> says which weapon
/// slots are in use (see <see cref="WornSlot.Active"/>).</summary>
public sealed record EquipmentUpdate(ushort ObjectId, bool CloakHidden, bool HelmHidden, bool HoodUp,
    IReadOnlyList<WornItem> Items, byte ActiveWeapons = 0xFF)
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
        byte active = r.U8();                    // active weapon slots
        r.U8();                                  // speed
        byte visibility = r.U8();
        byte hood = r.U8();
        int count = r.U8();
        var items = new List<WornItem>(count);
        for (int i = 0; i < count; i++)
        {
            byte slotByte = r.U8();
            byte slot = (byte)(slotByte & 0x7F);
            ushort raw = r.U16BE();
            byte extension = slot is < WornSlot.RightHand or > WornSlot.Ranged ? r.U8() : (byte)0;
            ushort color = (raw & 0x8000) != 0 ? r.U16BE() : (raw & 0x4000) != 0 ? r.U8() : (ushort)0;
            ushort effect = (raw & 0x2000) != 0 ? r.U16BE() : (ushort)0;
            items.Add(new WornItem(slot, (ushort)(raw & 0x1FFF), extension, color, effect, (slotByte & 0x80) != 0));
        }
        return new EquipmentUpdate(objectId, (visibility & 0x01) != 0, (visibility & 0x02) != 0, (hood & 0x01) != 0, items, active);
    }
}
