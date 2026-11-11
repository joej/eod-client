// SPDX-License-Identifier: GPL-3.0-or-later
//
// The character's own items (roadmap M10: the stats panel's Inventory page). Layouts ported (field order only) from
// OpenDAoC-Core @f0aaa1e, GPL-3.0: PacketLib189.SendInventoryItemsPartialUpdate (0x02 header), PacketLib1124.
// WriteItemData (each item, 1.124+), PacketLib168.SendEncumbrance (0xBD) and PlayerMoveItemRequestHandler (0xDD).
// Checked against the RustDAoC 1.127e capture (a level 50 Paladin): both of its 0x02 packets and its 0xBD decode to
// the end and rebuild byte for byte.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>The server's inventory slot numbers (OpenDAoC eInventorySlot): the paper doll, the backpack.</summary>
public static class InventorySlot
{
    public const byte RightHand = 10, LeftHand = 11, TwoHand = 12, Ranged = 13;
    public const byte Head = 21, Hands = 22, Feet = 23, Jewellery = 24, Torso = 25, Cloak = 26, Legs = 27, Arms = 28,
        Neck = 29, Waist = 32, LeftWrist = 33, RightWrist = 34, LeftRing = 35, RightRing = 36, Mythical = 37;
    public const byte FirstBackpack = 40, LastBackpack = 79;

    /// <summary>Dropping on the paper doll: the server picks the slot (OpenDAoC ConvertPaperDollDestinationSlot).</summary>
    public const ushort PaperDoll = 100;
}

/// <summary>
/// One item as the server describes it (1.124+, 24 bytes + its name): u16 BE 0 (unique id), level, two values (by
/// object type: a weapon's DPS × 10 and speed × 10, armour's AF and absorption, a stack's count…), hand (bits 6–7),
/// damage type (bits 6–7) | object type (bits 0–5), 0, u16 BE weight (tenths of a pound), condition %, durability %,
/// quality %, bonus %, bonus level, u16 BE model (an objects.csv row), extension, u16 BE colour (or emblem), flags
/// (0x02 salvage, 0x04 craft, 0x08 / 0x10 a charge or proc spell follows: u16 BE icon + u8-length name), u16 BE
/// effect, name (u8 length; the server puts a stack's count in front). An empty slot is the same 24 bytes, all 0.
/// </summary>
public sealed record InventoryItem(byte Level, byte Value1, byte Value2, byte Hand, byte DamageType, byte ObjectType,
    ushort WeightTenths, byte Condition, byte Durability, byte Quality, byte Bonus, byte BonusLevel, ushort Model,
    byte Extension, ushort Colour, byte Flags, ushort SpellIcon1, string SpellName1, ushort SpellIcon2, string SpellName2,
    ushort Effect, string Name)
{
    public bool IsEmpty => Name.Length == 0 && Model == 0;

    public bool IsWeapon => ObjectType is >= 1 and <= 28;
    public bool IsArmour => ObjectType is >= 31 and <= 38;
    public bool IsShield => ObjectType == 42;

    /// <summary>OpenDAoC's eObjectType names for the kinds a player sees most.</summary>
    public string Kind => ObjectType switch
    {
        0 => "Item", 2 => "Crushing", 3 => "Slashing", 4 => "Thrusting", 5 => "Fired", 6 => "Two-handed", 7 => "Polearm",
        8 => "Staff", 9 => "Longbow", 10 => "Crossbow", 11 => "Sword", 12 => "Hammer", 13 => "Axe", 14 => "Spear",
        15 => "Composite bow", 16 => "Thrown", 17 => "Left axe", 18 => "Recurve bow", 19 => "Blades", 20 => "Blunt",
        21 => "Piercing", 22 => "Large weapon", 23 => "Celtic spear", 24 => "Flexible", 25 => "Hand to hand", 26 => "Scythe",
        27 => "Fist wraps", 28 => "Mauler staff", 32 => "Cloth", 33 => "Leather", 34 => "Studded", 35 => "Chain", 36 => "Plate",
        37 => "Reinforced", 38 => "Scale", 41 => "Magical", 42 => "Shield", 43 => "Arrow", 44 => "Bolt", 45 => "Instrument",
        46 => "Poison", 47 => "Tincture", 48 => "Spellcraft gem", _ => $"Type {ObjectType}",
    };

    public static readonly InventoryItem Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", 0, "", 0, "");

    public static InventoryItem Read(ref PayloadReader r)
    {
        r.Skip(2);
        byte level = r.U8(), v1 = r.U8(), v2 = r.U8(), hand = r.U8(), type = r.U8();
        r.Skip(1);
        ushort weight = r.U16BE();
        byte con = r.U8(), dur = r.U8(), qua = r.U8(), bonus = r.U8(), bonusLevel = r.U8();
        ushort model = r.U16BE();
        byte ext = r.U8();
        ushort colour = r.U16BE();
        byte flags = r.U8();
        ushort icon1 = 0, icon2 = 0;
        string spell1 = "", spell2 = "";
        if ((flags & 0x08) != 0)
            (icon1, spell1) = (r.U16BE(), r.StringU8());
        if ((flags & 0x10) != 0)
            (icon2, spell2) = (r.U16BE(), r.StringU8());
        ushort effect = r.U16BE();
        string name = r.StringU8();
        return new InventoryItem(level, v1, v2, (byte)(hand >> 6), (byte)(type >> 6), (byte)(type & 0x3F), weight, con, dur, qua,
            bonus, bonusLevel, model, ext, colour, flags, icon1, spell1, icon2, spell2, effect, name);
    }

    public void Write(PayloadWriter w)
    {
        w.U16BE(0).U8(Level).U8(Value1).U8(Value2).U8((byte)(Hand << 6)).U8((byte)(DamageType << 6 | ObjectType)).U8(0)
            .U16BE(WeightTenths).U8(Condition).U8(Durability).U8(Quality).U8(Bonus).U8(BonusLevel).U16BE(Model).U8(Extension)
            .U16BE(Colour).U8(Flags);
        if ((Flags & 0x08) != 0)
            w.U16BE(SpellIcon1).StringU8(SpellName1);
        if ((Flags & 0x10) != 0)
            w.U16BE(SpellIcon2).StringU8(SpellName2);
        w.U16BE(Effect).StringU8(Name);
    }
}

