// SPDX-License-Identifier: GPL-3.0-or-later
//
// The character's skills and spells (roadmap M9: quickbars; M10: the spell and style sheets), the S→C 0x16
// VariousUpdate lists. Layouts ported (field order only) from OpenDAoC-Core @f0aaa1e, GPL-3.0:
// PacketLib1112.SendUpdatePlayerSkills (subcode 1) and SendNonHybridSpellLines (subcode 2),
// PacketLib1126.SendUpdateWeaponAndArmorStats (5), PacketLib168.SendUpdateCraftingSkills (8). Checked
// against the RustDAoC 1.127e capture (a level 50 Paladin: 68 skills over two pages, 14 crafting skills).
//
// Every 0x16 starts with four bytes: subcode, entry count, a subtype, and one more (the first index, or the
// spell line's number). The skill list comes in pages; the server sends the "usable skills" in one order, and
// the client refers to them by that index (a spell's specialisation, a style's prerequisite style).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>Which page of the skill list an entry belongs to (OpenDAoC eSkillPage).</summary>
public enum SkillPage : byte
{
    Specialization = 0,
    Ability = 1,
    Style = 2,
    Spell = 3,
    Song = 4,
    AbilitySpell = 5,
    RealmAbility = 6,
}

/// <summary>
/// One usable skill: its level (a style's: the specialisation level it needs), the server's id for it (what
/// the client sends to use it), its page, a page-specific value (<see cref="Special"/>), a bonus byte, its icon
/// and name.
/// </summary>
/// <param name="Special">A spell: the index of its specialisation in this list (0xFF a song, 0xFE none). A
/// style: its opening requirement (0x100 none; under 100 the attacker's last result, 100+ the defender's,
/// 200+ a position; the high byte names a prerequisite style by its index from the first style, + 100).
/// Otherwise 0.</param>
/// <param name="Bonus">A specialisation: the levels added by items and effects. A style: the internal index
/// of its specialisation. Otherwise 0.</param>
public sealed record SkillEntry(byte Level, ushort Id, SkillPage Page, ushort Special, byte Bonus, ushort Icon, string Name);

/// <summary>
/// 0x16 subcode 1: one page of the usable-skill list. <see cref="FirstIndex"/> is the list position of its
/// first entry (0 starts the list again); <see cref="MoreFollow"/> (subtype 99) says another page follows,
/// otherwise (subtype 3) this page ends the list. Entry: level u8, id u16 BE, page u8, special u16 BE,
/// bonus u8, icon u16 BE, name (u8 length).
/// </summary>
public sealed record SkillListPage(byte FirstIndex, bool MoreFollow, IReadOnlyList<SkillEntry> Entries)
{
    public const byte Subcode = 1;
    private const byte More = 99, Last = 3;

    public static SkillListPage Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        r.Skip(1);
        int count = r.U8();
        byte subtype = r.U8(), first = r.U8();
        var entries = new List<SkillEntry>(count);
        for (int k = 0; k < count; k++)
            entries.Add(new SkillEntry(r.U8(), r.U16BE(), (SkillPage)r.U8(), r.U16BE(), r.U8(), r.U16BE(), r.StringU8()));
        return new SkillListPage(first, subtype == More, entries);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8(Subcode).U8((byte)Entries.Count).U8(MoreFollow ? More : Last).U8(FirstIndex);
        foreach (SkillEntry e in Entries)
            w.U8(e.Level).U16BE(e.Id).U8((byte)e.Page).U16BE(e.Special).U8(e.Bonus).U16BE(e.Icon).StringU8(e.Name);
        return w.ToArray();
    }
}

/// <summary>What a spell line's entry is: a spell, or an ability or style listed with the line.</summary>
public enum LineEntryKind : byte { Spell = 0, Ability = 1, Style = 2 }

/// <summary>One entry of a spell line: the level it comes at, its id, icon and name.</summary>
public sealed record LineEntry(byte Level, LineEntryKind Kind, ushort Id, ushort Icon, string Name);

