// SPDX-License-Identifier: GPL-3.0-or-later
//
// Retail's chat routing (ChatRules): tabs, colours, the middle of the screen, the sender of a player's line, and what
// a typed line sends.

using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

public class ChatRulesTests
{
    [Theory]
    [InlineData(ChatType.Unspecified, false, ChatType.System)]
    [InlineData(ChatType.Help, true, ChatType.Say)]
    [InlineData(ChatType.Help, false, ChatType.Help)]
    [InlineData(ChatType.ScreenCenterAndSystem, false, ChatType.Say)]
    [InlineData(ChatType.ScreenCenterSmallerAndSystem, false, ChatType.Say)]
    [InlineData(ChatType.Group, true, ChatType.Group)]
    public void ALineIsShownAsItsRetailType(ChatType type, bool fromPlayer, ChatType shown) =>
        Assert.Equal(shown, ChatRules.ShownAs(new ChatMessage(type, "text", FromPlayer: fromPlayer)));

    [Theory]
    [InlineData(ChatType.ScreenCenter)]
    [InlineData(ChatType.ScreenCenterSmaller)]
    [InlineData(ChatType.SocialInterface)]
    public void MiddleOfScreenAndSocialRecordsAreNotChatLines(ChatType type) =>
        Assert.Null(ChatRules.ShownAs(new ChatMessage(type, "text")));

    [Theory]
    [InlineData(ChatType.ScreenCenter, CentreSize.Large)]
    [InlineData(ChatType.ScreenCenterAndSystem, CentreSize.Large)]
    [InlineData(ChatType.ScreenCenterSmaller, CentreSize.Small)]
    [InlineData(ChatType.ScreenCenterSmallerAndSystem, CentreSize.Small)]
    public void ScreenCentreTypesGoToTheMiddle(ChatType type, CentreSize size) =>
        Assert.Equal(size, ChatRules.Centre(new ChatMessage(type, "text")));

    [Fact]
    public void SystemLinesAreInCombatNotMainAndStaffIsEverywhere()
    {
        Assert.False(ChatRules.Main.Has(ChatType.System));
        Assert.True(ChatRules.Combat.Has(ChatType.System));
        Assert.True(ChatRules.Main.Has(ChatType.Staff) && ChatRules.Combat.Has(ChatType.Staff));
        Assert.False(ChatRules.Main.Has(ChatType.OthersCombat) || ChatRules.Combat.Has(ChatType.OthersCombat));
        Assert.False(ChatRules.Main.Has(ChatType.SpellPulse) || ChatRules.Combat.Has(ChatType.SpellPulse));
    }

    [Fact]
    public void CombatTabRecoloursAllianceBattleGroupAndTrade()
    {
        Assert.Equal(7, ChatRules.Main.ColourIndex(ChatType.Alliance));
        Assert.Equal(20, ChatRules.Combat.ColourIndex(ChatType.Alliance));
        Assert.Equal(24, ChatRules.Combat.ColourIndex(ChatType.Trade));
        Assert.Equal((255, 96, 0), ChatRules.Palette[ChatRules.Main.ColourIndex(ChatType.Group)]);
        Assert.Equal(26, ChatRules.Palette.Length);
    }

    [Theory]
    [InlineData(ChatType.Say, "Quitcheck says, \"hello there\"", "Quitcheck")]
    [InlineData(ChatType.Send, "Testjoe sends, \"psst\"", "Testjoe")]
    [InlineData(ChatType.Group, "[Party] Testjoe: on my way", "Testjoe")]
    [InlineData(ChatType.Guild, "[Guild] Eodarms: hi", "Eodarms")]
    public void TheSenderOfAPlayersLine(ChatType type, string text, string sender) =>
        Assert.Equal(sender, ChatRules.Sender(new ChatMessage(type, text, FromPlayer: true)));

    [Fact]
    public void ALineNotFromAPlayerHasNoSender() =>
        Assert.Null(ChatRules.Sender(new ChatMessage(ChatType.Say, "The guard says, \"halt\"")));

    [Theory]
    [InlineData("hello", ChatMode.Say, "/s hello", ChatMode.Say)]
    [InlineData("hello", ChatMode.Group, "/g hello", ChatMode.Group)]
    [InlineData("/g ready", ChatMode.Say, "/g ready", ChatMode.Group)]
    [InlineData("/guild hi all", ChatMode.Say, "/gu hi all", ChatMode.Guild)]
    [InlineData("/s back to say", ChatMode.Guild, "/s back to say", ChatMode.Say)]
    [InlineData("/who", ChatMode.Group, "/who", ChatMode.Group)]
    [InlineData("/g", ChatMode.Say, "/g", ChatMode.Say)]          // no space after it: not a mode switch
    [InlineData("100% sure", ChatMode.Say, "/s 100* sure", ChatMode.Say)]
    [InlineData("attack %t now", ChatMode.Group, "/g attack Grass Snake now", ChatMode.Group)]
    public void TypedLines(string line, ChatMode mode, string sent, ChatMode after)
    {
        (string? send, ChatMode now) = ChatRules.Typed(line, mode, "Grass Snake");
        Assert.Equal(sent, send);
        Assert.Equal(after, now);
    }

    [Fact]
    public void ASwitchWithNoTextSendsNothingButKeepsTheMode()
    {
        (string? send, ChatMode now) = ChatRules.Typed("/g ", ChatMode.Say, null);
        Assert.Null(send);
        Assert.Equal(ChatMode.Group, now);
    }

    [Fact]
    public void ThePlayerMarkerIsStrippedAndRemembered()
    {
        ChatMessage m = ChatMessage.Parse([(byte)ChatType.Send, .. "@@Testjoe sends, \"hi\""u8, 0]);
        Assert.True(m.FromPlayer);
        Assert.Equal("Testjoe sends, \"hi\"", m.Text);
    }
}
