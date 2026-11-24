// SPDX-License-Identifier: GPL-3.0-or-later
//
// Casting and effects (roadmap M13). Layouts ported (field order only) from OpenDAoC-Core @f0aaa1e, GPL-3.0:
// PacketLib168.SendSpellCastAnimation (S→C 0x72), SendInterruptAnimation (0x73), SendSpellEffectAnimation (0x1B),
// PacketLib191.SendConcentrationList (0x75), PacketLib1110.SendUpdateIcons (0x7F), RemoveConcentrationEffectHandler
// (C→S 0x76) and PlayerCancelsEffectHandler (C→S 0xF8). OpenDAoC writes every u16 here big-endian.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// S→C 0x72 SpellCastAnimation: a figure starts casting: u16 caster object id, u16 spell id (a spells.csv row; 0 does
/// nothing in retail), u16 cast time in tenths of a second (0 = instant), u16 0.
/// </summary>
public sealed record SpellCast(ushort CasterId, ushort SpellId, ushort CastTenths)
{
    public const byte Code = 0x72;

    public int CastMilliseconds => CastTenths * 100;

    public static SpellCast Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new SpellCast(r.U16BE(), r.U16BE(), r.U16BE());
    }

    public byte[] ToBytes() => new PayloadWriter().U16BE(CasterId).U16BE(SpellId).U16BE(CastTenths).U16BE(0).ToArray();
}

/// <summary>
/// S→C 0x73 InterruptSpellCast: u16 caster object id, u16 animation tag (OpenDAoC always 1: the cast animations). The
/// figure's cast stops: its cast animations, focus channel, spell effects and cast sounds.
/// </summary>
public sealed record SpellInterrupt(ushort CasterId, ushort Tag)
{
    public const byte Code = 0x73;

    public static SpellInterrupt Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new SpellInterrupt(r.U16BE(), r.U16BE());
    }

    public byte[] ToBytes() => new PayloadWriter().U16BE(CasterId).U16BE(Tag).ToArray();
}

/// <summary>
/// S→C 0x1B SpellEffectAnimation: a spell lands: u16 caster object id, u16 spell id, u16 target object id (0 none), u16 bolt
/// time in tenths (OpenDAoC: a bolt's flight; 0 = the effect's own duration), u8 pulse flag (OpenDAoC's "noSound": only
/// the pulse effect, no bolt or sound), u8 result (<see cref="Result"/>), u16 OpenDAoC writes 0xFFBF.
/// </summary>
public sealed record SpellEffect(ushort CasterId, ushort SpellId, ushort TargetId, ushort BoltTenths, bool Pulse, byte Result)
{
    public const byte Code = 0x1B;

    /// <summary>Results as retail shows them (daoc-re RE/78): 1 hit after the bolt, 0 resisted, 2 hit without buildup
    /// (radius effect when the spell has one), 3 resisted without buildup, 4 resisted, shown twice as far.</summary>
    public const byte Resisted = 0, Hit = 1, HitNoBuildup = 2, ResistedNoBuildup = 3, ResistedFar = 4;

    public bool Landed => Result is Hit or HitNoBuildup;

    public static SpellEffect Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new SpellEffect(r.U16BE(), r.U16BE(), r.U16BE(), r.U16BE(), r.U8() != 0, r.U8());
    }

    public byte[] ToBytes() =>
        new PayloadWriter().U16BE(CasterId).U16BE(SpellId).U16BE(TargetId).U16BE(BoltTenths).U8(Pulse ? (byte)1 : (byte)0)
            .U8(Result).U16BE(0xFFBF).ToArray();
}

/// <summary>One concentration buff the character keeps up: its index (retail ignores it: a drop names the row), the
/// concentration it uses, its spell (a spells.csv row, whose icon the window shows; OpenDAoC writes the effect's icon
/// number here), and its name and the name of who it's on (OpenDAoC cuts each to 12 characters + "..").</summary>
public sealed record ConcentrationEntry(byte Index, byte Concentration, ushort Spell, string Name, string Owner);

/// <summary>
/// S→C 0x75 ConcentrationList: u8 count, 3 bytes 0, then per buff u8 index, u8 0, u8 concentration, u16 spell, pascal
/// name, pascal owner. Each one replaces the whole list (count 0 when the last one ends); retail shows 50 rows at most.
/// </summary>
public sealed record ConcentrationList(IReadOnlyList<ConcentrationEntry> Entries)
{
    public const byte Code = 0x75;

