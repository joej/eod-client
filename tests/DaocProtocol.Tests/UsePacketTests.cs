// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>
/// Using skills, spells and slots (0xBB, 0x7D, 0x71) and their cooldowns (0xD6). The use layout is checked
/// against a retail 1.127 UseSlot recorded against our server (2026-11-05); the skill lookups and cooldowns
/// against the RustDAoC capture's skill list (a level 50 Paladin).
/// </summary>
public class UsePacketTests
{
    [Fact]
    public void AUseRequestMatchesRetailsBytes()
    {
        // Retail's UseSlot of slot 10: x, y, z, speed 191, heading 0x08D0, flags 0, slot, type 0, two zeros.
        byte[] retail = Convert.FromHexString("b6e60b49bf6c0149a094354500003f4308d000000a000000");
        var r = new PayloadReader(retail);
        float x = r.F32LE(), y = r.F32LE(), z = r.F32LE(), speed = r.F32LE();
        Assert.Equal(191f, speed);
        // The heading as 0xA9 carries it (0x1000: on the ground) goes out without that bit.
        Assert.Equal(retail, UseRequest.Payload(x, y, z, speed, 0x18D0, 0, 10, 0));
    }

    [Fact]
    public void CooldownsReadSkillsAndListSpells()
    {
        // Skills: two entries (index, seconds); the head's duration is unused.
        byte[] skills = [0, 0, 2, 1, 0x00, 0x05, 0x00, 0x1E, 0x00, 0x07, 0x00, 0x00];
        DisableSkills s = DisableSkills.Parse(skills);
        Assert.Equal([((ushort)5, (ushort)30), ((ushort)7, (ushort)0)], s.Skills);
        Assert.Equal(skills, s.ToBytes());

        // List spells: one duration for the packet, then (line, entry) pairs.
        byte[] spells = [0x00, 0x0F, 2, 2, 0, 3, 1, 0];
        DisableSkills l = DisableSkills.Parse(spells);
        Assert.Equal((ushort)15, l.SpellSeconds);
        Assert.Equal([((byte)0, (byte)3), ((byte)1, (byte)0)], l.Spells);
        Assert.Equal(spells, l.ToBytes());
    }

    private static SkillBook PaladinBook()
    {
        var book = new SkillBook();
        foreach (ServerPacket p in CaptureFile.Login1127e().ServerPackets()
                     .Where(p => p.Code == 0x16 && p.Payload[0] == SkillListPage.Subcode))
            book.Add(SkillListPage.Parse(p.Payload));
        return book;
    }

    [Fact]
    public void ASkillIsFoundByNameAndIndexedAsTheServerCountsIt()
    {
        SkillBook book = PaladinBook();
        int specs = book.Skills.TakeWhile(s => s.Page == SkillPage.Specialization).Count();
        Assert.Equal(7, specs);

        // A specialisation: its index in the whole list, type 0.
        Assert.Equal(new UseTarget(UseRequest.SkillCode, 0, 0, "Crush"), book.Find("crush"));
        // Anything else: counted from the first non-specialisation, its page as the type.
        int bone = book.Skills.ToList().FindIndex(e => e.Name == "Bone Breaker");
        Assert.Equal(new UseTarget(UseRequest.SkillCode, (byte)(bone - specs), (byte)SkillPage.Style, "Bone Breaker"),
            book.Find("Bone Breaker"));
        Assert.Null(book.Find("No Such Skill"));
    }

    [Fact]
    public void AListSpellIsFoundByLevelAndLine()
    {
        var book = new SkillBook();
        book.Add(new SpellLinePage(0, false, 1, "Path of Fire", [new LineEntry(1, LineEntryKind.Spell, 100, 1, "Fire Bolt")]));
        book.Add(new SpellLinePage(1, false, 2, "Path of Ice", [new LineEntry(5, LineEntryKind.Spell, 200, 2, "Chill")]));
        book.Add(new SpellLinePage(0, true, 0, "", []));
        Assert.Equal(new UseTarget(UseRequest.SpellCode, 5, 1, "Chill"), book.Find("Chill"));

        // A cooldown on line 1, entry 0, for 10 s.
        book.Apply(new DisableSkills([], [(1, 0)], 10), nowMs: 1_000);
        Assert.Equal(10, book.CooldownLeft("Chill", 1_000));
        Assert.Equal(4, book.CooldownLeft("Chill", 7_000));
        Assert.Equal(0, book.CooldownLeft("Fire Bolt", 1_000));
    }

    [Fact]
    public void ASkillCooldownOutlastsTheListBeingSentAgain()
    {
        SkillBook book = PaladinBook();
        UseTarget bone = book.Find("Bone Breaker")!.Value;
        book.Apply(new DisableSkills([(bone.First, 8)], [], 0), nowMs: 0);
        Assert.Equal(8, book.CooldownLeft("Bone Breaker", 0));

        // The server resends the list (it does, twice at world entry): the cooldown still applies.
        foreach (ServerPacket p in CaptureFile.Login1127e().ServerPackets()
                     .Where(p => p.Code == 0x16 && p.Payload[0] == SkillListPage.Subcode))
            book.Add(SkillListPage.Parse(p.Payload));
        Assert.Equal(5, book.CooldownLeft("Bone Breaker", 3_000));
        // 0 seconds: usable again.
        book.Apply(new DisableSkills([(bone.First, 0)], [], 0), nowMs: 3_000);
        Assert.Equal(0, book.CooldownLeft("Bone Breaker", 3_000));
    }
}
