// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>
/// Chat and system messages (roadmap M6): the 0xAF Message packet, checked against the recorded
/// 1.127e session. The capture has no player /say in it (it's a login-to-world trace), but it
/// does carry the server's startup/MOTD/guild lines, a zone-entry system line, the big centred
/// zone banner, and spell narration — enough to pin the layout and the chat/UI-control split.
/// </summary>
public class ChatPacketTests
{
    private static readonly CaptureFile Capture = CaptureFile.Login1127e();
    private static readonly List<ServerPacket> Server = Capture.ServerPackets();

    private delegate T SpanParser<out T>(ReadOnlySpan<byte> payload);

    private static List<T> ParseAll<T>(byte code, SpanParser<T> parse)
    {
        var result = new List<T>();
        foreach (ServerPacket p in Server)
            if (p.Code == code)
                result.Add(parse(p.Payload));
        return result;
    }

    [Fact]
    public void EveryMessagePacketParses()
    {
        List<ChatMessage> all = ParseAll(ChatMessage.Code, ChatMessage.Parse);
        Assert.NotEmpty(all);
        // Every chat type seen in the capture is a defined enum value (no stray bytes misread).
        Assert.All(all, m => Assert.True(Enum.IsDefined(m.Type), $"undefined chat type 0x{(byte)m.Type:X2}"));
    }

    [Fact]
    public void DecodesTheGuildMotdSystemLines()
    {
        List<ChatMessage> all = ParseAll(ChatMessage.Code, ChatMessage.Parse);

        // The server's startup lines arrive as type 0x00 with plain text.
        Assert.Contains(all, m => m.Text.StartsWith("Guild Message:", StringComparison.Ordinal));
        Assert.Contains(all, m => m.Text == "You have entered Camelot Hills.");
        Assert.Contains(all, m => m.Text.Contains("Dawn of Light", StringComparison.Ordinal));
    }

    [Fact]
    public void ZoneBannerIsScreenCenterAndExcludedFromChatLog()
    {
        List<ChatMessage> all = ParseAll(ChatMessage.Code, ChatMessage.Parse);

        // "Camelot Hills" arrives twice: once as a system line (in the log) and once as the big
        // centred banner (ScreenCenterSmaller, 0xc9) which must NOT go to the chat log.
        ChatMessage banner = Assert.Single(all, m => m.Type == ChatType.ScreenCenterSmaller);
        Assert.Equal("Camelot Hills", banner.Text);
        Assert.False(banner.IsPlayerVisibleChat, "the centred zone banner is UI, not a chat line");
    }

    [Fact]
    public void AnNpcsWordsToThePlayerKeepThePopupMark()
    {
        // OpenDAoC 1.127 (PacketLib1127.SendRawMessage): an NPC's SayTo goes to the pop-up window, written
        // as "##" before the text; a tell is "@@". Both are stripped; only the first is a pop-up.
        byte[] said = [0x12, .. System.Text.Encoding.Latin1.GetBytes("##Master Visur says, \"Greetings. [Camelot City]\""), 0];
        ChatMessage npc = ChatMessage.Parse(said);
        Assert.True(npc.Popup);
        Assert.Equal("Master Visur says, \"Greetings. [Camelot City]\"", npc.Text);

        ChatMessage tell = ChatMessage.Parse([(byte)ChatType.Send, .. System.Text.Encoding.Latin1.GetBytes("@@Bob sends, \"hi\""), 0]);
        Assert.False(tell.Popup);
        Assert.Equal("Bob sends, \"hi\"", tell.Text);
    }

    [Fact]
    public void SocialInterfaceControlStringsAreNotChat()
    {
        // Type 0x64 carries structured UI strings like "I,1,0,0,..." / "TE,1,0,0" for the guild and
        // social windows. They must be classified out of the chat log, text or not.
        List<ChatMessage> social = ParseAll(ChatMessage.Code, ChatMessage.Parse)
            .Where(m => m.Type == ChatType.SocialInterface).ToList();
        Assert.NotEmpty(social);
        Assert.All(social, m => Assert.False(m.IsPlayerVisibleChat));
    }

    [Fact]
    public void SpellNarrationIsVisibleChat()
    {
        // Spell / spell-pulse lines are the game narrating and DO belong in the log. (The capture
        // casts Virtuous Might more than once, so match all, not a single.)
        List<ChatMessage> spells = ParseAll(ChatMessage.Code, ChatMessage.Parse)
            .Where(m => m.Text == "You cast a Virtuous Might spell!").ToList();
        Assert.NotEmpty(spells);
        Assert.All(spells, s =>
        {
            Assert.Equal(ChatType.Spell, s.Type);
            Assert.True(s.IsPlayerVisibleChat);
        });
    }

    [Fact]
    public void ParseStripsTheSendPrefix()
    {
        // A tell (CT_Send) is written "@@<text>"; the prefix is a client-routing marker, not text.
        byte[] payload = [(byte)ChatType.Send, (byte)'@', (byte)'@', (byte)'h', (byte)'i', 0x00];
        ChatMessage m = ChatMessage.Parse(payload);
        Assert.Equal(ChatType.Send, m.Type);
        Assert.Equal("hi", m.Text);
    }

    [Fact]
    public void SayCommandProducesTheWireBytesTheServerReads()
    {
        // "say" chat rides the same 0xAF command channel as "&quit": lead 0x00, "&say <text>", NUL.
        // This is what ClientSession.SayAsync sends; the server's PlayerCommandHandler skips the
        // lead byte (1.127: no 7-byte skip) then ReadString to "say hello".
        byte[] payload = WorldPackets_CommandRequest("&say hello");
        Assert.Equal((byte)0x00, payload[0]);
        Assert.Equal((byte)0x00, payload[^1]);
        string text = System.Text.Encoding.Latin1.GetString(payload, 1, payload.Length - 2);
        Assert.Equal("&say hello", text);
    }

    // CommandRequest lives in the protocol library; call it through its public API.
    private static byte[] WorldPackets_CommandRequest(string command) =>
        DaocProtocol.Packets.CommandRequest.Payload(command);
}
