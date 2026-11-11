// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>
/// The 0x16 skill and spell lists: the RustDAoC 1.127e capture's pages (a level 50 Paladin: 68 usable skills in
/// two pages, no list spells, 14 crafting skills, weapon stats), and a hand-built spell line in OpenDAoC's
/// layout. Each page rebuilds byte for byte; the skill book joins the pages into one list.
/// </summary>
public class SkillPacketTests
{
    private static readonly List<ServerPacket> Server = CaptureFile.Login1127e().ServerPackets();

    private static List<ServerPacket> Sub(byte subcode) =>
        Server.Where(p => p.Code == 0x16 && p.Payload.Length > 0 && p.Payload[0] == subcode).ToList();

    [Fact]
    public void TheSkillListComesInPagesThatRebuild()
    {
        List<ServerPacket> pages = Sub(SkillListPage.Subcode);
        Assert.Equal(2, pages.Count);
        SkillListPage first = SkillListPage.Parse(pages[0].Payload), last = SkillListPage.Parse(pages[1].Payload);
        Assert.Equal(((byte)0, true, 66), (first.FirstIndex, first.MoreFollow, first.Entries.Count));
        Assert.Equal(((byte)66, false, 2), (last.FirstIndex, last.MoreFollow, last.Entries.Count));
        Assert.Equal(pages[0].Payload, first.ToBytes());
        Assert.Equal(pages[1].Payload, last.ToBytes());

        // The list opens with the specialisations: Crush at level 1 with +14 from items.
        Assert.Equal(new SkillEntry(1, 35, SkillPage.Specialization, 0, 14, 0, "Crush"), first.Entries[0]);
        // A style: Bone Breaker needs Two Handed 29 and follows another style (high byte: that style's index + 100).
        SkillEntry bone = first.Entries.Single(e => e.Name == "Bone Breaker");
        Assert.Equal((SkillPage.Style, (byte)29, (ushort)111), (bone.Page, bone.Level, bone.Icon));
        Assert.True(bone.Special >> 8 >= 100);
        Assert.Contains(first.Entries, e => e.Page == SkillPage.Spell);          // a Paladin's chants
        Assert.Contains(first.Entries, e => e.Page == SkillPage.RealmAbility);
    }

    [Fact]
    public void TheBookJoinsThePagesAndStartsOverAtIndexZero()
    {
        var book = new SkillBook();
        List<SkillListPage> pages = Sub(SkillListPage.Subcode).Select(p => SkillListPage.Parse(p.Payload)).ToList();
        Assert.False(book.Add(pages[0]));
        Assert.Empty(book.Skills);
        Assert.True(book.Add(pages[1]));
        Assert.Equal(68, book.Skills.Count);
        Assert.Equal("Smash", book.Skills[^1].Name);

        // Sent again (a skill changed): the new list replaces the old, not added to it.
        book.Add(pages[0]);
        book.Add(pages[1]);
        Assert.Equal(68, book.Skills.Count);

        // A new list starting before the old one finished: the half-built one is dropped, the new one completes.
        book.Add(pages[0]);
        book.Add(pages[0]);
        Assert.True(book.Add(pages[1]));
        Assert.Equal(68, book.Skills.Count);

        // A page that doesn't follow on (one lost) isn't joined to the wrong list.
        Assert.False(book.Add(pages[1]));
        Assert.Equal(68, book.Skills.Count);
    }

