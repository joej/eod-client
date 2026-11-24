// SPDX-License-Identifier: GPL-3.0-or-later
//
// Merchants (roadmap M13). Layouts ported (field order only) from OpenDAoC-Core @f0aaa1e, GPL-3.0:
// PacketLib1125.SendMerchantWindow (0x17), PlayerBuyRequestHandler (0x78), PlayerSellRequestHandler (0x79) and
// PlayerAppraiseItemRequestHandler (0xE0). A merchant has up to 5 pages of 30 items; a buy names the item as
// page × 30 + index.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// One item on a merchant's page (1.125+): index on the page, level, value 1, value 2 (the server's SPD_ABS), hand
/// (bits 6–7; a garden object's DPS_AF instead), damage type (bits 6–7) | object type (bits 0–5), a "can use" byte,
/// u16 LE weight (a stack's: pack size × weight), u32 LE price in copper, u16 LE model (an objects.csv row) and the name
/// (u32 LE length counting a terminating zero). Value 1 is a weapon's DPS or armour's AF, a stack's pack size, a shield's
/// size, a thrown weapon's DPS. "Can use": 0 means the character can't use it, anything else that it can (retail, daoc-re
/// 2026-11-24; OpenDAoC writes 1 or 0).
/// </summary>
public sealed record MerchantItem(byte Index, byte Level, byte Value1, byte Value2, byte Hand, byte DamageType,
    byte ObjectType, bool CanUse, ushort Weight, uint Price, ushort Model, string Name)
{
    /// <summary>Arrows, bolts, poisons and generic items come in packs of <see cref="Value1"/> (OpenDAoC's types 43, 44,
    /// 46 and 0); a thrown weapon's pack size is in <see cref="Weight"/>.</summary>
    public int PackSize => ObjectType switch
    {
        0 or 43 or 44 or 46 => Math.Max(1, (int)Value1),
        16 => Math.Max(1, (int)Weight),
        _ => 1,
    };

    public static MerchantItem Read(ref PayloadReader r)
    {
        byte index = r.U8(), level = r.U8(), v1 = r.U8(), v2 = r.U8(), hand = r.U8(), type = r.U8();
        bool canUse = r.U8() != 0;
        ushort weight = r.U16LE();
        uint price = r.U32LE();
        ushort model = r.U16LE();
        string name = r.StringIntLE();
        return new MerchantItem(index, level, v1, v2, (byte)(hand >> 6), (byte)(type >> 6), (byte)(type & 0x3F), canUse,
            weight, price, model, name);
    }

    public void Write(PayloadWriter w) =>
        w.U8(Index).U8(Level).U8(Value1).U8(Value2).U8((byte)(Hand << 6)).U8((byte)(DamageType << 6 | ObjectType))
            .U8(CanUse ? (byte)1 : (byte)0).U16LE(Weight).U32LE(Price).U16LE(Model).StringIntLE(Name);
}

/// <summary>
/// S→C 0x17 MerchantWindow: one page of a merchant's items: count, window type (<see cref="MerchantWindowType"/>), page
/// (0–4), then count × <see cref="MerchantItem"/>. OpenDAoC sends one per page that has items, all at once when the player
/// interacts with the merchant; a merchant with nothing to sell gets one empty page 0 (count 0, plus a byte 0).
/// Retail drops a page whose item name is longer than 47 bytes (daoc-re packet table).
/// </summary>
public sealed record MerchantPage(byte WindowType, byte Page, IReadOnlyList<MerchantItem> Items)
{
    public const byte Code = 0x17;
    public const int Pages = 5, ItemsPerPage = 30;

    public static MerchantPage Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        int count = r.U8();
        byte type = r.U8(), page = r.U8();
        var items = new List<MerchantItem>(count);
        for (int i = 0; i < count; i++)
            items.Add(MerchantItem.Read(ref r));
        return new MerchantPage(type, page, items);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8((byte)Items.Count).U8(WindowType).U8(Page);
        if (Items.Count == 0)
            w.U8(0);
        foreach (MerchantItem item in Items)
            item.Write(w);
        return w.ToArray();
    }
}

/// <summary>OpenDAoC's eMerchantWindowType: what the merchant takes. 1 bounty points, 2 "count" (an item currency);
/// 3 and up are housing menus, whose buys go to the housing code instead of the targeted merchant.</summary>
public static class MerchantWindowType
{
    public const byte Normal = 0, BountyPoints = 1, Count = 2;
}

/// <summary>
/// C→S 0x78 BuyRequest, 16 bytes: u32 BE x, u32 BE y (whole units, where the player stands), u16 BE session id (as
/// 0x7A; the server reads and ignores it), u16 BE item (page × 30 + index, 0–149), u8 how many (1–100; packs, for
/// stacked items), u8 window type (the last 0x17's), then 2 bytes retail never writes (we send 0). daoc-re 2026-11-24.
/// The server sells from the player's current target and checks money and distance (WORLD_PICKUP_DISTANCE).
/// </summary>
public static class BuyRequest
{
    public const byte Code = 0x78;

    public static byte[] Payload(float x, float y, ushort sessionId, ushort item, byte count, byte windowType) =>
        new PayloadWriter().U32BE((uint)x).U32BE((uint)y).U16BE(sessionId).U16BE(item).U8(count).U8(windowType).Zeros(2).ToArray();
}

/// <summary>
/// C→S 0x79 SellRequest and 0xE0 PlayerAppraiseItemRequest: u32 BE x, u32 BE y, u16 BE session id (ignored), u16 BE
/// inventory slot (a backpack slot, 40–79). The server sells the whole stack to the targeted merchant, or names its price
/// in chat (appraise).
/// </summary>
public static class SellRequest
{
    public const byte Code = 0x79, AppraiseCode = 0xE0;

    public static byte[] Payload(float x, float y, ushort sessionId, ushort slot) =>
        new PayloadWriter().U32BE((uint)x).U32BE((uint)y).U16BE(sessionId).U16BE(slot).ToArray();
}
