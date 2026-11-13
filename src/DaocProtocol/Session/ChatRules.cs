// SPDX-License-Identifier: GPL-3.0-or-later
//
// How retail's client routes chat (daoc-re RE/76, RE/82, RE/85: read from the client and its default.ini, not checked
// in play): which line goes to which tab, in which of the 26 palette colours, what reaches the middle of the screen,
// and what a typed line sends. No Godot here: the chat window (godot WorldView.ChatWindow) draws what this decides.

using DaocProtocol.Packets;

namespace DaocProtocol.Session;

/// <summary>The channel plain typed text goes to: retail's chat mode.</summary>
public enum ChatMode { Say, Group, Guild, Broadcast, Chat, Officer, Alliance, BattleGroup }

/// <summary>Text for the middle of the screen: large (0xC8, 0xCA) or small (0xC9, 0xCB).</summary>
public enum CentreSize { Large, Small }

/// <summary>A chat tab: the types it shows, colour indexes that differ from the defaults, its window's opacity and its
/// default channel.</summary>
public sealed record ChatTab(string Name, IReadOnlySet<ChatType> Shows, IReadOnlyDictionary<ChatType, int> Colours,
    float Opacity, ChatMode Channel)
{
    public bool Has(ChatType type) => type == ChatType.Staff || Shows.Contains(type);   // Staff is always shown

    /// <summary>The palette index for <paramref name="type"/> in this tab.</summary>
    public int ColourIndex(ChatType type) => Colours.TryGetValue(type, out int i) ? i : ChatRules.DefaultColour(type);
}

public static class ChatRules
{
    /// <summary>The 26-colour chat palette (shared with the con colours).</summary>
    public static readonly (byte R, byte G, byte B)[] Palette =
    [
        (255, 255, 255), (245, 0, 0), (255, 255, 0), (99, 99, 251), (156, 255, 133), (254, 131, 49), (147, 145, 145),
        (210, 127, 252), (0, 254, 250), (25, 180, 25), (240, 144, 107), (185, 46, 46), (46, 185, 82), (46, 144, 185),
        (255, 245, 104), (255, 180, 0), (171, 141, 0), (255, 96, 0), (172, 211, 115), (198, 156, 109), (199, 178, 153),
        (122, 204, 200), (0, 169, 157), (245, 152, 157), (189, 140, 191), (6, 5, 5),
    ];

    /// <summary>Retail's default palette index per type.</summary>
    public static int DefaultColour(ChatType type) => type switch
    {
        ChatType.Say => 0, ChatType.Send => 8, ChatType.Group => 17, ChatType.Guild => 4, ChatType.Broadcast => 3,
        ChatType.Emote => 6, ChatType.Help => 2, ChatType.Chat => 9, ChatType.Advise => 8, ChatType.Officer => 0,
        ChatType.Alliance => 7, ChatType.BattleGroup => 1, ChatType.BattleGroupLeader => 14, ChatType.Staff => 2,
        ChatType.LookingForGroup => 23, ChatType.Trade => 21,
        ChatType.Spell => 3, ChatType.YouHit => 17, ChatType.System => 0, ChatType.Items => 2, ChatType.Merchant => 10,
        ChatType.YourDeath => 1, ChatType.OthersDeath => 10, ChatType.OthersCombat => 6, ChatType.ResistsChanged => 2,
        ChatType.SpellExpires => 9, ChatType.Loot => 4, ChatType.SpellResisted => 7, ChatType.Important => 17,
        ChatType.Damaged => 1, ChatType.Action => 8, ChatType.SpellPulse => 0,
        ChatType.KilledByAlb => 11, ChatType.KilledByMid => 13, ChatType.KilledByHib => 12,
        _ => 0,
    };

    /// <summary>Retail's default Main tab (in front; window 58% opaque).</summary>
    public static readonly ChatTab Main = new("Main", new HashSet<ChatType>
    {
        ChatType.Say, ChatType.Send, ChatType.Group, ChatType.Guild, ChatType.Broadcast, ChatType.Help, ChatType.Chat,
        ChatType.Advise, ChatType.Officer, ChatType.Alliance, ChatType.BattleGroup, ChatType.BattleGroupLeader,
        ChatType.Staff, ChatType.LookingForGroup, ChatType.Trade,
    }, new Dictionary<ChatType, int>(), 0.58f, ChatMode.Say);

    /// <summary>Retail's default Combat tab (window 38% opaque). Neither tab shows Others' Combat or Spell Pulse.</summary>
    public static readonly ChatTab Combat = new("Combat", new HashSet<ChatType>
    {
        ChatType.Emote, ChatType.Staff, ChatType.Spell, ChatType.YouHit, ChatType.System, ChatType.Items, ChatType.Merchant,
        ChatType.YourDeath, ChatType.OthersDeath, ChatType.ResistsChanged, ChatType.SpellExpires, ChatType.Loot,
        ChatType.SpellResisted, ChatType.Important, ChatType.Damaged, ChatType.Action,
        ChatType.KilledByAlb, ChatType.KilledByMid, ChatType.KilledByHib,
    }, new Dictionary<ChatType, int> { [ChatType.Alliance] = 20, [ChatType.BattleGroup] = 10, [ChatType.Trade] = 24 },
        0.38f, ChatMode.Say);