    public int Used => Entries.Sum(e => e.Concentration);

    public static ConcentrationList Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        int count = r.U8();
        r.Skip(3);
        var entries = new List<ConcentrationEntry>(count);
        for (int i = 0; i < count; i++)
        {
            byte index = r.U8();
            r.Skip(1);
            entries.Add(new ConcentrationEntry(index, r.U8(), r.U16BE(), r.StringU8(), r.StringU8()));
        }
        return new ConcentrationList(entries);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8((byte)Entries.Count).Zeros(3);
        foreach (ConcentrationEntry e in Entries)
            w.U8(e.Index).U8(0).U8(e.Concentration).U16BE(e.Spell).StringU8(e.Name).StringU8(e.Owner);
        return w.ToArray();
    }
}

/// <summary>
/// One slot of the effect icons (0x7F): its place, where its icon comes from (0xFF: <see cref="Icon"/> is an icon id, else
/// it is a spells.csv row whose icon is shown), an immunity byte (bit 0 tints the icon; OpenDAoC: 1 for an immunity or a
/// disabled effect), the icon or spell, seconds left (0 = no countdown), the tooltip id (sent back by
/// <see cref="EffectCancel"/>), a negative flag and the name. An empty name clears the slot (retail, daoc-re RE/97).
/// </summary>
public sealed record EffectIcon(byte Slot, byte Source, byte Immunity, ushort Icon, ushort SecondsLeft, ushort TooltipId,
    bool Negative, string Name)
{
    public bool IsEmpty => Name.Length == 0;

    public bool IconIsSpell => Source != 0xFF;
}

/// <summary>
/// S→C 0x7F UpdateIcons (1.110+): u8 entry count, u8 ignored, u8 redraw flag (retail stores the slots but redraws
/// nothing and starts no timers when it is 0; OpenDAoC sends 1 to 1.105+ clients), u8 ignored, then the changed slots only
/// (<see cref="EffectIcon"/>: slot, source, immunity, u16 icon, u16 seconds, u16 tooltip id, u8 negative, pascal name).
/// An empty name clears its slot: OpenDAoC sends slots past the new end as the slot and 10 zero bytes. Retail shows slots
/// 0–74.
/// </summary>
public sealed record EffectIcons(IReadOnlyList<EffectIcon> Changed, bool Redraw = true)
{
    public const byte Code = 0x7F;

    public static EffectIcons Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        int count = r.U8();
        r.Skip(1);
        bool redraw = r.U8() != 0;
        r.Skip(1);
        var icons = new List<EffectIcon>(count);
        for (int i = 0; i < count; i++)
            icons.Add(new EffectIcon(r.U8(), r.U8(), r.U8(), r.U16BE(), r.U16BE(), r.U16BE(), r.U8() != 0, r.StringU8()));
        return new EffectIcons(icons, redraw);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8((byte)Changed.Count).U8(0).U8(Redraw ? (byte)1 : (byte)0).U8(0);
        foreach (EffectIcon e in Changed)
            w.U8(e.Slot).U8(e.Source).U8(e.Immunity).U16BE(e.Icon).U16BE(e.SecondsLeft).U16BE(e.TooltipId)
                .U8(e.Negative ? (byte)1 : (byte)0).StringU8(e.Name);
        return w.ToArray();
    }
}

/// <summary>C→S 0x76 RemoveConcentrationEffect: 4 bytes, the row in the concentration list (0–49), then 3 bytes retail
/// leaves unwritten (we send 0). OpenDAoC reads only the first.</summary>
public static class ConcentrationDrop
{
    public const byte Code = 0x76;

    public static byte[] Payload(byte index) => [index, 0, 0, 0];
}

/// <summary>C→S 0xF8 PlayerCancelsEffect: 4 bytes, u16 BE the effect's tooltip id from 0x7F, then 2 bytes retail leaves
/// unwritten (we send 0). OpenDAoC (1.110+) ends the character's own effect with that tooltip id.</summary>
public static class EffectCancel
{
    public const byte Code = 0xF8;

    public static byte[] Payload(ushort tooltipId) => new PayloadWriter().U16BE(tooltipId).U16BE(0).ToArray();
}
