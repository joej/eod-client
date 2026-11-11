// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;

namespace DaocProtocol.Tests;

/// <summary>
/// Spell and style details: the S→C 0xD8 record (checked against the one in the 1.127e capture, the answer to its one
/// C→S 0xD8), a style record as OpenDAoC's SpellDelve.GetStyleString writes one, the opening words from a 0x16 style entry,
/// and the text the windows show.
/// </summary>
public class DelveTests
{
    private static readonly ServerPacket Recorded = CaptureFile.Login1127e().ServerPackets().Single(p => p.Code == DelveRecord.Code);

    [Fact]
    public void TheRecordedSpellRecordReadsAndRebuilds()
    {
        DelveRecord r = DelveRecord.Parse(Recorded.Payload);
        Assert.Equal("Spell", r.Kind);
        Assert.Equal((ushort)30125, r.Index);   // the id the capture's request asked for
        Assert.Equal("Tornado's Dance", r.Name);
        Assert.Equal(1, r.Int("instant"));
        Assert.Equal("Increases Dexterity and Quickness by 61 until death.\nIcon ID: 5075", r.Get("delve_string"));
        Assert.Equal(Recorded.Payload, r.ToBytes());
    }

    [Fact]
    public void TheRecordedSpellsText()
    {
        DelveRecord r = DelveRecord.Parse(Recorded.Payload);
        Assert.Equal(["Level: 1", "Target: Self", "Casting time: Instant", "", "Increases Dexterity and Quickness by 61 until death.", "Icon ID: 5075"],
            DelveText.Spell(r).Select(l => l.ToString()));
        var info = new SkillInfo(SkillKind.Spell, 30125, "Tornado's Dance", 1, 0);
        Assert.Equal(["Tornado's Dance", "Magical Ability", "1st Level", "", "", "", "", ""], DelveText.MiniInfo(info, null));
        Assert.Equal("Target: Self", DelveText.MiniInfo(info, r)[3]);
    }

    [Fact]
    public void ASpellWithCostRangeAndConcentration()
    {
        DelveRecord r = DelveRecord.Parse("(Spell (Function \"light\")(Index \"1234\")(Name \"Blast\")(cast_timer \"1000\")(damage_type \"10\")(level \"12\")" +
            "(power_cost \"-5\")(range \"1500\")(duration \"75\")(dur_type \"2\")(timer_value \"30\")(frequency \"2499\")(target \"1\")(radius \"350\")(concentration_points \"4\"))");
        Assert.Equal(["Level: 12", "Duration: 1 minute 15 seconds every 2.5 sec", "Concentration cost: 4", "Power cost: 5%", "Target: Targeted area effect",
                "Range: 1500", "Area radius: 350", "Casting time: 3.0 seconds", "Recast: 30 seconds", "Magic Type: Heat"],
            DelveText.Spell(r).Select(l => l.ToString()));
        Assert.Equal("4 Conc. Pts", DelveText.MiniInfo(new SkillInfo(SkillKind.Spell, 1234, "Blast", 12, 0), r)[4]);
        // Range 0 with a radius: point-blank.
        Assert.Equal("Point-blank Area", DelveText.TargetWord(DelveRecord.Parse("(Spell (target \"1\")(radius \"350\"))")));
    }

    [Fact]
    public void AStyleRecordAsTheServerWritesIt()
    {
        // Index as unchecked((short) InternalID); 0-valued and empty keys are left out.
        DelveRecord r = DelveRecord.Parse("(Style (Index \"-3\")(OpeningStyle \"Bash\")(FollowupStyle \"Crush, Smash\")(Name \"Slam\")(Icon \"201\")" +
            "(Level \"8\")(Fatigue \"7\")(AttackMod \"-12\")(OpeningDamage \"120\")(Weapon \"Crushing\")(SpecialNumber \"4321\")(SpecialType \"1\"))");
        Assert.Equal((ushort)0xFFFD, r.Index);
        DelveRecord stun = DelveRecord.Parse("(Spell (Index \"4321\")(Name \"Stun\")(instant \"1\")(duration \"5\")(dur_type \"2\"))");
        List<DelveLine> lines = DelveText.Style(r, id => id == 4321 ? stun : null);
        Assert.Equal(["Opening: Bash", "Weapon Type: Crushing", "Follow-up Style(s): Crush, Smash", "Endurance Cost: Medium", "Damage: Medium",
                "To Hit: Medium Penalty", "Defense: No Bonus", "", "Special: Stun", "Duration: 5 seconds", "Target: Self", "Casting time: Instant"],
            lines.Select(l => l.ToString()));
        Assert.Equal(((byte)254, (byte)131, (byte)49), lines[5].Colour);
        Assert.Equal("Special: a spell effect", DelveText.Style(r, _ => null).Last().ToString());
    }

    [Theory]
    [InlineData(45, "45 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "1 minute 30 seconds")]
    [InlineData(121, "2 minutes 1 second")]
    public void TimesAreWrittenAsTheClientDoes(int seconds, string expected) => Assert.Equal(expected, DelveText.Time(seconds));

    [Theory]
    [InlineData((ushort)0x100, null)]
    [InlineData((ushort)2, "Opening: You Hit")]
    [InlineData((ushort)103, "Opening: You Parry")]
    [InlineData((ushort)200, "Positional: Behind target")]
    [InlineData((ushort)202, "Positional: In front of target")]
    [InlineData((ushort)(7 | 102 << 8), "Previous Style Req.: Second")]
    public void AStylesOpeningFromItsEntry(ushort special, string? expected)
    {
        string[] styles = ["Zeroth", "First", "Second"];
        StyleOpening? o = DelveText.Opening(special, i => i >= 0 && i < styles.Length ? styles[i] : null);
        Assert.Equal(expected, o is null ? null : $"{o.Label}: {o.Value}");
    }

    [Fact]
    public void AStylesSmallWindowLines()
    {
        var after = new SkillInfo(SkillKind.Style, 7, "Slam", 8, 0, new StyleOpening("Previous Style Req.", "Bash"));
        Assert.Equal(["Slam", "Combat Style", "8th Level", "Previous Style Req.", "Bash", "", "", ""], DelveText.MiniInfo(after, null));
        var side = after with { Opening = new StyleOpening("Positional", "To side of target") };
        Assert.Equal("Positional: To side of target", DelveText.MiniInfo(side, null)[3]);
        Assert.Equal(["11th", "12th", "13th", "21st", "22nd", "23rd", "101st"], new[] { 11, 12, 13, 21, 22, 23, 101 }.Select(DelveText.Ordinal));
    }

    [Fact]
    public void TheBridgeKeepsRecordsInTheSharedCache()
    {
        var cache = new DelveCache();
        var bridge = new NetworkBridge(delves: cache);
        DelveRecord? seen = null;
        bridge.DelveArrived += r => seen = r;
        bridge.Dispatch(Recorded);
        Assert.Equal("Tornado's Dance", seen?.Name);
        Assert.Same(seen, new NetworkBridge(delves: cache).Delves.Get("Spell", 30125));
        Assert.True(cache.ShouldAsk(DetailRequest.SpellDelve, 30125));
        Assert.False(cache.ShouldAsk(DetailRequest.SpellDelve, 30125));
        Assert.Throws<InvalidDataException>(() => DelveRecord.Parse("(Spell (Name \"x)"));
    }
}
