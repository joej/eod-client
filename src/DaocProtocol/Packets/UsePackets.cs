// SPDX-License-Identifier: GPL-3.0-or-later
//
// Using skills, spells and quickbar slots, and their cooldowns (roadmap M9: quickbars). Layouts from
// OpenDAoC-Core @f0aaa1e, GPL-3.0 (field order only): UseSkillHandler (C→S 0xBB), UseSpellHandler (0x7D),
// UseSlotHandler (0x71), PacketLib168.SendDisableSkill (S→C 0xD6). Retail 1.127 (a recorded session against
// our server, 2026-11-05) sends UseSlot as 24 bytes: the handlers' 22 plus two zero bytes; the flag word was
// 0 in every sample (no target), and the heading carries no on-ground bit (unlike 0xA9's).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// The three "use" requests share one layout on 1.124+: x, y, z, speed (f32 little-endian, as in 0xA9),
/// heading u16 BE (0–4095), a flag word u16 BE, two bytes saying what to use, then two zero bytes.
/// </summary>
public static class UseRequest
{
    /// <summary>C→S 0x71: a quickbar or inventory slot (slot, type).</summary>
    public const byte SlotCode = 0x71;
    /// <summary>C→S 0xBB: a skill from the usable-skill list (index, type: 0 a specialisation, indexed in the
    /// whole list; otherwise indexed from the first entry that isn't one).</summary>
    public const byte SkillCode = 0xBB;
    /// <summary>C→S 0x7D: a list spell (its level, its line's number), or a style or ability listed in a line.</summary>
    public const byte SpellCode = 0x7D;

    /// <summary>Flag word bits as OpenDAoC reads them: strafing, a target in view (0x2000 or 0x8000), a ground
    /// target in view. What retail sets with a target is unconfirmed (open question C12).</summary>
    public const ushort Strafing = 0x4000, TargetInView = 0x2000, GroundTargetInView = 0x1000;

    public static byte[] Payload(float x, float y, float z, float speed, ushort heading, ushort flags, byte first, byte second) =>
        new PayloadWriter().F32LE(x).F32LE(y).F32LE(z).F32LE(speed).U16BE((ushort)(heading & 0x0FFF)).U16BE(flags)
            .U8(first).U8(second).Zeros(2).ToArray();
}

/// <summary>
/// S→C 0xD6 DisableSkills: skills or list spells that can't be used for a while (a cooldown starting, or 0
/// seconds: usable again). Head: u16 BE seconds (list spells; unused for skills), count u8, kind u8.
/// Kind 1 (skills): count × (u16 BE index from the first non-specialisation entry, u16 BE seconds). Kind 2
/// (list spells, one packet per duration): count × (u8 line number, u8 index of the entry in that line).
/// </summary>
public sealed record DisableSkills(IReadOnlyList<(ushort Index, ushort Seconds)> Skills,
    IReadOnlyList<(byte Line, byte Entry)> Spells, ushort SpellSeconds)
{
    public const byte Code = 0xD6;
    private const byte SkillKind = 1, SpellKind = 2;

    public static DisableSkills Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        ushort seconds = r.U16BE();
        int count = r.U8();
        byte kind = r.U8();
        var skills = new List<(ushort, ushort)>();
        var spells = new List<(byte, byte)>();
        for (int k = 0; k < count; k++)
        {
            if (kind == SkillKind)
                skills.Add((r.U16BE(), r.U16BE()));
            else if (kind == SpellKind)
                spells.Add((r.U8(), r.U8()));
            else
                throw new InvalidDataException($"0xD6 kind {kind} is not known.");
        }
        return new DisableSkills(skills, spells, kind == SpellKind ? seconds : (ushort)0);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter();
        if (Skills.Count > 0)
        {
            w.U16BE(0).U8((byte)Skills.Count).U8(SkillKind);
            foreach ((ushort index, ushort seconds) in Skills)
                w.U16BE(index).U16BE(seconds);
        }
        else
        {
            w.U16BE(SpellSeconds).U8((byte)Spells.Count).U8(SpellKind);
            foreach ((byte line, byte entry) in Spells)
                w.U8(line).U8(entry);
        }
        return w.ToArray();
    }
}