/// <summary>
/// S→C 0x02 InventoryUpdate: count, 0, visibility (0x01 cloak hidden, 0x02 helm hidden), hood (bit 0) | active quiver
/// (bits 4–7) (or a house vault's number), the visible weapon slots, the window (0 an update, 1 all equipment, 2 all the
/// rest, 3+ a vault, merchant or other window to open), then count × (slot u8, item). An empty item clears its slot.
/// </summary>
public sealed record InventoryUpdate(byte Visibility, byte HoodAndQuiver, byte VisibleWeapons, byte Window,
    IReadOnlyList<(byte Slot, InventoryItem Item)> Items)
{
    public const byte Code = 0x02;

    public static InventoryUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        int count = r.U8();
        r.Skip(1);
        byte visibility = r.U8(), hood = r.U8(), weapons = r.U8(), window = r.U8();
        var items = new List<(byte, InventoryItem)>(count);
        for (int i = 0; i < count; i++)
        {
            byte slot = r.U8();
            items.Add((slot, InventoryItem.Read(ref r)));
        }
        return new InventoryUpdate(visibility, hood, weapons, window, items);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8((byte)Items.Count).U8(0).U8(Visibility).U8(HoodAndQuiver).U8(VisibleWeapons).U8(Window);
        foreach ((byte slot, InventoryItem item) in Items)
        {
            w.U8(slot);
            item.Write(w);
        }
        return w.ToArray();
    }
}

/// <summary>S→C 0xBD Encumbrance: u16 BE what the character can carry, u16 BE what it carries (pounds).</summary>
public sealed record Encumbrance(ushort Max, ushort Carried)
{
    public const byte Code = 0xBD;

    public static Encumbrance Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new Encumbrance(r.U16BE(), r.U16BE());
    }

    public byte[] ToBytes() => new PayloadWriter().U16BE(Max).U16BE(Carried).ToArray();
}

/// <summary>C→S 0xDD PlayerMoveItem: u16 BE 0, u16 BE to-slot, u16 BE from-slot, u16 BE count (a stack's share; 0 the
/// whole item). The server answers with 0x02 for both slots, or a chat line saying why not.</summary>
public static class MoveItemRequest
{
    public const byte Code = 0xDD;

    public static byte[] Payload(ushort to, ushort from, ushort count = 0) =>
        new PayloadWriter().U16BE(0).U16BE(to).U16BE(from).U16BE(count).ToArray();
}
