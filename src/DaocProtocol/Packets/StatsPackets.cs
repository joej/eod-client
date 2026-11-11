// SPDX-License-Identifier: GPL-3.0-or-later
//
// The character sheet's numbers (roadmap M9: the stats panel's Attributes page): S→C 0xFB StatsUpdate, which
// carries either the eight stats or the nine resists. Layout ported (field order only) from OpenDAoC-Core
// @f0aaa1e, GPL-3.0: PacketLib175.SendCharStatsUpdate and SendCharResistsUpdate. Checked against the RustDAoC
// 1.127e capture (a level 50 Paladin), whose 0xFB packets of both kinds decode and rebuild byte for byte.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// S→C 0xFB StatsUpdate, 78 bytes: nine values in each of five columns, then a kind byte at offset 72 (0x00
/// stats, 0xFF resists), the constitution lost to deaths, max health (u16 BE; stats only) and two zero bytes.
/// Columns: base (u16 BE; a resist's base is its racial resist), buffs and debuffs (signed u16 BE), item
/// bonuses (u16 BE), the item cap (u8) and realm-ability bonuses (u8). Stats fill eight of the nine places
/// (the ninth is 0), resists all nine; see <see cref="StatNames"/> and <see cref="ResistNames"/> for the order.
/// </summary>
public sealed record StatsUpdate(bool IsResists, short[] Base, short[] Buff, short[] Item, byte[] ItemCap, byte[] Bonus,
    byte ConstitutionLost, ushort MaxHealth)
{
    public const byte Code = 0xFB;
    private const int Places = 9;

    /// <summary>The stats' order in the packet (OpenDAoC's _updateStats).</summary>
    public static readonly string[] StatNames = ["Strength", "Dexterity", "Constitution", "Quickness", "Intelligence", "Piety", "Empathy", "Charisma"];

    /// <summary>The resists' order in the packet.</summary>
    public static readonly string[] ResistNames = ["Crush", "Slash", "Thrust", "Heat", "Cold", "Matter", "Body", "Spirit", "Energy"];

    /// <summary>
    /// What the character has in place <paramref name="i"/>: base + buffs + item bonuses up to the cap + realm
    /// abilities. That is how the server counts the stat (its buff column is what remains once the base, the
    /// capped items and the abilities are taken off the total), so this is the server's own figure.
    /// </summary>
    public int Total(int i) => Base[i] + Buff[i] + Math.Min(Item[i], (short)ItemCap[i]) + Bonus[i];

    public static StatsUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        short[] b = Shorts(ref r), buff = Shorts(ref r), item = Shorts(ref r);
        byte[] cap = Bytes(ref r), bonus = Bytes(ref r);
        bool resists = r.U8() == 0xFF;
        byte conLost = r.U8();
        ushort maxHealth = r.U16BE();
        return new StatsUpdate(resists, b, buff, item, cap, bonus, conLost, maxHealth);
    }

    private static short[] Shorts(ref PayloadReader r)
    {
        var a = new short[Places];
        for (int i = 0; i < Places; i++)
            a[i] = (short)r.U16BE();
        return a;
    }

    private static byte[] Bytes(ref PayloadReader r)
    {
        var a = new byte[Places];
        for (int i = 0; i < Places; i++)
            a[i] = r.U8();
        return a;
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter();
        foreach (short[] column in new[] { Base, Buff, Item })
            foreach (short v in column)
                w.U16BE((ushort)v);
        foreach (byte[] column in new[] { ItemCap, Bonus })
            foreach (byte v in column)
                w.U8(v);
        return w.U8(IsResists ? (byte)0xFF : (byte)0).U8(ConstitutionLost).U16BE(MaxHealth).U16BE(0).ToArray();
    }
}