    [Fact]
    public void TheRecordedPaladinsStylesDescribeTheirOpenings()
    {
        var book = new SkillBook();
        foreach (ServerPacket p in Sub(SkillListPage.Subcode))
            book.Add(SkillListPage.Parse(p.Payload));
        List<SkillEntry> styles = book.Skills.Where(e => e.Page == SkillPage.Style).ToList();
        Assert.NotEmpty(styles);
        List<DaocProtocol.World.SkillInfo> described = styles.Select(e => book.Describe(e.Name)!).ToList();
        Assert.All(described, (d, i) => Assert.Equal((DaocProtocol.World.SkillKind.Style, styles[i].Id), (d.Kind, d.Id)));
        // A follow-up names a style the Paladin has (the server's index from the first non-specialisation entry).
        var chained = described.Where(d => d.Opening?.Label == "Previous Style Req.").ToList();
        Assert.NotEmpty(chained);
        Assert.All(chained, d => Assert.Contains(d.Opening!.Value, styles.Select(s => s.Name)));
        Assert.Null(book.Describe(book.Skills[0].Name));   // a specialisation: no item window
    }

    [Fact]
    public void ASpellLineReadsItsSpellsAndTheFooterEndsTheLines()
    {
        // OpenDAoC's layout for a line "Rejuvenation" (id 0x0102) with a level 5 spell and a level 12 ability.
        byte[] line =
        [
            2, 3, 2, 0,                                       // subcode, 2 entries + the line, subtype 2, line 0
            0, 0, 0x01, 0x02, 0, 0, 12, .."Rejuvenation"u8,   // level word 0, id, icon 0, name
            5, 0, 0x03, 0xE8, 0x00, 0x2A, 4, .."Heal"u8,      // level 5 (LE word), spell id 1000, icon 42
            12, 1, 0x00, 0x07, 0x00, 0x09, 3, .."Cut"u8,      // level 12, high byte 1: an ability
        ];
        SpellLinePage page = SpellLinePage.Parse(line);
        Assert.False(page.IsFooter);
        Assert.Equal(((ushort)0x0102, "Rejuvenation"), (page.LineId, page.LineName));
        Assert.Equal(new LineEntry(5, LineEntryKind.Spell, 1000, 42, "Heal"), page.Entries[0]);
        Assert.Equal(new LineEntry(12, LineEntryKind.Ability, 7, 9, "Cut"), page.Entries[1]);
        Assert.Equal(line, page.ToBytes());

        // The capture's Paladin has no list spells: only the footer.
        ServerPacket footer = Sub(SpellLinePage.Subcode).Single();
        Assert.True(SpellLinePage.Parse(footer.Payload).IsFooter);
        Assert.Equal(footer.Payload, SpellLinePage.Parse(footer.Payload).ToBytes());

        var book = new SkillBook();
        Assert.False(book.Add(page));
        Assert.True(book.Add(SpellLinePage.Parse(footer.Payload)));
        Assert.Equal("Heal", Assert.Single(book.SpellLines).Entries[0].Name);
    }

    [Fact]
    public void WeaponStatsAndCraftingSkillsRead()
    {
        ServerPacket w = Sub(WeaponStats.Subcode)[0];
        WeaponStats s = WeaponStats.Parse(w.Payload);
        Assert.Equal((14.85, (ushort)802, (ushort)68), (s.WeaponDamage, s.WeaponSkill, s.ArmorFactor));
        Assert.Equal(w.Payload, s.ToBytes());

        ServerPacket c = Sub(CraftingSkills.Subcode).Single();
        CraftingSkills crafts = CraftingSkills.Parse(c.Payload);
        Assert.Equal(14, crafts.Skills.Count);
        Assert.Equal(new CraftingSkill(1, 1, "Weaponcraft"), crafts.Skills[0]);
        Assert.Equal(c.Payload, crafts.ToBytes());
    }

    [Fact]
    public void TheBridgeRaisesTheWholeListOnce()
    {
        var bridge = new NetworkBridge(_ => { });
        int raised = 0;
        IReadOnlyList<SkillEntry>? skills = null;
        bridge.SkillsChanged += l => { raised++; skills = l; };
        foreach (ServerPacket p in Sub(SkillListPage.Subcode))
            bridge.Dispatch(p);
        Assert.Equal(1, raised);
        Assert.Equal(68, skills?.Count);
    }
}