/// <summary>
/// 0x16 subcode 2: one spell line of a list caster (subtype 2), or the footer that ends the lines (subtype
/// 99, no entries). After the head (2, entries + 1, subtype, the line's number) come the line itself and then
/// its entries, each: a u16 LITTLE-endian level word (low byte the level; high byte 1 an ability, 2 a style,
/// 0 a spell), id u16 BE, icon u16 BE, name (u8 length). The line's own level word and icon are 0.
/// </summary>
public sealed record SpellLinePage(byte LineIndex, bool IsFooter, ushort LineId, string LineName, IReadOnlyList<LineEntry> Entries)
{
    public const byte Subcode = 2;
    private const byte Line = 2, Footer = 99;

    public static SpellLinePage Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        r.Skip(1);
        int count = r.U8();
        byte subtype = r.U8(), index = r.U8();
        if (subtype == Footer || count == 0)
            return new SpellLinePage(index, true, 0, "", []);
        r.Skip(2);   // the line's level word (0)
        ushort lineId = r.U16BE();
        r.Skip(2);   // its icon (0)
        string lineName = r.StringU8();
        var entries = new List<LineEntry>(count - 1);
        for (int k = 1; k < count; k++)
        {
            ushort word = r.U16LE();
            entries.Add(new LineEntry((byte)word, (LineEntryKind)(word >> 8), r.U16BE(), r.U16BE(), r.StringU8()));
        }
        return new SpellLinePage(index, false, lineId, lineName, entries);
    }

    public byte[] ToBytes()
    {
        if (IsFooter)
            return new PayloadWriter().U8(Subcode).U8(0).U8(Footer).U8(LineIndex).ToArray();
        var w = new PayloadWriter().U8(Subcode).U8((byte)(Entries.Count + 1)).U8(Line).U8(LineIndex)
            .U16LE(0).U16BE(LineId).U16BE(0).StringU8(LineName);
        foreach (LineEntry e in Entries)
            w.U16LE((ushort)(e.Level | (byte)e.Kind << 8)).U16BE(e.Id).U16BE(e.Icon).StringU8(e.Name);
        return w.ToArray();
    }
}

/// <summary>
/// 0x16 subcode 5 (1.126+): weapon damage (× 100), weapon skill and the overall armour factor, each a u16
/// split into a high and a low byte with a zero after each: 05 06 00 00, then hi 0 lo 0 three times.
/// </summary>
public sealed record WeaponStats(ushort WeaponDamageX100, ushort WeaponSkill, ushort ArmorFactor)
{
    public const byte Subcode = 5;

    public double WeaponDamage => WeaponDamageX100 / 100.0;

    public static WeaponStats Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        r.Skip(4);
        var v = new ushort[3];
        for (int k = 0; k < 3; k++)
        {
            byte hi = r.U8();
            r.Skip(1);
            byte lo = r.U8();
            r.Skip(1);
            v[k] = (ushort)(hi << 8 | lo);
        }
        return new WeaponStats(v[0], v[1], v[2]);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8(Subcode).U8(6).U8(0).U8(0);
        foreach (ushort v in new[] { WeaponDamageX100, WeaponSkill, ArmorFactor })
            w.U8((byte)(v >> 8)).U8(0).U8((byte)v).U8(0);
        return w.ToArray();
    }
}

/// <summary>One crafting skill: points, icon, name.</summary>
public sealed record CraftingSkill(ushort Points, byte Icon, string Name);

/// <summary>
/// 0x16 subcode 8: the crafting skills. Head 08, count, 3, 0; each: points u16 BE, icon u8, u32 BE 1, name
/// (u8 length).
/// </summary>
public sealed record CraftingSkills(IReadOnlyList<CraftingSkill> Skills)
{
    public const byte Subcode = 8;

    public static CraftingSkills Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        r.Skip(1);
        int count = r.U8();
        r.Skip(2);
        var skills = new List<CraftingSkill>(count);
        for (int k = 0; k < count; k++)
        {
            ushort points = r.U16BE();
            byte icon = r.U8();
            r.Skip(4);
            skills.Add(new CraftingSkill(points, icon, r.StringU8()));
        }
        return new CraftingSkills(skills);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8(Subcode).U8((byte)Skills.Count).U8(3).U8(0);
        foreach (CraftingSkill s in Skills)
            w.U16BE(s.Points).U8(s.Icon).U32BE(1).StringU8(s.Name);
        return w.ToArray();
    }
}
