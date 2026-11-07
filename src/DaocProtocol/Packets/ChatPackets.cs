// SPDX-License-Identifier: GPL-3.0-or-later
//
// Chat and system messages (roadmap M6). The one server→client text channel is 0xAF Message;
// the one client→server text channel is also 0xAF (CommandHandler), which carries BOTH slash
// commands and chat — "say" is just the "&say" command (see WorldPackets.CommandRequest).
//
// Layout ported (field order and the chat-type enum, not code) from OpenDAoC-Core @ master,
// GPL-3.0 — GameServer/packets/Server/IPacketLib.cs (eChatType / eChatLoc) and
// PacketLib1127.cs (SendRawMessage, the 1.127 override). The 1.127 Message packet is far
// simpler than the 1.68 one: no session/header shorts, just a chat-type byte then the text.
// Verified against the RustDAoC 1.127e capture (tests/DaocProtocol.Tests/Fixtures): the 0xAF
// stream there carries chat-type bytes 0x00/0x10/0x12/0x1f/0x64/0xc9 followed by NUL-terminated
// text, exactly as parsed here.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// Chat channels and message categories, the first byte of a 0xAF Message packet
/// (OpenDAoC eChatType). Channels are what a player says/hears; the message types are the
/// game narrating (spells, combat, system). The high values (SocialInterface, ScreenCenter)
/// are UI control, not human-readable chat — see <see cref="ChatMessage.IsPlayerVisibleChat"/>.
/// </summary>
public enum ChatType : byte
{
    // Not in OpenDAoC's eChatType (which starts at CT_Say = 0x01), but the most common type on the
    // wire: the server writes 0x00 for generic system/MOTD/guild-startup/sit-stand/"saved" lines
    // (18 of the 84 0xAF packets in the 1.127e capture). Treated as a system line for the chat log.
    // See docs/open-questions.md.
    Unspecified = 0x00,

    // Chat channels.
    Say = 0x01,
    Send = 0x02,
    Group = 0x03,
    Guild = 0x04,
    Broadcast = 0x05,
    Emote = 0x06,
    Help = 0x07,
    Chat = 0x08,
    Advise = 0x09,
    Officer = 0x0a,
    Alliance = 0x0b,
    BattleGroup = 0x0c,
    BattleGroupLeader = 0x0d,
    Staff = 0x0f,
    LookingForGroup = 0x23,
    Trade = 0x24,

    // Message types (the game talking to you).
    Spell = 0x10,
    YouHit = 0x11,
    System = 0x12,
    Items = 0x13,
    Merchant = 0x14,
    YourDeath = 0x15,
    OthersDeath = 0x16,
    OthersCombat = 0x17,
    ResistsChanged = 0x18,
    SpellExpires = 0x19,
    Loot = 0x1a,
    SpellResisted = 0x1b,
    Important = 0x1c,
    Damaged = 0x1d,
    Action = 0x1e,
    SpellPulse = 0x1f,

    KilledByAlb = 0x20,
    KilledByMid = 0x21,
    KilledByHib = 0x22,

    // UI control, not chat text.
    SocialInterface = 0x64,
    ScreenCenter = 0xc8,
    ScreenCenterSmaller = 0xc9,
    ScreenCenterAndSystem = 0xca,
    ScreenCenterSmallerAndSystem = 0xcb,
}

/// <summary>
/// 0xAF Message (server → client): one line of chat or a system/combat message. Layout (1.127):
/// <c>[1 byte: ChatType][optional "@@" or "##" prefix][text ...][0x00]</c>. The "@@" prefix is
/// written only for <see cref="ChatType.Send"/> (tells, for the reply feature) and "##" for a
/// pop-up window; both are stripped here so the text is what the player reads, and
/// <see cref="Popup"/> keeps the second: an NPC's words to the player (<c>SayTo</c>, e.g. a teleporter's
/// destinations in [brackets], which the player clicks to whisper back).
/// </summary>
public readonly record struct ChatMessage(ChatType Type, string Text, bool Popup = false)
{
    public const byte Code = 0xAF;

    public static ChatMessage Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        var type = (ChatType)r.U8();
        string text = r.StringRestNulTerminated();

        // Strip the client-routing prefixes the server prepends for certain types.
        bool popup = text.StartsWith("##", System.StringComparison.Ordinal);
        if (popup || text.StartsWith("@@", System.StringComparison.Ordinal))
            text = text[2..];

        return new ChatMessage(type, text, popup);
    }

    /// <summary>
    /// True when this line belongs in the player-facing chat log: real chat channels and the
    /// game-narration message types. False for the UI-control types (SocialInterface carries
    /// structured strings like <c>"I,1,0,0,..."</c> for guild/social windows; the ScreenCenter
    /// family is the big centred banner), which must not be dumped into the text log.
    /// </summary>
    public bool IsPlayerVisibleChat => Type switch
    {
        ChatType.SocialInterface => false,
        ChatType.ScreenCenter => false,
        ChatType.ScreenCenterSmaller => false,
        ChatType.ScreenCenterAndSystem => false,
        ChatType.ScreenCenterSmallerAndSystem => false,
        _ => Text.Length > 0,
    };
}