    /// <summary>How long a chat line stays drawn while its window shows the newest lines.</summary>
    public const double LineSeconds = 90;

    /// <summary>The most wrapped lines a window keeps.</summary>
    public const int MaxLines = 2500;

    /// <summary>
    /// The type a line is shown as in chat, or null: not a chat line. Type 0 is System; a player's Help line is shown as
    /// Say; 0xCA/0xCB also go to chat as Say; the middle-of-screen-only types (200, 201) and the social windows' records
    /// (100) are never chat lines.
    /// </summary>
    public static ChatType? ShownAs(ChatMessage m)
    {
        if (m.Text.Length == 0)
            return null;
        return m.Type switch
        {
            ChatType.Unspecified => ChatType.System,
            ChatType.Help when m.FromPlayer => ChatType.Say,
            ChatType.ScreenCenterAndSystem or ChatType.ScreenCenterSmallerAndSystem => ChatType.Say,
            ChatType.ScreenCenter or ChatType.ScreenCenterSmaller or ChatType.SocialInterface => null,
            _ => m.Type,
        };
    }

    /// <summary>The middle-of-the-screen size for this line, or null: not shown there.</summary>
    public static CentreSize? Centre(ChatMessage m) => m.Type switch
    {
        ChatType.ScreenCenter or ChatType.ScreenCenterAndSystem => CentreSize.Large,
        ChatType.ScreenCenterSmaller or ChatType.ScreenCenterSmallerAndSystem => CentreSize.Small,
        _ => null,
    };

    /// <summary>
    /// The sender of a player's line ("@@"), after the type's own prefix ("[Party] ", "[Guild] "…): for Say, Send, Help,
    /// Emote and Staff up to the first space, otherwise up to ": " or "/". Null when not a player's line.
    /// </summary>
    public static string? Sender(ChatMessage m)
    {
        if (!m.FromPlayer)
            return null;
        string t = m.Text;
        if (t.StartsWith('[') && t.IndexOf("] ", System.StringComparison.Ordinal) is int close and > 0)
            t = t[(close + 2)..];
        int end = m.Type is ChatType.Say or ChatType.Send or ChatType.Help or ChatType.Emote or ChatType.Staff
            ? t.IndexOf(' ')
            : MinPositive(t.IndexOf(": ", System.StringComparison.Ordinal), t.IndexOf('/'));
        return end > 0 ? t[..end] : null;
    }

    private static int MinPositive(int a, int b) => a < 0 ? b : b < 0 ? a : System.Math.Min(a, b);

    /// <summary>The command plain text is sent with in each mode.</summary>
    public static string Command(ChatMode mode) => mode switch
    {
        ChatMode.Group => "/g", ChatMode.Guild => "/gu", ChatMode.Broadcast => "/b", ChatMode.Chat => "/chat",
        ChatMode.Officer => "/os", ChatMode.Alliance => "/as", ChatMode.BattleGroup => "/bc", _ => "/s",
    };

    // The commands that, followed by a space, switch the mode and keep it.
    private static readonly Dictionary<string, ChatMode> Switches = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["/s"] = ChatMode.Say, ["/say"] = ChatMode.Say, ["/g"] = ChatMode.Group, ["/group"] = ChatMode.Group,
        ["/gu"] = ChatMode.Guild, ["/guild"] = ChatMode.Guild, ["/c"] = ChatMode.Chat, ["/chat"] = ChatMode.Chat,
        ["/as"] = ChatMode.Alliance, ["/asend"] = ChatMode.Alliance, ["/os"] = ChatMode.Officer, ["/osend"] = ChatMode.Officer,
        ["/bc"] = ChatMode.BattleGroup, ["/bchat"] = ChatMode.BattleGroup,
    };

    /// <summary>
    /// What a typed line sends (a "/" command line; null for nothing) and the chat mode after it. Plain text goes out
    /// with the mode's command. /s, /g, /gu, /c, /as, /os, /bc (or their long forms) followed by a space switch the mode
    /// and keep it. "%t" / "%T" become <paramref name="target"/> (no target: left out), any other "%" becomes "*".
    /// </summary>
    public static (string? Send, ChatMode Mode) Typed(string line, ChatMode mode, string? target)
    {
        line = Substitute(line.TrimStart(), target);   // the end stays: "/g " (a trailing space) is a switch
        if (line.Trim().Length == 0)
            return (null, mode);
        if (!line.StartsWith('/'))
            return ($"{Command(mode)} {line.TrimEnd()}", mode);
        int space = line.IndexOf(' ');
        if (space > 0 && Switches.TryGetValue(line[..space], out ChatMode switched))
        {
            string text = line[(space + 1)..].Trim();
            return (text.Length > 0 ? $"{Command(switched)} {text}" : null, switched);
        }
        return (line.TrimEnd(), mode);
    }

    private static string Substitute(string text, string? target)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '%')
                sb.Append(text[i]);
            else if (i + 1 < text.Length && text[i + 1] is 't' or 'T')
            {
                sb.Append(target ?? "");
                i++;
            }
            else
                sb.Append('*');
        }
        return sb.ToString();
    }
}
